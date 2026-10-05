using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Agent;

public enum ConfirmChoice
{
    /// <summary>允许这一次。</summary>
    AllowOnce,

    /// <summary>本会话内同一工具不再询问（命令类工具仍逐条确认）。</summary>
    AllowForConversation,

    Reject,
}

public sealed class ConfirmRequest
{
    public required string ConversationId { get; init; }
    public required ToolCall Call { get; init; }
    public required string Summary { get; init; }
    public required PolicyDecision Decision { get; init; }

    /// <summary>模型在调用工具前给出的说明，作为“理由”显示给用户。</summary>
    public string Rationale { get; init; } = "";
}

public interface IConfirmationHandler
{
    Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct);
}

public interface IAuditSink
{
    void Record(AuditEntry entry);
}

public sealed class NullAuditSink : IAuditSink
{
    public void Record(AuditEntry entry)
    {
    }
}

/// <summary>Agent 运行过程中的事件，界面据此实时刷新。</summary>
public interface IAgentObserver : IStreamSink
{
    void OnAssistantMessage(ChatMessage message);
    void OnToolStarted(ToolCall call, string summary, PolicyDecision decision);
    void OnToolFinished(ToolCall call, ToolResult result, string decision);
    void OnToolMessage(ChatMessage message);
    void OnPlanUpdated(IReadOnlyList<PlanItem> plan);
}

public sealed class AgentOptions
{
    public int MaxSteps { get; init; } = 25;
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxToolOutputChars { get; init; } = ToolResult.DefaultMaxChars;

    /// <summary>连续多少次工具失败后停止，避免死循环。</summary>
    public int MaxConsecutiveFailures { get; init; } = 4;
}

public enum AgentStopReason
{
    Completed,
    MaxSteps,
    TooManyFailures,
    Cancelled,
}

public sealed class AgentRunResult
{
    public required AgentStopReason StopReason { get; init; }

    /// <summary>本次运行新产生的消息（assistant 与 tool），需要保存到会话。</summary>
    public required IReadOnlyList<ChatMessage> NewMessages { get; init; }

    public string? ModelName { get; init; }
}
