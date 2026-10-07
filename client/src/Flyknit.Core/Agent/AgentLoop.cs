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

    /// <summary>本轮已经报过的产出文件，避免同一个文件反复出现在界面上。</summary>
    private readonly HashSet<string> _outputs = new(StringComparer.OrdinalIgnoreCase);

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
                modelName ??= turn.ModelName;
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

                var assistant = ChatMessage.Assistant(turn.Content, turn.ToolCalls, turn.Reasoning.Length > 0 ? turn.Reasoning : null);
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
                    if (ToolMessageOf(call) is { } result)
                    {
                        var v = guard.Observe(call, result.Content);
                        if (v == LoopVerdict.Nudge)
                        {
                            result.Content += LoopGuard.NudgeText(call.Name);
                        }
                        verdict = (LoopVerdict)Math.Max((int)verdict, (int)v);
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
                // 规则只能放行「安全的那一类」：高危和认不出内容的命令拿不到 Rule，永远走人工确认
                if (_approvals.IsAllowed(decision.Rule))
                {
                    decisionText = "remembered";
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
                    if ((choice is ConfirmChoice.AllowAlways or ConfirmChoice.AllowForConversation) && decision.Rule is not null)
                    {
                        _approvals.Add(decision.Rule);
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
            if (result.Outputs.Count > 0)
            {
                // 同一个文件被改好几次只报一次，按最后一次的状态
                var files = OutputTracker.Describe(result.Outputs).Where(f => _outputs.Add(f.Path)).ToList();
                if (files.Count > 0)
                {
                    obs.OnOutputsProduced(files);
                }
            }
            AppendTool(call, result.Output);
            return result.Ok;
        }
    }
}
