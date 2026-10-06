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
using Flyknit.Core.Context;
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
    /// <summary>发给模型的最大历史消息数（兜底；正常情况下由上下文压缩控制长度）。</summary>
    private const int HistoryWindow = 300;

    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new();
    private readonly Timer _configTimer;
    private CommandPolicy _policy = CommandPolicy.Default();

    public FlyknitServerClient Server { get; }
    public ConversationStore Store { get; }
    public MemoryStore Memory { get; }
    public SkillCatalog Skills { get; }
    public SkillService SkillManager { get; }
    public ToolRegistry Tools { get; }
    public AuditQueue Audit { get; }

    /// <summary>上报服务端的同时，把拦截与放行记录存一份在本机（用量与安全面板用）。</summary>
    private readonly RecordingAuditSink _auditSink;
    public ApprovalStore Approvals { get; }
    public EpisodeStore Episodes { get; }
    public ScheduleRunner Scheduler { get; }

    /// <summary>界面就绪后由 WebBridge 挂上，定时任务用它推送事件和请求确认。</summary>
    public IHostEvents? Events { get; private set; }
    public IConfirmationHandler? Confirm { get; private set; }

    public string UiLanguage => _settings.ResolveUiLanguage();

    /// <summary>界面（WebBridge）准备好之后调用一次。</summary>
    public void AttachUi(IHostEvents events, IConfirmationHandler confirm)
    {
        Events = events;
        Confirm = confirm;
    }

    /// <summary>复盘沉淀出的技能放在个人技能目录下的 learned 子目录。</summary>
    public static string LearnedSkills => AppPaths.LearnedSkills;

    /// <summary>最近一次得知的模型上下文长度。</summary>
    private int _contextLength;

    public bool Connected { get; private set; }
    public string ServerMessage { get; private set; } = "";
    public string ModelName { get; private set; } = "";

    public event Action? StatusChanged;

    /// <summary>有运行中的任务或等待确认时变化，悬浮球据此显示状态。</summary>
    public event Action<int>? ActiveRunsChanged;

    /// <summary>技能目录发生变化（安装、卸载、手动拷入）。</summary>
    public event Action? SkillsChanged;

    public AgentHost(AppSettings settings)
    {
        _settings = settings;
        Server = new FlyknitServerClient(new HttpClient(), settings.ServerUrl, settings.DeviceToken);
        Store = new ConversationStore(AppPaths.Database);
        Memory = new MemoryStore(AppPaths.Memory);
        Skills = new SkillCatalog()
            // 机器级目录优先：IT 统一预装的技能对所有 Windows 用户可见
            .AddRoot(AppPaths.MachineSkills, SkillSource.Organization)
            .AddRoot(AppPaths.OrgSkills, SkillSource.Organization)
            .AddRoot(AppPaths.LearnedSkills, SkillSource.Learned)
            .AddRoot(AppPaths.Skills);
        SkillManager = new SkillService(Skills, settings, Server, () => _policy);
        Tools = ToolRegistry.CreateDefault().Add(new OpenAppTool(() => _settings.AppAliases));
        Audit = new AuditQueue(Server);
        _auditSink = new RecordingAuditSink(Audit, Store);
        Approvals = new ApprovalStore(AppPaths.ApprovalRules);
        Episodes = new EpisodeStore(AppPaths.Memory);
        Scheduler = new ScheduleRunner(this);
        RunFinished += Scheduler.OnRunFinished;
        _configTimer = new Timer(_ => _ = RefreshConfigAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task InitializeAsync()
    {
        Memory.EnsureDefaults();
        _settings.EnsureWorkspaces();
        Directory.CreateDirectory(AppPaths.Skills);
        Directory.CreateDirectory(AppPaths.OrgSkills);
        Directory.CreateDirectory(AppPaths.LearnedSkills);
        SkillManager.Refresh();
        Skills.Changed += () => SkillsChanged?.Invoke();
        Skills.StartWatching();
        await Task.Run(() => Store.PurgeExpired());
        await RefreshConfigAsync();
        Scheduler.Start();
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
            if (agent is { ContextLength: > 0 } && _contextLength == 0)
            {
                _contextLength = agent.ContextLength;
            }
            Connected = true;
            ServerMessage = agent is { Available: true } ? "" : "服务端尚未配置模型";
            ModelName = agent?.ModelName ?? "";
            _configTimer.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
            // 同步公司技能库里管理员标记为必装的技能
            _ = SkillManager.SyncRequiredAsync(CancellationToken.None);
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
    /// <param name="messageId">界面生成的消息 ID，保证界面与数据库中的 ID 一致（编辑、重发时使用）。</param>
    public void Send(string conversationId, string text, IReadOnlyList<Attachment> attachments, string? messageId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events)
    {
        StartRun(conversationId, uiLanguage, confirm, events, _ => NewUserMessage(messageId, text, attachments));
    }

    /// <summary>重新生成最后一个问题的回答：删除最后一条用户消息之后的内容，再运行一次。</summary>
    public void Regenerate(string conversationId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events)
    {
        StartRun(conversationId, uiLanguage, confirm, events, id =>
        {
            var last = Store.GetMessages(id).LastOrDefault(m => m.Role == ChatRole.User)
                       ?? throw new InvalidOperationException("没有可以重新生成的问题");
            Store.DeleteMessagesFrom(id, last.Id, inclusive: false);
            return null;
        });
    }

    /// <summary>编辑某条用户消息后重新发送：删除这条消息及之后的所有内容，用新内容重新提问（保留原附件）。</summary>
    public void EditAndResend(string conversationId, string messageId, string text, string? newMessageId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events)
    {
        StartRun(conversationId, uiLanguage, confirm, events, id =>
        {
            var original = Store.GetMessages(id).FirstOrDefault(m => m.Id == messageId && m.Role == ChatRole.User)
                           ?? throw new InvalidOperationException("找不到要编辑的消息");
            Store.DeleteMessagesFrom(id, messageId, inclusive: true);
            return NewUserMessage(newMessageId, text, original.Attachments);
        });
    }

    private static readonly System.Text.RegularExpressions.Regex SafeId = new("^[A-Za-z0-9_-]{8,64}$");

    private static ChatMessage NewUserMessage(string? id, string text, IEnumerable<Attachment> attachments) => new()
    {
        Id = id is not null && SafeId.IsMatch(id) ? id : Guid.NewGuid().ToString("N"),
        Role = ChatRole.User,
        Content = text,
        Attachments = attachments.ToList(),
    };

    /// <summary>占用会话的运行槽位后，在后台准备消息并运行，保证同一会话同时只有一个任务。</summary>
    private void StartRun(string conversationId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events, Func<string, ChatMessage?> prepare)
    {
        var conv = Store.Get(conversationId) ?? throw new InvalidOperationException("会话不存在");
        var cts = new CancellationTokenSource();
        if (!_runs.TryAdd(conversationId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException("该对话正在处理中");
        }
        ChatMessage? newUser;
        try
        {
            newUser = prepare(conversationId);
        }
        catch
        {
            _runs.TryRemove(conversationId, out _);
            cts.Dispose();
            throw;
        }
        ActiveRunsChanged?.Invoke(_runs.Count);
        _ = Task.Run(() => RunAsync(conv, newUser, uiLanguage, confirm, events, cts));
    }

    private async Task RunAsync(Conversation conv, ChatMessage? newUser, string uiLanguage, IConfirmationHandler confirm, IHostEvents events, CancellationTokenSource cts)
    {
        var id = conv.Id;
        var observer = new RunObserver(id, events);
        try
        {
            if (newUser is not null)
            {
                Store.AddMessages(id, new[] { newUser });
            }
            var stored = Store.GetMessages(id);
            var user = stored.LastOrDefault(m => m.Role == ChatRole.User) ?? throw new InvalidOperationException("没有可以回答的问题");
            var text = user.Content;
            var attachments = user.Attachments;

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

            var workspace = _settings.ResolveWorkspace(conv.Workspace);
            var promptBuilder = new PromptBuilder(Memory, Skills, Episodes);
            var prompt = promptBuilder.Build(new PromptContext
            {
                Mode = conv.Mode,
                UiLanguage = uiLanguage,
                TranslateFrom = conv.TranslateFrom,
                TranslateTo = conv.TranslateTo,
                Workspace = workspace,
                Permission = conv.Permission,
                Query = text,
            });

            var scene = conv.Mode switch
            {
                ConversationMode.Translate => Scenes.Translate,
                ConversationMode.Chat => Scenes.Chat,
                _ => Scenes.Agent,
            };

            // 短期记忆：之前压缩过的部分用摘要代替，只发送摘要之后的消息
            var summary = conv.Summary;
            var recent = (IEnumerable<ChatMessage>)stored;
            var uptoIndex = conv.SummaryUpto is null ? -1 : stored.FindIndex(m => m.Id == conv.SummaryUpto);
            if (uptoIndex >= 0)
            {
                recent = stored.Skip(uptoIndex + 1);
            }
            else
            {
                summary = null; // 摘要覆盖的消息已不存在（编辑或重新生成过），摘要作废
            }

            ContextManager? context = null;
            var history = new List<ChatMessage>();
            if (conv.Mode == ConversationMode.Translate)
            {
                history.Add(ChatMessage.System(prompt));
                history.Add(user); // 翻译不需要上下文，避免把上一段译文混进来
            }
            else
            {
                context = new ContextManager(Server, prompt, summary, _contextLength) { Scene = scene, ModelId = conv.ModelId };
                context.Progress += p => events.Post(new
                {
                    type = "context.compacting",
                    conversationId = id,
                    phase = p.Phase,
                    percent = p.Percent,
                    messages = p.MessagesCompacted,
                });
                context.Compacted += info =>
                {
                    Store.SetSummary(id, info.Summary, info.UptoMessageId);
                    Log.Info($"对话 {id} 上下文已压缩：{info.MessagesCompacted} 条消息，{info.TokensBefore} → {info.TokensAfter} tokens");
                    events.Post(new { type = "context.compacted", conversationId = id, uptoMessageId = info.UptoMessageId, tokensBefore = info.TokensBefore, tokensAfter = info.TokensAfter });
                };
                history.Add(ChatMessage.System(context.SystemPrompt));
                var window = TrimHistory(recent.ToList()).ToList();
                if (window.Count > 0 && window[0].Role != ChatRole.User)
                {
                    history.Add(ChatMessage.User(ContextManager.ContinueMarker));
                }
                history.AddRange(window);
            }

            var ctx = new ToolContext
            {
                Policy = _policy,
                ConversationId = id,
                Workspace = workspace,
                Permission = conv.Permission,
                Memory = Memory,
                Episodes = Episodes,
                Skills = Skills,
                Deleter = new RecycleBinDeleter(),
            };

            var loop = new AgentLoop(Server, Tools, confirm, _auditSink, approvals: Approvals);
            var result = await loop.RunAsync(history, scene, ctx, observer, useTools: conv.Mode == ConversationMode.Agent, cts.Token, conv.ModelId, context);
            // 把本轮产出的文件和执行链路挂到最后一条回答上，重开会话时还能查
            var answer = result.NewMessages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (observer.Outputs.Count > 0)
            {
                answer?.Outputs.AddRange(observer.Outputs);
            }
            if (answer is not null && result.Trace is { } trace)
            {
                answer.TraceJson = Bridge.TraceDto.Serialize(trace, result.StopReason.ToString());
            }
            Store.AddMessages(id, result.NewMessages);
            if (context is { ContextLength: > 0 })
            {
                _contextLength = context.ContextLength;
            }

            events.Post(new
            {
                type = "chat.done",
                conversationId = id,
                stopReason = result.StopReason.ToString(),
                modelName = result.ModelName,
                usage = result.Usage is { } u ? new { promptTokens = u.PromptTokens, completionTokens = u.CompletionTokens } : null,
            });
            RunFinished?.Invoke(new RunFinishedInfo(id, Store.Get(id)?.Title ?? "", LastAnswer(result.NewMessages), result.StopReason));

            if (needsTitle && result.StopReason != AgentStopReason.Cancelled)
            {
                await SummarizeTitleAsync(id, text, result, uiLanguage, events);
            }

            // 长期记忆：办事任务结束后在后台复盘，提炼偏好、经验和可复用的做法。
            // 被用户中途停止的任务同样复盘——用户喊停往往说明做错了方向，这是最该记下来的教训（只记教训，不记成功经验）。
            if (_settings.EnableLearning && conv.Mode == ConversationMode.Agent
                && Reflector.ShouldReflect(result.NewMessages, 0))
            {
                var previous = stored.Take(stored.Count - 1).LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";
                _ = Task.Run(() => ReflectAsync(conv, workspace, text, previous, result.NewMessages, result.StopReason.ToString(), 0, uiLanguage, events));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"对话 {id} 运行失败", ex);
            events.Post(new { type = "chat.error", conversationId = id, message = ex is GatewayException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}" });
            RunFinished?.Invoke(new RunFinishedInfo(id, Store.Get(id)?.Title ?? "", ex.Message, null));
        }
        finally
        {
            _runs.TryRemove(id, out _);
            cts.Dispose();
            ActiveRunsChanged?.Invoke(_runs.Count);
        }
    }

    /// <summary>一次运行结束（用于系统通知）。StopReason 为 null 表示出错。</summary>
    public event Action<RunFinishedInfo>? RunFinished;

    private static string LastAnswer(IReadOnlyList<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Trim().Length > 0)?.Content.Trim() ?? "";

    private async Task ReflectAsync(Conversation conv, string workspace, string request, string previousAnswer, IReadOnlyList<ChatMessage> messages, string stopReason, int feedback, string uiLanguage, IHostEvents events)
    {
        try
        {
            var reflector = new Reflector(Server, Memory, Episodes, LearnedSkills) { Scene = Scenes.Agent, ModelId = conv.ModelId };
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var report = await reflector.ReflectAsync(new ReflectionInput
            {
                ConversationId = conv.Id,
                Workspace = workspace,
                UserRequest = request,
                PreviousAnswer = previousAnswer,
                Messages = messages,
                StopReason = stopReason,
                Feedback = feedback,
                UiLanguage = uiLanguage,
            }, cts.Token);
            if (report is null)
            {
                Log.Warn($"对话 {conv.Id} 复盘失败：{reflector.LastError}");
                return;
            }
            Log.Info($"对话 {conv.Id} 复盘完成：新增记忆 {report.Added.Count} 条，历史任务 {(report.Episode is null ? "无" : report.Episode.Title)}，技能 {report.SkillName ?? "无"}");
            if (report.SkillName is not null)
            {
                SkillManager.Refresh();
            }
            if (report.Added.Count > 0 || report.SkillName is not null)
            {
                events.Post(new
                {
                    type = "memory.learned",
                    conversationId = conv.Id,
                    items = report.Added.Select(a => new { kind = a.Kind.ToString().ToLowerInvariant(), text = a.Text }).ToList(),
                    skill = report.SkillName,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"对话 {conv.Id} 复盘异常", ex);
        }
    }

    /// <summary>
    /// 用户对回答点赞或点踩：保存评价，同步到历史任务；点踩时复盘这一轮，记下教训。
    /// </summary>
    public void Feedback(string conversationId, string messageId, int? value, string uiLanguage, IHostEvents events)
    {
        Store.SetFeedback(messageId, value);
        if (value is null)
        {
            return;
        }
        Episodes.SetFeedback(conversationId, value.Value);
        if (value.Value >= 0 || !_settings.EnableLearning || Store.Get(conversationId) is not { } conv)
        {
            return;
        }
        var messages = Store.GetMessages(conversationId);
        var index = messages.FindIndex(m => m.Id == messageId);
        var userIndex = index < 0 ? -1 : messages.FindLastIndex(index, m => m.Role == ChatRole.User);
        if (userIndex < 0)
        {
            return;
        }
        var nextUser = messages.FindIndex(userIndex + 1, m => m.Role == ChatRole.User);
        var turn = messages.Skip(userIndex + 1).Take((nextUser < 0 ? messages.Count : nextUser) - userIndex - 1).ToList();
        var previous = messages.Take(userIndex).LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";
        var workspace = _settings.ResolveWorkspace(conv.Workspace);
        _ = Task.Run(() => ReflectAsync(conv, workspace, messages[userIndex].Content, previous, turn, "Completed", -1, uiLanguage, events));
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
        Scheduler.Dispose();
        Skills.Dispose();
        Audit.FlushAsync().Wait(TimeSpan.FromSeconds(3));
        Audit.Dispose();
    }

    /// <summary>
    /// 审计出口：危险命令拦截、用户放行和拒绝都记在本机一份，同时照常上报服务端。
    /// 自动执行的只读操作不记（量大且没有安全意义）。
    /// </summary>
    private sealed class RecordingAuditSink : IAuditSink
    {
        private readonly IAuditSink _inner;
        private readonly ConversationStore _store;

        public RecordingAuditSink(IAuditSink inner, ConversationStore store)
        {
            _inner = inner;
            _store = store;
        }

        public void Record(AuditEntry entry)
        {
            _inner.Record(entry);
            if (entry.Decision is not ("blocked" or "approved" or "remembered" or "rejected"))
            {
                return;
            }
            try
            {
                _store.AddSecurityEvent(new SecurityEvent
                {
                    ConversationId = entry.ConversationId,
                    Scene = entry.Scene,
                    Tool = entry.ToolName,
                    Detail = ToolDetail.From(entry.Arguments),
                    Decision = entry.Decision,
                    Reason = entry.Decision == "blocked" ? entry.Summary : "",
                    CreatedAt = entry.OccurredAt,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("保存安全记录失败", ex);
            }
        }
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

        public void OnContextCompacted(CompactionInfo info)
        {
            // 由 ContextManager.Compacted 事件统一处理（保存摘要并通知界面）
        }

        /// <summary>本轮产出的文件，按出现顺序去重。</summary>
        public List<string> Outputs { get; } = new();

        public void OnNotice(string text) => _events.Post(new { type = "chat.notice", conversationId = _id, text });

        public void OnOutputsProduced(IReadOnlyList<OutputFile> files)
        {
            foreach (var f in files)
            {
                if (!Outputs.Contains(f.Path, StringComparer.OrdinalIgnoreCase))
                {
                    Outputs.Add(f.Path);
                }
            }
            _events.Post(new
            {
                type = "files.produced",
                conversationId = _id,
                files = files.Select(f => Bridge.OutputFileDto.From(f.Path)).ToList(),
            });
        }

        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) => _events.Post(new
        {
            type = "plan.updated",
            conversationId = _id,
            plan = plan.Select(p => new { step = p.Step, status = p.Status }).ToList(),
        });
    }
}

public sealed record RunFinishedInfo(string ConversationId, string Title, string Answer, AgentStopReason? StopReason);
