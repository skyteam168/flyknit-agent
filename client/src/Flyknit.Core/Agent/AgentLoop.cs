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
        int? modelId = null)
    {
        var newMessages = new List<ChatMessage>();
        var tools = useTools ? _tools.ToOpenAiTools() : null;
        var failures = 0;
        string? modelName = null;

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
                try
                {
                    turn = await _gateway.CompleteAsync(
                        new ChatRequest { Scene = scene, Messages = history, Tools = tools, Stream = true, ModelId = modelId },
                        observer,
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return Result(AgentStopReason.Cancelled);
                }
                modelName ??= turn.ModelName;

                var assistant = ChatMessage.Assistant(turn.Content, turn.ToolCalls, turn.Reasoning.Length > 0 ? turn.Reasoning : null);
                Append(assistant);
                observer.OnAssistantMessage(assistant);

                if (turn.ToolCalls.Count == 0)
                {
                    return Result(AgentStopReason.Completed);
                }

                foreach (var call in turn.ToolCalls)
                {
                    if (ct.IsCancellationRequested)
                    {
                        // 已经发起的工具调用必须有结果，否则下一轮请求会被模型服务拒绝
                        AppendTool(call, "用户已停止任务，未执行");
                        continue;
                    }
                    var ok = await HandleCallAsync(call, turn.Content, ctx, observer, ct);
                    failures = ok ? 0 : failures + 1;
                }

                if (ct.IsCancellationRequested)
                {
                    return Result(AgentStopReason.Cancelled);
                }
                if (failures >= _options.MaxConsecutiveFailures)
                {
                    return Result(AgentStopReason.TooManyFailures);
                }
            }
            return Result(AgentStopReason.MaxSteps);
        }
        finally
        {
            ctx.PlanChanged -= PlanChanged;
        }

        AgentRunResult Result(AgentStopReason reason) => new() { StopReason = reason, NewMessages = newMessages, ModelName = modelName };

        void Append(ChatMessage m)
        {
            history.Add(m);
            newMessages.Add(m);
        }

        void AppendTool(ToolCall call, string content)
        {
            var m = ChatMessage.ToolResult(call, content);
            Append(m);
            observer.OnToolMessage(m);
        }

        async Task<bool> HandleCallAsync(ToolCall call, string rationale, ToolContext context, IAgentObserver obs, CancellationToken token)
        {
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
                var key = ApprovalStore.KeyFor(call.Name, args);
                if (decision.Rememberable && _approvals.IsApproved(key))
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
                    if ((choice is ConfirmChoice.AllowAlways or ConfirmChoice.AllowForConversation) && decision.Rememberable)
                    {
                        _approvals.Approve(key, call.Name, summary);
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
            AppendTool(call, result.Output);
            return result.Ok;
        }
    }
}
