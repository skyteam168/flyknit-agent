using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Experts;
using Flyknit.Core.Experts.Delegation;
using Flyknit.Core.Gateway;
using Flyknit.Core.Library;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;

namespace Flyknit.Client.Services;

/// <summary>
/// 派遣专家在员工端的接线：给 <see cref="ExpertDispatcher"/> 准备专家的运行环境（提示词、工具、模型），
/// 把子任务的进展推给界面，开关被关掉时取消正在跑的子任务，启动时如实处理上次没跑完的。
/// </summary>
public sealed partial class AgentHost
{
    private ExpertDispatcher? _dispatcher;
    private DelegationHost? _delegationHost;

    /// <summary>派遣开关：IT 在后台开了才有（没下发时视为关）。</summary>
    private bool DelegationEnabled() => Security.On(SecuritySettings.ExpertDelegation, fallback: false);

    private ExpertDispatcher Dispatcher
    {
        get
        {
            if (_dispatcher is null)
            {
                _delegationHost = new DelegationHost(this);
                _dispatcher = new ExpertDispatcher(_delegationHost, Server, Store, _delegationHost, Experts.Get, DelegationEnabled);
            }
            return _dispatcher;
        }
    }

    /// <summary>
    /// 这一轮给主助手的候选专家：只在普通的办事对话里、开关开着时才有（专家对话里不派遣别的专家）。
    /// 返回提示词段落和要加的工具；没有候选时两样都没有，主助手和以前一模一样。
    /// </summary>
    private (string Prompt, AskExpertTool? Tool) DelegationFor(Conversation conv, Expert? expert, string query,
        IReadOnlyDictionary<string, double>? semantic)
    {
        if (conv.Mode != ConversationMode.Agent || expert is not null || !DelegationEnabled())
        {
            return ("", null);
        }
        var candidates = ExpertCandidates.Pick(Experts.All, query, semantic);
        if (candidates.Count == 0)
        {
            return ("", null);
        }
        return (ExpertCandidates.PromptSection(candidates), new AskExpertTool(Dispatcher, candidates));
    }

    /// <summary>挑候选专家用的文字：员工这句话，加上一句之前说的（「继续」「那 B 车间呢」这种要靠上一句）。</summary>
    private static string DelegationQuery(IReadOnlyList<ChatMessage> stored, string text)
    {
        var previous = stored.Where(m => m.Role == ChatRole.User).Reverse().Skip(1).FirstOrDefault()?.Content ?? "";
        if (previous.Length > 200)
        {
            previous = previous[..200];
        }
        return previous.Length > 0 ? $"{text}\n{previous}" : text;
    }

    /// <summary>语义匹配要比的额外文字：技能，开关开着时再加上专家名片。</summary>
    private IReadOnlyDictionary<string, string> SemanticExtras()
    {
        var extras = new Dictionary<string, string>(Skills.SemanticTexts());
        if (DelegationEnabled())
        {
            foreach (var (key, value) in ExpertCandidates.SemanticTexts(Experts.All.Where(e => e.AllowDelegate)))
            {
                extras[key] = value;
            }
        }
        return extras;
    }

    /// <summary>开关被关掉：正在跑的子任务立刻取消（派遣模块自己也会在两秒内发现）。</summary>
    private void OnDelegationSwitchChanged()
    {
        if (!DelegationEnabled())
        {
            _dispatcher?.CancelAll("disabled");
        }
    }

    /// <summary>
    /// 上次退出时还没跑完的子任务：进程已经没了，没法接着跑，标成失败（interrupted），并通知服务端结束它们。
    /// </summary>
    private async Task RecoverInterruptedSubtasksAsync()
    {
        IReadOnlyList<SubtaskRecord> interrupted;
        try
        {
            interrupted = Store.MarkInterruptedSubtasks();
        }
        catch (Exception ex)
        {
            Log.Warn("检查上次没跑完的子任务失败", ex);
            return;
        }
        foreach (var record in interrupted)
        {
            Log.Info($"子任务 {record.Id}（{record.ExpertName}）在上次退出时没有完成，已标记为失败");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await Server.CancelAsync(record.Id, "parent", cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warn($"通知服务端结束子任务 {record.Id} 失败（服务端到时限后会自己结束它）", ex);
            }
        }
    }

    /// <summary>界面要的子任务列表（不含执行记录）。</summary>
    public IReadOnlyList<object> SubtasksOf(string conversationId) =>
        Store.SubtasksOf(conversationId).Select(r => SubtaskDto(r, withTrace: false)).ToList();

    public object? Subtask(string id) => Store.GetSubtask(id) is { } r ? SubtaskDto(r, withTrace: true) : null;

    /// <summary>和 docs/expert-delegation-contract.md 里的 SubtaskDto 一致。</summary>
    internal static object SubtaskDto(SubtaskRecord r, bool withTrace) => new
    {
        id = r.Id,
        callId = r.CallId,
        conversationId = r.ConversationId,
        expert = new { id = r.ExpertId, name = r.ExpertName, avatar = r.ExpertAvatar, version = r.ExpertVersion },
        task = r.Task,
        context = r.Context,
        status = r.Status,
        summary = r.Summary,
        findings = r.Findings.Select(f => new
        {
            id = f.Id,
            statement = f.Statement,
            evidence = f.Evidence.Select(e => new { @ref = e.Ref, kind = e.Kind, label = e.Label, check = e.Check }),
            verification = f.Verification,
            note = f.Note,
        }),
        openQuestions = r.OpenQuestions,
        nextSuggestions = r.NextSuggestions,
        artifacts = r.Artifacts.Select(a => new { path = a, name = Path.GetFileName(a), exists = File.Exists(a) }),
        confidence = r.Confidence,
        toolCalls = r.ToolCalls.Select(t => new { name = t.Name, count = t.Count, failed = t.Failed }),
        tokens = new { prompt = r.PromptTokens, completion = r.CompletionTokens, estimated = r.Estimated },
        startedAt = r.StartedAt,
        finishedAt = r.FinishedAt,
        durationMs = r.DurationMs,
        errorCode = r.ErrorCode,
        error = r.Error,
        trace = withTrace ? r.Trace : null,
    };

    /// <summary>派遣模块要的宿主能力，以及推给界面的事件。</summary>
    private sealed class DelegationHost : IExpertRunHost, IDelegationEvents
    {
        private readonly AgentHost _host;

        public DelegationHost(AgentHost host)
        {
            _host = host;
        }

        public ExpertRunSetup Prepare(Expert expert, string brief, ToolContext parent)
        {
            var persona = _host.PersonaForPrompt();
            var permission = (PermissionMode)Math.Min((int)parent.Permission, (int)ExpertRuntime.CapPermission(expert, parent.Permission));
            var prompt = new PromptBuilder(_host.Memory, _host.Skills, _host.Episodes).Build(new PromptContext
            {
                Tone = persona.Tone,
                CallName = persona.CallName,
                AssistantName = persona.AssistantName,
                Mode = ConversationMode.Agent,
                UiLanguage = _host._settings.ResolveUiLanguage(),
                Workspace = parent.Workspace,
                Permission = permission,
                Sandboxed = _host.Security.On(SecuritySettings.Sandbox),
                Query = brief,
                McpServers = ExpertRuntime.FilterMcp(expert, _host.Mcp.PromptInfo()),
                Expert = expert,
            });
            return new ExpertRunSetup
            {
                SystemPrompt = prompt,
                Tools = ExpertRuntime.FilterTools(_host.Tools, expert),
                ModelId = expert.ModelId,
            };
        }

        public AgentOptions Options(long maxTokens) => new()
        {
            MaxSteps = Math.Min(_host._settings.ResolveAgentMaxSteps(), _host.Dispatcher.Limits.MaxSteps),
            PlanGuidance = _host.Security.On(SecuritySettings.PlanGuidance),
            VerifyOutputs = _host.Security.On(SecuritySettings.VerifyOutputs),
            MaxRunTokens = maxTokens,
        };

        public string SerializeTrace(Trace trace, string stopReason) => Bridge.TraceDto.Serialize(trace, stopReason);

        public IConfirmationHandler Confirm => (IConfirmationHandler?)_host.Confirm ?? RejectAll.Instance;

        public IAuditSink Audit => _host._auditSink;

        public ApprovalStore Approvals => _host.Approvals;

        public IChatGateway Gateway => _host.Server;

        public bool IsRegisteredFile(string conversationId, string fullPath) =>
            _host.Library.Store.List(new LibraryQuery { ConversationId = conversationId, IncludeHidden = true, Limit = 5000 })
                .Any(i => string.Equals(Path.GetFullPath(i.Path), fullPath, StringComparison.OrdinalIgnoreCase));

        public void Started(SubtaskRecord r, int seq) => Post(new
        {
            type = "expert.started",
            conversationId = r.ConversationId,
            callId = r.CallId,
            subtaskId = r.Id,
            seq,
            expert = new { id = r.ExpertId, name = r.ExpertName, avatar = r.ExpertAvatar, version = r.ExpertVersion },
            task = r.Task.Length > 300 ? r.Task[..300] + "…" : r.Task,
            startedAt = r.StartedAt,
        });

        public void Step(SubtaskRecord r, int seq, string kind, string name, string summary, bool? ok) => Post(new
        {
            type = "expert.step",
            conversationId = r.ConversationId,
            callId = r.CallId,
            subtaskId = r.Id,
            seq,
            kind,
            name,
            summary = summary.Length > 200 ? summary[..200] + "…" : summary,
            ok,
        });

        public void Finished(SubtaskRecord r, int seq) => Post(new
        {
            type = "expert.finished",
            conversationId = r.ConversationId,
            callId = r.CallId,
            subtaskId = r.Id,
            seq,
            subtask = SubtaskDto(r, withTrace: false),
        });

        private void Post(object evt)
        {
            try
            {
                _host.Events?.Post(evt);
            }
            catch (Exception ex)
            {
                Log.Warn("推送专家进展失败", ex);
            }
        }
    }

    /// <summary>界面还没准备好时专家请求确认：一律拒绝（不能没人看着就放行）。</summary>
    private sealed class RejectAll : IConfirmationHandler
    {
        public static readonly RejectAll Instance = new();

        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.Reject);
    }
}
