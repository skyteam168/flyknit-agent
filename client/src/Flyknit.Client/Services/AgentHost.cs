using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Client.Bridge;
using Flyknit.Client.Tools;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Memory;
using Flyknit.Core.Security;
using Flyknit.Core.Skills;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;

namespace Flyknit.Client.Services;

/// <summary>界面事件的出口（由 WebBridge 实现）。</summary>
public interface IHostEvents
{
    void Post(object evt);
}

/// <summary>
/// 客户端的核心编排：会话存储、Agent 循环、工具、策略、记忆、Skills、审计。
/// 与界面无关，界面通过 WebBridge 调用这里。
/// </summary>
public sealed class AgentHost : IDisposable
{
    /// <summary>发给模型的最大历史消息数（更早的消息不再发送）。</summary>
    private const int HistoryWindow = 60;

    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();
    private readonly Timer _configTimer;
    private CommandPolicy _policy = CommandPolicy.Default();

    public FlyknitServerClient Server { get; }
    public ConversationStore Store { get; }
    public MemoryStore Memory { get; }
    public SkillCatalog Skills { get; }
    public ToolRegistry Tools { get; }
    public AuditQueue Audit { get; }

    public bool Connected { get; private set; }
    public string ServerMessage { get; private set; } = "";
    public string ModelName { get; private set; } = "";

    public event Action? StatusChanged;

    /// <summary>有运行中的任务或等待确认时变化，悬浮球据此显示状态。</summary>
    public event Action<int>? ActiveRunsChanged;

    public AgentHost(AppSettings settings)
    {
        _settings = settings;
        Server = new FlyknitServerClient(new HttpClient(), settings.ServerUrl, settings.DeviceToken);
        Store = new ConversationStore(AppPaths.Database);
        Memory = new MemoryStore(AppPaths.Memory);
        Skills = new SkillCatalog().AddRoot(AppPaths.OrgSkills, isOrganization: true).AddRoot(AppPaths.Skills);
        Tools = ToolRegistry.CreateDefault().Add(new OpenAppTool(() => _settings.AppAliases));
        Audit = new AuditQueue(Server);
        _configTimer = new Timer(_ => _ = RefreshConfigAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync()
    {
        Memory.EnsureDefaults();
        Directory.CreateDirectory(AppPaths.Skills);
        Skills.Refresh();
        await Task.Run(() => Store.PurgeExpired());
        await RefreshConfigAsync();
    }

    /// <summary>从服务端拉取策略与模型信息，失败时 1 分钟后重试，成功后每 10 分钟刷新。</summary>
    public async Task RefreshConfigAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var config = await Server.GetConfigAsync(cts.Token);
            if (config.Policy is not null)
            {
                _policy = new CommandPolicy(config.Policy);
            }
            var agent = config.Scenes.FirstOrDefault(s => s.Scene == Scenes.Agent);
            Connected = true;
            ServerMessage = agent is { Available: true } ? "" : "服务端尚未配置模型";
            ModelName = agent?.ModelName ?? "";
            _configTimer.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            Connected = false;
            ServerMessage = ex.Message;
            Log.Warn("拉取服务端配置失败", ex);
            _configTimer.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
        }
        StatusChanged?.Invoke();
    }

    public bool IsRunning(string conversationId) => _runs.ContainsKey(conversationId);

    public void Stop(string conversationId)
    {
        if (_runs.TryGetValue(conversationId, out var cts))
        {
            cts.Cancel();
        }
    }

    /// <summary>发送一条用户消息并在后台运行，过程通过 events 推送给界面。</summary>
    public void Send(string conversationId, string text, IReadOnlyList<Attachment> attachments, string uiLanguage, IConfirmationHandler confirm, IHostEvents events)
    {
        var conv = Store.Get(conversationId) ?? throw new InvalidOperationException("会话不存在");
        var cts = new CancellationTokenSource();
        if (!_runs.TryAdd(conversationId, cts))
        {
            throw new InvalidOperationException("该对话正在处理中");
        }
        ActiveRunsChanged?.Invoke(_runs.Count);
        _ = Task.Run(() => RunAsync(conv, text, attachments, uiLanguage, confirm, events, cts));
    }

    private async Task RunAsync(Conversation conv, string text, IReadOnlyList<Attachment> attachments, string uiLanguage, IConfirmationHandler confirm, IHostEvents events, CancellationTokenSource cts)
    {
        var id = conv.Id;
        var observer = new RunObserver(id, events);
        try
        {
            var user = ChatMessage.User(text, attachments);
            Store.AddMessages(id, new[] { user });
            var needsTitle = conv.TitleSource == "auto" && string.IsNullOrWhiteSpace(conv.Title);
            if (needsTitle)
            {
                // 先用问题开头作为临时标题，AI 总结出来后再替换
                var draft = text.Trim().Replace('\n', ' ');
                draft = draft.Length > 24 ? draft[..24] + "…" : draft;
                if (draft.Length == 0 && attachments.Count > 0)
                {
                    draft = attachments[0].FileName;
                }
                if (draft.Length > 0 && Store.SetAutoTitle(id, draft) && Store.Get(id) is { } drafted)
                {
                    events.Post(new { type = "conversation.updated", conversation = ConversationDto.From(drafted) });
                }
            }

            var promptBuilder = new PromptBuilder(Memory, Skills);
            var prompt = promptBuilder.Build(new PromptContext
            {
                Mode = conv.Mode,
                UiLanguage = uiLanguage,
                TranslateFrom = conv.TranslateFrom,
                TranslateTo = conv.TranslateTo,
            });

            var history = new List<ChatMessage> { ChatMessage.System(prompt) };
            if (conv.Mode == ConversationMode.Translate)
            {
                history.Add(user); // 翻译不需要上下文，避免把上一段译文混进来
            }
            else
            {
                history.AddRange(TrimHistory(Store.GetMessages(id)));
            }

            var scene = conv.Mode switch
            {
                ConversationMode.Translate => Scenes.Translate,
                ConversationMode.Chat => Scenes.Chat,
                _ => Scenes.Agent,
            };
            var ctx = new ToolContext
            {
                Policy = _policy,
                ConversationId = id,
                Memory = Memory,
                Skills = Skills,
                Deleter = new RecycleBinDeleter(),
            };

            var loop = new AgentLoop(Server, Tools, confirm, Audit);
            var result = await loop.RunAsync(history, scene, ctx, observer, useTools: conv.Mode == ConversationMode.Agent, cts.Token, conv.ModelId);
            Store.AddMessages(id, result.NewMessages);

            events.Post(new { type = "chat.done", conversationId = id, stopReason = result.StopReason.ToString(), modelName = result.ModelName });

            if (needsTitle && result.StopReason != AgentStopReason.Cancelled)
            {
                await SummarizeTitleAsync(id, text, result, uiLanguage, events);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"对话 {id} 运行失败", ex);
            events.Post(new { type = "chat.error", conversationId = id, message = ex is GatewayException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}" });
        }
        finally
        {
            _runs.TryRemove(id, out _);
            cts.Dispose();
            ActiveRunsChanged?.Invoke(_runs.Count);
        }
    }

    private async Task SummarizeTitleAsync(string id, string text, AgentRunResult result, string uiLanguage, IHostEvents events)
    {
        try
        {
            var answer = result.NewMessages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";
            var generator = new TitleGenerator(Server);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var title = await generator.GenerateAsync(text, answer, uiLanguage, cts.Token);
            if (string.IsNullOrWhiteSpace(title))
            {
                Log.Warn($"会话 {id} 标题生成失败：{generator.LastError}");
                return; // 保留临时标题
            }
            if (Store.SetAutoTitle(id, title) && Store.Get(id) is { } updated)
            {
                events.Post(new { type = "conversation.updated", conversation = ConversationDto.From(updated) });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"会话 {id} 标题生成异常", ex);
        }
    }

    private List<ClientModel>? _models;
    private DateTime _modelsAt;

    /// <summary>输入框可选的模型列表，缓存 5 分钟。</summary>
    public async Task<List<ClientModel>> GetModelsAsync(bool refresh = false)
    {
        if (!refresh && _models is not null && DateTime.Now - _modelsAt < TimeSpan.FromMinutes(5))
        {
            return _models;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _models = await Server.GetModelsAsync(cts.Token);
            _modelsAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            Log.Warn("获取模型列表失败", ex);
            _models ??= new List<ClientModel>();
        }
        return _models;
    }

    /// <summary>只保留最近的消息，并保证从一条用户消息开始，避免工具消息失去对应的调用。</summary>
    private static IEnumerable<ChatMessage> TrimHistory(List<ChatMessage> messages)
    {
        if (messages.Count <= HistoryWindow)
        {
            return messages;
        }
        var start = messages.Count - HistoryWindow;
        while (start < messages.Count && messages[start].Role != ChatRole.User)
        {
            start++;
        }
        return messages.Skip(start);
    }

    public void Dispose()
    {
        foreach (var cts in _runs.Values)
        {
            cts.Cancel();
        }
        _configTimer.Dispose();
        Audit.FlushAsync().Wait(TimeSpan.FromSeconds(3));
        Audit.Dispose();
    }

    /// <summary>把一次运行中的事件转成界面消息。</summary>
    private sealed class RunObserver : IAgentObserver
    {
        private readonly string _id;
        private readonly IHostEvents _events;

        public RunObserver(string id, IHostEvents events)
        {
            _id = id;
            _events = events;
        }

        public void OnContent(string delta) => _events.Post(new { type = "chat.delta", conversationId = _id, text = delta });

        public void OnReasoning(string delta) => _events.Post(new { type = "chat.reasoning", conversationId = _id, text = delta });

        public void OnAssistantMessage(ChatMessage message) =>
            _events.Post(new { type = "chat.message", conversationId = _id, message = MessageDto.From(message) });

        public void OnToolMessage(ChatMessage message) =>
            _events.Post(new { type = "chat.message", conversationId = _id, message = MessageDto.From(message) });

        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) => _events.Post(new
        {
            type = "tool.started",
            conversationId = _id,
            callId = call.Id,
            name = call.Name,
            summary = ToolDetail.From(call.ArgumentsJson) is { Length: > 0 } detail ? detail : summary,
            risk = decision.Level switch { RiskLevel.Blocked => "blocked", RiskLevel.Confirm => "confirm", _ => "auto" },
        });

        public void OnToolFinished(ToolCall call, ToolResult result, string decision) => _events.Post(new
        {
            type = "tool.finished",
            conversationId = _id,
            callId = call.Id,
            ok = result.Ok,
            output = result.Output,
            decision,
        });

        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) => _events.Post(new
        {
            type = "plan.updated",
            conversationId = _id,
            plan = plan.Select(p => new { step = p.Step, status = p.Status }).ToList(),
        });
    }
}
