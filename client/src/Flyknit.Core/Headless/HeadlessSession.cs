using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Skills;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Headless;

/// <summary>
/// 命令行和 IDE（ACP）共用的运行环境：服务端下发的安全设置、命令策略、IT 工具规则，员工确认过的自动执行规则，审计出口。
/// 和员工端是同一套东西（同一个设备令牌拉的同一份配置），所以闸门一个不少：
/// blocked_patterns → CommandAnalyzer 定级 → 权限档 + 工作区 → 自动执行规则；高危永不自动放行。
/// </summary>
public sealed class HeadlessEnvironment
{
    public required IChatGateway Gateway { get; init; }
    public SecuritySettings Security { get; init; } = new();
    public CommandPolicy Policy { get; init; } = CommandPolicy.Default();
    public ToolRules ToolRules { get; init; } = ToolRules.None;

    /// <summary>员工确认过的「以后自动执行」规则（和员工端同一个文件）。为空时不读也不记。</summary>
    public ApprovalStore? Approvals { get; init; }

    public IAuditSink Audit { get; init; } = new NullAuditSink();

    /// <summary>审计里记的「谁」：cli（flyknit -p）或 ide（ACP）。员工在聊天窗口里做的是 main。</summary>
    public string Actor { get; init; } = "cli";

    public string UiLanguage { get; init; } = "zh-CN";

    /// <summary>员工在员工端打开了「深度思考」：办事时强制开思考（和员工端一样）。</summary>
    public bool DeepThinking { get; init; }

    /// <summary>员工端的技能（只读：命令行不装、不删技能）。为空时没有技能工具。</summary>
    public SkillCatalog? Skills { get; init; }

    public int ContextLength { get; init; }

    /// <summary>太长的工具结果存到这里（见 ToolOutputSpill）。</summary>
    public string? ToolOutputDir { get; init; }

    /// <summary>删除方式。为空时按安全中心「删除进回收站」决定（开着用回收站，见 <see cref="ShellRecycleBin"/>）。</summary>
    public IFileDeleter? Deleter { get; init; }
}

/// <summary>命令行 / IDE 接入的总开关检查。</summary>
public static class HeadlessAccess
{
    /// <summary>不能用时返回给人看的原因；能用返回 null。服务端没下发这一项时按关处理（默认关）。</summary>
    public static string? Problem(SecuritySettings security) =>
        security.On(SecuritySettings.CliAccess, fallback: false)
            ? null
            : "IT 没有开启「命令行 / IDE 接入」（管理后台 → 安全中心）。开启前 flyknit 命令行和 IDE 插件都不能执行任务";
}

/// <summary>
/// 一个无界面的办事会话：命令行一次 -p 是一个，IDE 里每个会话是一个（多轮对话的历史留在内存里，不进员工端的历史记录）。
///
/// 工具：文件、命令、计划，开了开发者工具再加 grep / multi_edit / git_*，有技能时加 load_skill / search_skills。
/// 不带长期记忆（命令行里读写员工的个人记忆不合适）、浏览器、连接器、子代理、向员工提问——这些都需要员工端界面。
/// </summary>
public sealed class HeadlessSession
{
    /// <summary>无界面会话不提供的工具：要么写员工的个人数据，要么得有界面。</summary>
    public static readonly IReadOnlySet<string> Excluded = new HashSet<string>(StringComparer.Ordinal)
    {
        "memory_write", "memory_search", "suggest_task", "open_app",
    };

    private readonly HeadlessEnvironment _env;
    private readonly List<ChatMessage> _history = new();
    private readonly HashSet<string> _loadedTools = new(StringComparer.Ordinal);
    private List<PlanItem> _plan = new();

    public HeadlessSession(HeadlessEnvironment env, string workspace, PermissionMode permission, string? id = null)
    {
        if (permission == PermissionMode.Full)
        {
            // 「完全权限」只能员工在聊天窗口里对单个任务选，命令行和 IDE 不给
            throw new ArgumentException("命令行和 IDE 不能使用完全权限", nameof(permission));
        }
        _env = env;
        Workspace = Path.GetFullPath(workspace);
        Permission = permission;
        DeepThinking = env.DeepThinking;
        Id = id ?? "cli-" + Guid.NewGuid().ToString("N")[..12];
    }

    public string Id { get; }
    public string Workspace { get; }
    public PermissionMode Permission { get; private set; }

    /// <summary>这个会话要不要深度思考（命令行 /think 可以中途切换；起始值来自 --think）。</summary>
    public bool DeepThinking { get; set; }

    /// <summary>
    /// 较早对话压成的摘要（/compact 之后）：接在系统提示词后面，跟着会话一起保存。
    /// </summary>
    public string? Summary { get; private set; }

    /// <summary>
    /// 调这个会话的权限（命令行 /permission）。和开会话时一样，「完全权限」不给：只能员工在聊天窗口里对单个任务选。
    /// </summary>
    public void SetPermission(PermissionMode permission)
    {
        if (permission == PermissionMode.Full)
        {
            throw new ArgumentException("命令行和 IDE 不能使用完全权限", nameof(permission));
        }
        Permission = permission;
    }
    public IReadOnlyList<ChatMessage> History => _history;
    public IReadOnlyList<PlanItem> Plan => _plan;
    public IReadOnlyCollection<string> LoadedTools => _loadedTools;

    /// <summary>
    /// 接着一个保存下来的会话聊（IDE 的 session/load）：历史、计划、加载过的工具原样放回。
    /// 历史末尾要是停在半截（助手发了工具调用、结果没存下来），补上「没有执行」的结果，免得下一轮请求被上游拒绝。
    /// </summary>
    public void Restore(IEnumerable<ChatMessage> history, IEnumerable<PlanItem> plan, IEnumerable<string> loadedTools, string? summary = null)
    {
        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary;
        _history.Clear();
        _history.AddRange(history.Where(m => m.Role != ChatRole.System));
        var answered = _history.Where(m => m.Role == ChatRole.Tool && m.ToolCallId is not null).Select(m => m.ToolCallId!).ToHashSet(StringComparer.Ordinal);
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role != ChatRole.Assistant)
            {
                continue;
            }
            var missing = _history[i].ToolCalls.Where(c => !answered.Contains(c.Id)).ToList();
            var at = i + 1;
            while (at < _history.Count && _history[at].Role == ChatRole.Tool)
            {
                at++;
            }
            foreach (var call in missing)
            {
                _history.Insert(at++, ChatMessage.ToolResult(call, "（这一步没有执行完：会话在这里中断了）"));
            }
        }
        _plan = plan.ToList();
        _loadedTools.Clear();
        _loadedTools.UnionWith(loadedTools);
    }

    private bool DeveloperToolsOn => _env.Security.On(SecuritySettings.DeveloperTools, fallback: false);

    /// <summary>这个会话能用的工具。</summary>
    public ToolRegistry BuildTools()
    {
        var registry = new ToolRegistry();
        foreach (var tool in ToolRegistry.CreateDefault().All)
        {
            if (Excluded.Contains(tool.Name))
            {
                continue;
            }
            if (_env.Skills is null && tool.Name is "load_skill" or "search_skills")
            {
                continue;
            }
            registry.Add(tool);
        }
        foreach (var tool in DeveloperToolSet.For(_env.Security, agentMode: true))
        {
            registry.Add(tool);
        }
        return registry;
    }

    /// <summary>系统提示词：和员工端同一个 PromptBuilder（安全规则、外部内容不是指令等都在里面），再说明这是无界面调用。</summary>
    public string BuildPrompt(string query)
    {
        var (system, turn) = BuildPromptParts(query);
        return turn.Length == 0 ? system : system + "\n\n" + turn;
    }

    /// <summary>
    /// 分成不变的系统提示词和本轮参考资料（按问题挑的技能、当前时间）：参考资料附在这一轮的用户消息上、跟着历史走，
    /// 多轮时系统提示词和历史都不变，提示词缓存才命中（见 PromptBuilder.BuildSplit）。
    /// </summary>
    public (string System, string Turn) BuildPromptParts(string query)
    {
        var (system, turn) = new PromptBuilder(memory: null, _env.Skills).BuildSplit(new PromptContext
        {
            Mode = ConversationMode.Agent,
            UiLanguage = _env.UiLanguage,
            Workspace = Workspace,
            Permission = Permission,
            Sandboxed = _env.Security.On(SecuritySettings.Sandbox),
            Query = query,
            DeveloperTools = DeveloperToolsOn,
        });
        return (system + HeadlessGuide(_env.Actor), turn);
    }

    private static string HeadlessGuide(string actor) => $"""


        <无界面调用>
        这次是从{(actor == "ide" ? "开发工具（IDE）" : "命令行")}调用的，没有员工端的聊天窗口：
        - 不能向员工提问；信息不够时按最合理的理解做，并在最后的回答里说明你做了什么假设。
        - 需要确认的操作由调用方决定放不放行；被拒绝了就换不需要确认的做法，或者在回答里说明还差哪一步要员工自己做。
        - 最后的回答要能单独看懂：做了什么、结果如何、跑了哪些检查（没跑的如实说）。
        </无界面调用>
        """;

    /// <summary>发一句话，跑一轮。多轮时历史接着用（IDE 会话）。</summary>
    public async Task<AgentRunResult> PromptAsync(string text, IConfirmationHandler confirm, IAgentObserver observer, CancellationToken ct)
    {
        var tools = BuildTools();
        var (prompt, turnContext) = BuildPromptParts(text);
        var scene = DeveloperToolsOn ? Scenes.Code : Scenes.Agent;
        var user = ChatMessage.User(text);
        user.TurnContext = turnContext.Length > 0 ? turnContext : null;
        var plan = TaskPlan.HasOpenSteps(_plan) ? _plan : new List<PlanItem>();

        var context = new ContextManager(_env.Gateway, prompt, Summary, _env.ContextLength) { Scene = scene, Plan = () => plan };
        var history = new List<ChatMessage> { ChatMessage.System(context.SystemPrompt) };
        history.AddRange(_history);
        history.Add(user);

        var ctx = new ToolContext
        {
            Policy = _env.Policy,
            ConversationId = Id,
            LoadedTools = _loadedTools,
            Rules = _env.ToolRules,
            UserRequest = text,
            Workspace = Workspace,
            Permission = Permission,
            RunId = Guid.NewGuid().ToString("N"),
            Skills = _env.Skills,
            Deleter = _env.Deleter ?? (_env.Security.On(SecuritySettings.DeleteProtection) ? new ShellRecycleBin() : new PermanentDeleter()),
            Security = _env.Security,
        };
        ctx.Plan.AddRange(plan);
        ctx.PlanChanged += updated => plan = updated.ToList();

        var options = new AgentOptions
        {
            MaxRunTokens = RunTokenLimit(_env.Security),
            PlanGuidance = _env.Security.On(SecuritySettings.PlanGuidance),
            VerifyOutputs = _env.Security.On(SecuritySettings.VerifyOutputs),
            ToolOutputDir = _env.ToolOutputDir is { } dir ? Path.Combine(dir, Id) : null,
        };
        var loop = new AgentLoop(_env.Gateway, tools, confirm, new ActorAudit(_env.Audit, _env.Actor), options, _env.Approvals ?? new ApprovalStore());
        var extraBody = DeepThinking
            ? new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["enable_thinking"] = true }
            : null;
        var result = await loop.RunAsync(history, scene, ctx, observer, useTools: true, ct, modelId: null, context, extraBody);

        // 这一轮的话和结果留在会话里，下一句接着用（AgentLoop 停止时已给发出的调用补了结果，历史是完整的）
        _history.Add(user);
        _history.AddRange(result.NewMessages);
        _plan = plan;
        return result;
    }

    /// <summary>
    /// 立即整理这个会话（命令行 /compact）：最近一问一答之前的对话压成摘要，摘要接在系统提示词后面。
    /// 和员工端「整理对话」同一个 ContextManager.CompactNowAsync；focus 是员工交代要重点保留的内容。
    /// 能压的太少时返回 null，什么也不改。
    /// </summary>
    public async Task<CompactionInfo?> CompactAsync(string? focus, CancellationToken ct)
    {
        var (prompt, _) = BuildPromptParts("");
        var context = new ContextManager(_env.Gateway, prompt, Summary, _env.ContextLength)
        {
            Scene = DeveloperToolsOn ? Scenes.Code : Scenes.Agent,
        };
        var history = new List<ChatMessage> { ChatMessage.System(context.SystemPrompt) };
        history.AddRange(_history);
        var info = await context.CompactNowAsync(history, ct, focus);
        if (info is null)
        {
            return null;
        }
        Summary = context.Summary;
        _history.Clear();
        _history.AddRange(history.Where(m => m.Role != ChatRole.System));
        return info;
    }

    /// <summary>上下文用了多少、花在哪（命令行 /context）：和员工端「会话信息」同一套估算。</summary>
    public ContextUsageReport MeasureContext()
    {
        var (prompt, _) = BuildPromptParts("");
        var options = new ContextOptions();
        var length = _env.ContextLength > 0 ? _env.ContextLength : options.DefaultContextLength;
        var sections = new List<(string, string)> { ("base", prompt) };
        if (Summary is not null)
        {
            sections.Add(("summary", ContextManager.WithSummary("", Summary)));
        }
        return ContextUsage.Measure(
            sections,
            BuildTools().All,
            _history,
            length,
            (int)((length - options.ReserveOutputTokens) * options.CompactRatio),
            options.ReserveOutputTokens);
    }

    /// <summary>单次任务的 token 上限：和员工端一样读安全中心（单位万 tokens，0 = 不限）。</summary>
    private static long RunTokenLimit(SecuritySettings security)
    {
        var wan = security.Number(SecuritySettings.RunTokenBudget, (int)(AgentOptions.DefaultMaxRunTokens / 10_000));
        return wan <= 0 ? 0 : wan * 10_000L;
    }

    /// <summary>审计记录标上来源（cli / ide），后台能分清哪些操作不是在聊天窗口里做的。</summary>
    private sealed class ActorAudit : IAuditSink
    {
        private readonly IAuditSink _inner;
        private readonly string _actor;

        public ActorAudit(IAuditSink inner, string actor)
        {
            _inner = inner;
            _actor = actor;
        }

        public void Record(AuditEntry entry)
        {
            if (entry.Actor is "" or "main")
            {
                entry.Actor = _actor;
            }
            _inner.Record(entry);
        }
    }
}
