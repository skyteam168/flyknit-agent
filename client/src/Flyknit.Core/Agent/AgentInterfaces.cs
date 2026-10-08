using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Agent;

public enum ConfirmChoice
{
    /// <summary>只允许这一次。</summary>
    AllowOnce,

    /// <summary>本次任务内同类操作不再询问（会话级临时授权，任务结束后失效）。</summary>
    AllowForSession,

    /// <summary>允许，并且以后完全相同的操作不再询问（持久化规则）。</summary>
    AllowAlways,

    Reject,

    /// <summary>旧版选项，等同于 AllowAlways。用于兼容旧 UI。</summary>
    [Obsolete("Use AllowAlways instead")]
    AllowForConversation = AllowAlways,
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

    /// <summary>上下文被自动压缩。</summary>
    void OnContextCompacted(Context.CompactionInfo info)
    {
    }

    /// <summary>任务产出了文件（新增或改动）。界面据此给出打开 / 预览的卡片。</summary>
    void OnOutputsProduced(IReadOnlyList<OutputFile> files)
    {
    }

    /// <summary>运行中的一条提示（例如内容被审核拦截后省略重试），显示给用户但不算回答。</summary>
    void OnNotice(string text)
    {
    }
}

public sealed class AgentOptions
{
    /// <summary>
    /// 一轮最多调用多少次模型。只是最后的保险：真正防空转靠 <see cref="LoopGuard"/>（重复同一个操作会被提醒、再犯就停），
    /// 上下文过长靠 ContextManager 压缩，所以这里可以放得比较宽，长任务不会中途被打断。
    /// </summary>
    public int MaxSteps { get; init; } = DefaultMaxSteps;

    public const int DefaultMaxSteps = 100;

    /// <summary>每隔多少步提醒模型对照计划自查一次（不停下）。0 表示不提醒。</summary>
    public int CheckpointInterval { get; init; } = 25;

    /// <summary>因步数上限或空转暂停时，先让模型不调工具总结一次做到哪了。</summary>
    public bool WrapUpOnPause { get; init; } = true;
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxToolOutputChars { get; init; } = ToolResult.DefaultMaxChars;

    /// <summary>连续多少次工具失败后停止，避免死循环。</summary>
    public int MaxConsecutiveFailures { get; init; } = 4;

    /// <summary>
    /// 规划提醒（只提醒，不拦截）：做了几步还没列计划时提醒一次；有计划时，连续失败或原地打转就提醒先改计划。
    /// 由 IT 在安全中心开关（plan_guidance）。
    /// </summary>
    public bool PlanGuidance { get; init; } = true;

    /// <summary>没列计划时，做了几次操作后提醒一次。简单任务两三步就完了，不会被打扰。</summary>
    public int PlanNudgeAfter { get; init; } = 3;

    /// <summary>
    /// 检查产出文件（存在、不是空的、能打开，以及行数页数），结果附在工具结果后面给模型看；
    /// 最后回答前再查一遍，有问题在回答末尾注明。由 IT 在安全中心开关（verify_outputs）。
    /// </summary>
    public bool VerifyOutputs { get; init; } = true;
}

public enum AgentStopReason
{
    Completed,
    MaxSteps,
    TooManyFailures,
    Cancelled,

    /// <summary>反复做同一个操作没有进展（空转），提醒后仍然如此，主动停下。</summary>
    Stuck,

    /// <summary>模型服务返回错误（网络、鉴权、内容审核等），已经做完的部分照常保留。</summary>
    Failed,
}

public sealed class AgentRunResult
{
    public required AgentStopReason StopReason { get; init; }

    /// <summary>本次运行的完整链路：每一步做了什么、花了多久、用了多少 token。</summary>
    public Trace? Trace { get; init; }

    /// <summary>本次运行新产生的消息（assistant 与 tool），需要保存到会话。</summary>
    public required IReadOnlyList<ChatMessage> NewMessages { get; init; }

    public string? ModelName { get; init; }

    /// <summary>本次运行所有模型调用的 token 用量合计。</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>运行中发生的上下文压缩（最后一次）。</summary>
    public Context.CompactionInfo? Compaction { get; init; }

    /// <summary>调用了几次模型、几次工具（统计任务效果用）。</summary>
    public int Steps { get; init; }
    public int ToolCalls { get; init; }

    /// <summary>产出文件检查发现的问题（过程中发现过的 + 最后仍然存在的）。</summary>
    public int OutputProblems { get; init; }
    public int OutputProblemsAtEnd { get; init; }

    /// <summary>是否提醒过列计划 / 改计划。</summary>
    public bool PlanNudged { get; init; }
    public bool ReplanNudged { get; init; }
}
