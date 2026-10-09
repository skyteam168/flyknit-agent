using System.Text.Json;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Agent;

/// <summary>
/// 思考 → 规划 → 执行 → 观察 的循环：
/// 调用模型 → 模型返回工具调用 → 按策略阻止 / 确认 / 自动执行 → 结果回填 → 再次调用模型，直到模型给出最终答复。
/// </summary>
public sealed class AgentLoop
{
    private readonly IChatGateway _gateway;
    private readonly ToolRegistry _tools;
    private readonly IConfirmationHandler _confirm;
    private readonly IAuditSink _audit;
    private readonly AgentOptions _options;

    /// <summary>用户授权过的操作（同样的命令确认一次后不再询问）。由宿主提供并持久化，跨对话、跨重启有效。</summary>
    private readonly ApprovalStore _approvals;

    /// <summary>
    /// 会话级临时授权：本次任务内同类操作不再询问。
    /// Key 格式："{tool}:{pattern}" 例如 "run_shell:python" 或 "run_shell:npm"
    /// </summary>
    private readonly HashSet<string> _sessionApprovals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本轮已经报过的产出文件，避免同一个文件反复出现在界面上。</summary>
    private readonly HashSet<string> _outputs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本轮工具报告过的全部产出路径（按出现顺序），交付前再检查一遍。</summary>
    private readonly List<string> _produced = new();

    private const string UpdatePlanName = "update_plan";

    // ---------- 模型把工具调用写进正文 ----------

    public const string LeakRetryNotice = "模型返回的格式不对，正在重新生成回答…";

    public const string ChatModeFallback = "（AI 想执行操作，但对话模式下不能调用工具。需要它操作电脑的话，请切换到“办事”模式再问一次。）";

    public const string AgentModeFallback = "（模型返回的工具调用格式无法识别，没有执行。请再试一次，或者换一个模型。）";

    private static string LeakNote(bool toolsAvailable, IEnumerable<string> names) => toolsAvailable
        ? $"（系统提示）你刚才把工具调用（{string.Join("、", names.Distinct())}）写成了正文里的标记，这样不会被执行。请通过函数调用接口调用提供的工具，只能用工具列表里有的工具；不需要工具就直接用文字回答。"
        : "（系统提示）当前是对话模式，没有可用的工具，不能执行命令或读写文件。请直接用文字回答；如果确实需要操作电脑，告诉用户切换到“办事”模式。";

    private static readonly HashSet<string> ShellAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "shell", "cmd", "powershell", "pwsh", "terminal", "execute_command", "run_command", "exec", "command", "run_terminal_cmd",
    };

    private static readonly HashSet<string> ReadAliases = new(StringComparer.OrdinalIgnoreCase) { "read", "cat", "open_file", "view_file", "readfile" };

    private static readonly HashSet<string> ListAliases = new(StringComparer.OrdinalIgnoreCase) { "ls", "dir", "list_directory", "list_files", "listdir" };

    /// <summary>
    /// 正文里认出来的调用 → 能执行的工具调用。工具列表里有同名的直接用；模型训练时常用的几个名字
    /// （bash、cmd、cat、ls…）映射到对应的工具；其余认不出来，返回 null。
    /// </summary>
    public static ToolCall? MapLeaked(LeakedToolCall call, ToolRegistry registry)
    {
        var id = "call_" + Guid.NewGuid().ToString("N")[..16];
        if (registry.Get(call.Name) is not null)
        {
            return new ToolCall(id, call.Name, call.Arguments.ToJsonString());
        }
        string? Arg(params string[] keys) =>
            keys.Select(k => call.Arguments[k]).OfType<System.Text.Json.Nodes.JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (ShellAliases.Contains(call.Name) && registry.Get("run_shell") is not null && Arg("command", "cmd", "script", "code", "commandLine") is { } command)
        {
            var args = new System.Text.Json.Nodes.JsonObject { ["command"] = command };
            var shell = call.Name.ToLowerInvariant() switch
            {
                "cmd" => "cmd",
                "powershell" or "pwsh" => "powershell",
                // 模型常在 bash 的名义下写 cmd 语法（2>nul、&、&&），PowerShell 5 不认这些
                _ => command.Contains("2>nul", StringComparison.OrdinalIgnoreCase) || command.Contains("&&") || command.Contains(" & ") ? "cmd" : null,
            };
            if (shell is not null)
            {
                args["shell"] = shell;
            }
            if (Arg("cwd", "workdir", "working_directory") is { } cwd)
            {
                args["working_directory"] = cwd;
            }
            return new ToolCall(id, "run_shell", args.ToJsonString());
        }
        if (ReadAliases.Contains(call.Name) && registry.Get("read_file") is not null && Arg("path", "file", "file_path", "filename") is { } file)
        {
            return new ToolCall(id, "read_file", new System.Text.Json.Nodes.JsonObject { ["path"] = file }.ToJsonString());
        }
        if (ListAliases.Contains(call.Name) && registry.Get("list_dir") is not null && Arg("path", "dir", "directory") is { } dir)
        {
            return new ToolCall(id, "list_dir", new System.Text.Json.Nodes.JsonObject { ["path"] = dir }.ToJsonString());
        }
        return null;
    }

    private static ChatTurn WithContent(ChatTurn turn, string content, IReadOnlyList<ToolCall> calls, TokenUsage? usage = null) => new()
    {
        Content = content,
        Reasoning = turn.Reasoning,
        ToolCalls = calls,
        FinishReason = turn.FinishReason,
        ModelName = turn.ModelName,
        Usage = usage ?? turn.Usage,
        ContextLength = turn.ContextLength,
    };

    private static TokenUsage? Sum(TokenUsage? a, TokenUsage? b) => a is null ? b : b is null ? a : a + b;

    /// <summary>做了几步还没列计划时的提醒。</summary>
    public static string PlanNudgeText(int actions) => $"""


        【系统提醒】这个任务已经做了 {actions} 步，还没有列计划。如果后面还有好几步，先用 update_plan 把剩下的步骤列出来再继续；
        如果马上就能做完，忽略这条，直接完成。
        """;

    /// <summary>有计划但卡住了（连续失败、原地打转）时的提醒。</summary>
    public const string ReplanText = """


        【系统提醒】现在的做法不太顺。先回头看看计划：这一步行不通就用 update_plan 改掉它（换个办法、拆小、或者标记需要用户帮忙），再继续执行。
        """;

    public AgentLoop(IChatGateway gateway, ToolRegistry tools, IConfirmationHandler confirm, IAuditSink? audit = null, AgentOptions? options = null, ApprovalStore? approvals = null)
    {
        _gateway = gateway;
        _tools = tools;
        _confirm = confirm;
        _audit = audit ?? new NullAuditSink();
        _options = options ?? new AgentOptions();
        _approvals = approvals ?? new ApprovalStore();
    }

    /// <param name="history">完整上下文（含系统提示词），新消息会追加到此列表。</param>
    /// <param name="useTools">为 false 时只做普通对话（翻译模式、标题生成等）。</param>
    public async Task<AgentRunResult> RunAsync(
        List<ChatMessage> history,
        string scene,
        ToolContext ctx,
        IAgentObserver observer,
        bool useTools,
        CancellationToken ct,
        int? modelId = null,
        Context.ContextManager? context = null)
    {
        var newMessages = new List<ChatMessage>();
        var trace = new Trace(ctx.ConversationId);
        var tools = useTools ? _tools.ToOpenAiTools() : null;
        var failures = 0;
        string? modelName = null;
        TokenUsage? usage = null;
        Context.CompactionInfo? compaction = null;
        var guard = new LoopGuard();
        var markupRetried = false;      // 正文里的调用标记认不出来时，只提醒重答一次
        var steps = 0;
        var toolCalls = 0;
        var actions = 0;               // 除了更新计划以外的操作次数
        var planNudged = false;
        var replanNudged = false;
        var replanHintedThisStreak = false;
        var outputProblems = 0;
        var outputProblemsAtEnd = 0;

        void PlanChanged(IReadOnlyList<PlanItem> plan) => observer.OnPlanUpdated(plan);
        ctx.PlanChanged += PlanChanged;
        try
        {
            for (var step = 0; step < _options.MaxSteps; step++)
            {
                if (ct.IsCancellationRequested)
                {
                    return Result(AgentStopReason.Cancelled);
                }

                ChatTurn turn;
                using var modelStep = trace.Begin("model", modelName ?? "", $"第 {step + 1} 轮");
                try
                {
                    if (context is not null)
                    {
                        // 短期记忆管理：上下文过长时裁剪旧工具输出或压缩为摘要
                        using var compactStep = trace.Begin("compact", "context");
                        var info = await context.PrepareAsync(history, ct);
                        if (info is not null)
                        {
                            compaction = info;
                            compactStep.Summary = $"压缩 {info.MessagesCompacted} 条，{info.TokensBefore} → {info.TokensAfter} tokens";
                            observer.OnContextCompacted(info);
                        }
                        else
                        {
                            compactStep.Status = "skipped";
                        }
                    }
                    turn = await _gateway.CompleteAsync(
                        new ChatRequest
                        {
                            Scene = scene,
                            Messages = history,
                            Tools = tools,
                            Stream = true,
                            ModelId = modelId,
                            ConversationId = ctx.ConversationId,
                        },
                        observer,
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    modelStep.Status = "stopped";
                    return Result(AgentStopReason.Cancelled);
                }
                catch (GatewayException ex) when (ContentFilter.IsBlocked(ex) && ContentFilter.Redact(history))
                {
                    modelStep.Status = "blocked";
                    modelStep.Summary = "内容审核拦截，省略后重试";
                    // 内容审核拦截：多半是刚读进来的文件里有触发词。
                    // 把最近那段大的工具输出换成占位符再试一次，让任务能走下去，而不是整轮报废。
                    observer.OnNotice(ContentFilter.RetryNotice);
                    continue;
                }
                catch (GatewayException ex)
                {
                    modelStep.Status = "error";
                    modelStep.Summary = ex.Message;
                    // 模型服务出错：把原因作为一条回答留在对话里，已经做完的步骤照常保存
                    var failure = ChatMessage.Assistant(ContentFilter.Explain(ex));
                    Append(failure);
                    observer.OnAssistantMessage(failure);
                    return Result(AgentStopReason.Failed);
                }
                if (turn.ToolCalls.Count == 0 && ToolMarkup.Contains(turn.Content))
                {
                    // 模型把工具调用写进了正文（上游没解析成 tool_calls）：认得出的拿回来执行，认不出的去掉再要一次
                    turn = await RecoverLeakedCallsAsync(turn);
                }
                modelName ??= turn.ModelName;
                steps++;
                modelStep.PromptTokens = turn.Usage?.PromptTokens ?? 0;
                modelStep.CompletionTokens = turn.Usage?.CompletionTokens ?? 0;
                modelStep.Summary = turn.ToolCalls.Count > 0
                    ? $"决定调用 {string.Join("、", turn.ToolCalls.Select(c => c.Name))}"
                    : "给出回答";
                modelStep.Dispose(); // 这一轮的模型调用到此结束，后面是工具执行

                context?.Observe(turn);
                if (turn.Usage is { } u)
                {
                    usage = usage is null ? u : usage + u;
                }

                var content = turn.Content;
                if (turn.ToolCalls.Count == 0 && _options.VerifyOutputs && _produced.Count > 0)
                {
                    // 交付前最后查一遍这一轮的产出：确定有问题的（不见了、空的、打不开）在回答末尾注明，
                    // 不自动返工——过程中每产出一个文件都已经查过一次、模型有机会当场修
                    using var verifyStep = trace.Begin("verify", "");
                    var problems = _produced.Where(OutputVerifier.IsDocument).Take(OutputVerifier.MaxFiles * 2)
                        .Select(OutputVerifier.Check).Where(c => c.Status == OutputCheckStatus.Problem).ToList();
                    outputProblemsAtEnd = problems.Count;
                    verifyStep.Summary = problems.Count == 0 ? "产出文件检查通过" : $"{problems.Count} 个产出文件有问题";
                    if (problems.Count > 0)
                    {
                        verifyStep.Status = "warning";
                        content = content.TrimEnd() + "\n\n> ⚠ 系统检查：" + string.Join("；", problems.Select(p => $"{p.Name} {p.Detail}")) + "。请打开确认一下。";
                    }
                }

                var assistant = ChatMessage.Assistant(content, turn.ToolCalls, turn.Reasoning.Length > 0 ? turn.Reasoning : null);
                assistant.ModelName = turn.ModelName;
                assistant.PromptTokens = turn.Usage?.PromptTokens;
                assistant.CompletionTokens = turn.Usage?.CompletionTokens;
                Append(assistant);
                observer.OnAssistantMessage(assistant);

                if (turn.ToolCalls.Count == 0)
                {
                    return Result(AgentStopReason.Completed);
                }

                var verdict = LoopVerdict.Ok;
                foreach (var call in turn.ToolCalls)
                {
                    if (ct.IsCancellationRequested)
                    {
                        // 已经发起的工具调用必须有结果，否则下一轮请求会被模型服务拒绝
                        AppendTool(call, "用户已停止任务，未执行");
                        continue;
                    }
                    var ok = await HandleCallAsync(call, turn.Content, ctx, observer, trace, ct);
                    failures = ok ? 0 : failures + 1;
                    toolCalls++;
                    if (call.Name != UpdatePlanName)
                    {
                        actions++;
                    }
                    if (ToolMessageOf(call) is { } result)
                    {
                        var v = guard.Observe(call, result.Content);
                        if (v == LoopVerdict.Nudge)
                        {
                            result.Content += LoopGuard.NudgeText(call.Name);
                            if (_options.PlanGuidance && call.Name != UpdatePlanName && TaskPlan.HasOpenSteps(ctx.Plan))
                            {
                                result.Content += ReplanText;
                                replanNudged = true;
                            }
                        }
                        verdict = (LoopVerdict)Math.Max((int)verdict, (int)v);
                    }
                }

                // 规划提醒：只提醒，不拦截，每种情况至多一次，不影响失败计数
                if (_options.PlanGuidance && newMessages.LastOrDefault(m => m.Role == ChatRole.Tool) is { } lastTool)
                {
                    if (!planNudged && ctx.Plan.Count == 0 && actions >= _options.PlanNudgeAfter)
                    {
                        // 做了几步还没有计划：多半是个多步任务，提醒一次先把剩下的步骤列出来
                        lastTool.Content += PlanNudgeText(actions);
                        planNudged = true;
                    }
                    else if (failures == 0)
                    {
                        replanHintedThisStreak = false;
                    }
                    else if (failures == 2 && !replanHintedThisStreak && TaskPlan.HasOpenSteps(ctx.Plan))
                    {
                        // 连着失败两次：先回头看计划还行不行，而不是第三次硬试
                        lastTool.Content += ReplanText;
                        replanHintedThisStreak = true;
                        replanNudged = true;
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    return Result(AgentStopReason.Cancelled);
                }
                if (failures >= _options.MaxConsecutiveFailures)
                {
                    return Result(AgentStopReason.TooManyFailures);
                }
                if (verdict == LoopVerdict.Stop)
                {
                    // 提醒过了还在原地打转：停下，让模型说明卡在哪，不再继续烧 token
                    await WrapUpAsync($"""


                        【系统提醒】你已经多次重复调用 {guard.LastTool} 仍没有进展，任务已暂停。不要再调用工具。
                        请用一两段话告诉用户：卡在哪一步、已经试过什么、需要用户提供什么信息或做什么操作。
                        """);
                    return Result(AgentStopReason.Stuck);
                }
                var done = step + 1;
                if (_options.CheckpointInterval > 0 && done % _options.CheckpointInterval == 0 && done < _options.MaxSteps
                    && newMessages.LastOrDefault(m => m.Role == ChatRole.Tool) is { } last)
                {
                    last.Content += LoopGuard.CheckpointText(done);
                }
            }
            // 步数用完：最后让模型不调工具答一次，交代做到哪了，用户回复“继续”时有清楚的交接
            await WrapUpAsync($"""


                【系统提醒】这一轮已经执行了 {_options.MaxSteps} 步，到达上限，任务暂停。不要再调用工具。
                请简要总结：已经完成了什么、还差什么、下一步打算怎么做。用户回复“继续”后会接着做。
                """);
            return Result(AgentStopReason.MaxSteps);
        }
        finally
        {
            ctx.PlanChanged -= PlanChanged;
        }

        AgentRunResult Result(AgentStopReason reason) => new()
        {
            StopReason = reason,
            Trace = trace,
            NewMessages = newMessages,
            ModelName = modelName,
            Usage = usage,
            Compaction = compaction,
            Steps = steps,
            ToolCalls = toolCalls,
            OutputProblems = outputProblems,
            OutputProblemsAtEnd = outputProblemsAtEnd,
            PlanNudged = planNudged,
            ReplanNudged = replanNudged,
        };

        void Append(ChatMessage m)
        {
            history.Add(m);
            newMessages.Add(m);
        }

        ChatMessage? ToolMessageOf(ToolCall call) =>
            newMessages.LastOrDefault(m => m.Role == ChatRole.Tool && m.ToolCallId == call.Id);

        // 暂停前的收尾回答：不带工具，模型只能用文字说明情况。出错就算了，暂停照常
        async Task WrapUpAsync(string note)
        {
            if (!_options.WrapUpOnPause || ct.IsCancellationRequested
                || newMessages.LastOrDefault() is not { Role: ChatRole.Tool } last)
            {
                return;
            }
            last.Content += note;
            using var step = trace.Begin("model", modelName ?? "", "暂停前总结");
            try
            {
                var turn = await _gateway.CompleteAsync(
                    new ChatRequest
                    {
                        Scene = scene,
                        Messages = history,
                        Stream = true,
                        ModelId = modelId,
                        ConversationId = ctx.ConversationId,
                    },
                    observer,
                    ct);
                if (turn.Usage is { } u)
                {
                    usage = usage is null ? u : usage + u;
                }
                step.PromptTokens = turn.Usage?.PromptTokens ?? 0;
                step.CompletionTokens = turn.Usage?.CompletionTokens ?? 0;
                if (turn.Content.Trim().Length == 0)
                {
                    return;
                }
                // 不带工具也可能吐出工具调用（个别模型），丢掉，只留文字
                var answer = ChatMessage.Assistant(turn.Content, null, turn.Reasoning.Length > 0 ? turn.Reasoning : null);
                answer.ModelName = turn.ModelName;
                answer.PromptTokens = turn.Usage?.PromptTokens;
                answer.CompletionTokens = turn.Usage?.CompletionTokens;
                Append(answer);
                observer.OnAssistantMessage(answer);
            }
            catch (Exception ex) when (ex is GatewayException || (ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                step.Status = "error";
                step.Summary = ex.Message;
            }
        }

        async Task<ChatTurn> RecoverLeakedCallsAsync(ChatTurn leaked)
        {
            using var step = trace.Begin("recover", "");
            var (clean, calls) = ToolMarkup.Extract(leaked.Content);
            var mapped = tools is null ? new List<ToolCall>() : calls.Select(MapLeakedCall).OfType<ToolCall>().ToList();
            if (calls.Count > 0 && mapped.Count == calls.Count)
            {
                step.Summary = $"模型把 {calls.Count} 个工具调用写进了正文，已识别：{string.Join("、", mapped.Select(c => c.Name))}";
                return WithContent(leaked, clean, mapped);
            }
            step.Status = "warning";
            step.Summary = tools is null ? "对话模式下模型想调用工具，已去掉调用标记" : $"认不出的工具调用：{string.Join("、", calls.Select(c => c.Name).Distinct())}";
            if (!markupRetried)
            {
                // 提醒一次再要一次回答。不往界面流式输出（前面那段已经流出去了），新回答出来后整条替换
                markupRetried = true;
                observer.OnNotice(LeakRetryNotice);
                try
                {
                    var retry = await _gateway.CompleteAsync(new ChatRequest
                    {
                        Scene = scene,
                        Messages = history.Append(ChatMessage.User(LeakNote(tools is not null, calls.Select(c => c.Name)))).ToList(),
                        Tools = tools,
                        Stream = true,
                        ModelId = modelId,
                        ConversationId = ctx.ConversationId,
                    }, null, ct);
                    // 两次请求的用量都算上（外面按返回的这一轮累计）
                    var both = Sum(leaked.Usage, retry.Usage);
                    if (retry.ToolCalls.Count > 0 || !ToolMarkup.Contains(retry.Content))
                    {
                        step.Summary += "；提醒后重新回答";
                        return WithContent(retry, retry.Content, retry.ToolCalls, both);
                    }
                    clean = ToolMarkup.Extract(retry.Content).Text;
                    leaked = WithContent(leaked, leaked.Content, leaked.ToolCalls, both);
                }
                catch (GatewayException ex)
                {
                    step.Summary += $"；重试失败：{ex.Message}";
                }
            }
            return WithContent(leaked, clean.Length > 0 ? clean : tools is null ? ChatModeFallback : AgentModeFallback, Array.Empty<ToolCall>());
        }

        ToolCall? MapLeakedCall(LeakedToolCall c) => MapLeaked(c, _tools);

        void AppendTool(ToolCall call, string content)
        {
            var m = ChatMessage.ToolResult(call, content);
            Append(m);
            observer.OnToolMessage(m);
        }

        async Task<bool> HandleCallAsync(ToolCall call, string rationale, ToolContext context, IAgentObserver obs, Trace trace, CancellationToken token)
        {
            using var step = trace.Begin("tool", call.Name);
            var tool = _tools.Get(call.Name);
            if (tool is null)
            {
                AppendTool(call, $"错误：不存在名为 {call.Name} 的工具");
                return false;
            }

            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                args = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                AppendTool(call, $"错误：参数不是合法的 JSON（{ex.Message}），请修正后重试");
                return false;
            }

            PolicyDecision decision;
            string summary;
            try
            {
                decision = tool.Assess(args, context);
                summary = tool.Describe(args);
            }
            catch (Exception ex)
            {
                AppendTool(call, $"错误：{ex.Message}");
                return false;
            }

            obs.OnToolStarted(call, summary, decision);
            var audit = new AuditEntry
            {
                ConversationId = context.ConversationId,
                Scene = scene,
                ToolName = call.Name,
                Arguments = call.ArgumentsJson,
                Risk = decision.Level switch { RiskLevel.Blocked => "blocked", RiskLevel.Confirm => "confirm", _ => "auto" },
            };

            if (decision.Level == RiskLevel.Blocked)
            {
                audit.Decision = "blocked";
                audit.Status = "skipped";
                audit.Summary = decision.Reason;
                _audit.Record(audit);
                var blocked = ToolResult.Fail($"已被安全策略阻止：{decision.Reason}。请不要尝试用其他方式绕过，向用户说明原因即可。");
                obs.OnToolFinished(call, blocked, "blocked");
                AppendTool(call, blocked.Output);
                return true; // 被阻止不算失败，模型应向用户解释
            }

            var decisionText = "auto";
            if (decision.Level == RiskLevel.Confirm)
            {
                // 1. 检查持久化规则
                if (_approvals.IsAllowed(decision.Rule))
                {
                    decisionText = "remembered";
                }
                // 2. 检查会话级临时授权（本次任务内同类操作）。高危的从不自动放行：
                //    授权按程序名记（"python"、"git status"），同一个程序的删除、改系统设置照样要问
                else if (decision.Effect != CommandEffect.Destructive && IsSessionApproved(call.Name, summary))
                {
                    decisionText = "session";
                }
                else
                {
                    var choice = await _confirm.ConfirmAsync(new ConfirmRequest
                    {
                        ConversationId = context.ConversationId,
                        Call = call,
                        Summary = summary,
                        Decision = decision,
                        Rationale = rationale,
                    }, token);

                    if (choice == ConfirmChoice.Reject)
                    {
                        audit.Decision = "rejected";
                        audit.Status = "skipped";
                        _audit.Record(audit);
                        var rejected = ToolResult.Fail("用户拒绝了这个操作。请询问用户希望怎么做，不要重复同样的操作。");
                        obs.OnToolFinished(call, rejected, "rejected");
                        AppendTool(call, rejected.Output);
                        return true;
                    }
                    // 3. 处理持久化规则（AllowAlways）
                    if (choice == ConfirmChoice.AllowAlways && decision.Rule is not null)
                    {
                        _approvals.Add(decision.Rule);
                    }
                    // 4. 处理会话级临时授权（AllowForSession）
                    else if (choice == ConfirmChoice.AllowForSession && decision.Effect != CommandEffect.Destructive)
                    {
                        AddSessionApproval(call.Name, summary);
                    }
                    decisionText = "approved";
                }
            }

            ToolResult result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.ToolTimeout);
            try
            {
                result = await tool.ExecuteAsync(args, context, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                result = ToolResult.Fail("工具执行超时");
            }
            catch (OperationCanceledException)
            {
                result = ToolResult.Fail("用户已停止任务");
            }
            catch (Exception ex)
            {
                result = ToolResult.Fail($"执行失败：{ex.Message}");
            }

            result = result.Truncate(_options.MaxToolOutputChars);
            audit.Decision = decisionText;
            audit.Status = result.Ok ? "ok" : "error";
            audit.Summary = result.Output.Length > 500 ? result.Output[..500] : result.Output;
            _audit.Record(audit);

            obs.OnToolFinished(call, result, decisionText);
            var text = result.Output;
            if (result.Outputs.Count > 0)
            {
                // 同一个文件被改好几次只报一次，按最后一次的状态
                var files = OutputTracker.Describe(result.Outputs).Where(f => _outputs.Add(f.Path)).ToList();
                if (files.Count > 0)
                {
                    obs.OnOutputsProduced(files);
                }
                foreach (var path in result.Outputs.Where(p => !_produced.Contains(p, StringComparer.OrdinalIgnoreCase)))
                {
                    _produced.Add(path);
                }
                if (_options.VerifyOutputs)
                {
                    // 当场检查这一步产出的文件，结果附在工具结果后面：模型看得到行数、页数，发现不对当场就能修。
                    // 检查不影响这一步算成功还是失败
                    using var verifyStep = trace.Begin("verify", "");
                    var checks = result.Outputs.Where(OutputVerifier.IsDocument).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(OutputVerifier.MaxFiles).Select(OutputVerifier.Check).ToList();
                    var problems = checks.Count(c => c.Status == OutputCheckStatus.Problem);
                    outputProblems += problems;
                    verifyStep.Summary = checks.Count == 0 ? "没有需要检查的文档" : problems == 0 ? $"检查了 {checks.Count} 个文件" : $"{problems} 个文件有问题";
                    if (problems > 0)
                    {
                        verifyStep.Status = "warning";
                    }
                    if (OutputVerifier.Describe(checks) is { } note)
                    {
                        text += note;
                    }
                }
            }
            AppendTool(call, text);
            return result.Ok;
        }

        // 会话级授权的辅助函数（本地函数，不需要访问修饰符）
        string SessionApprovalKey(string toolName, string summary)
        {
            var prefix = ExtractCommandPrefix(summary);
            return $"{toolName}:{prefix}".ToLowerInvariant();
        }

        string ExtractCommandPrefix(string summary)
        {
            if (string.IsNullOrWhiteSpace(summary)) return "*";
            var parts = summary.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "*";
            var exe = Path.GetFileNameWithoutExtension(parts[0].Trim('"', '\''));
            if (parts.Length > 1 && exe is "npm" or "pip" or "git" or "dotnet" or "docker" or "yarn" or "pnpm")
            {
                return $"{exe} {parts[1]}";
            }
            return exe;
        }

        bool IsSessionApproved(string toolName, string summary)
        {
            var key = SessionApprovalKey(toolName, summary);
            return _sessionApprovals.Contains(key);
        }

        void AddSessionApproval(string toolName, string summary)
        {
            var key = SessionApprovalKey(toolName, summary);
            _sessionApprovals.Add(key);
        }
    }
}
