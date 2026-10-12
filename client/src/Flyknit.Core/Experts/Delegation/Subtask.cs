using System.Text.Json.Serialization;

namespace Flyknit.Core.Experts.Delegation;

/// <summary>
/// 子任务（派遣给专家的一件事）的状态。
///
/// queued → running ⇄ waiting_confirmation → 终态。终态六个：
/// completed 完成 / partial 部分完成 / needs_input 缺信息 / failed 失败 / cancelled 取消 / timed_out 超时。
/// 前四个可以由专家交卷时自报；cancelled、timed_out 只由系统设置（员工停止、关闭开关、到时限）。
/// </summary>
public enum SubtaskStatus
{
    Queued,
    Running,
    WaitingConfirmation,
    Completed,
    Partial,
    NeedsInput,
    Failed,
    Cancelled,
    TimedOut,
}

public static class SubtaskStatuses
{
    public static bool IsFinal(this SubtaskStatus s) => s >= SubtaskStatus.Completed;

    public static string Wire(this SubtaskStatus s) => s switch
    {
        SubtaskStatus.Queued => "queued",
        SubtaskStatus.Running => "running",
        SubtaskStatus.WaitingConfirmation => "waiting_confirmation",
        SubtaskStatus.Completed => "completed",
        SubtaskStatus.Partial => "partial",
        SubtaskStatus.NeedsInput => "needs_input",
        SubtaskStatus.Failed => "failed",
        SubtaskStatus.Cancelled => "cancelled",
        _ => "timed_out",
    };

    public static SubtaskStatus? Parse(string? wire) => wire switch
    {
        "queued" => SubtaskStatus.Queued,
        "running" => SubtaskStatus.Running,
        "waiting_confirmation" => SubtaskStatus.WaitingConfirmation,
        "completed" => SubtaskStatus.Completed,
        "partial" => SubtaskStatus.Partial,
        "needs_input" => SubtaskStatus.NeedsInput,
        "failed" => SubtaskStatus.Failed,
        "cancelled" => SubtaskStatus.Cancelled,
        "timed_out" => SubtaskStatus.TimedOut,
        _ => null,
    };

    /// <summary>专家交卷时能自报的状态。取消、超时只能由系统定。</summary>
    public static readonly IReadOnlyList<string> Submittable = new[] { "completed", "partial", "needs_input", "failed" };
}

/// <summary>
/// 一个子任务的状态机。线程安全；进了终态就再也改不动——超时后迟到的工具结果、
/// 取消后才回来的确认，都只能看到终态，不会把任务「复活」。
/// </summary>
public sealed class SubtaskState
{
    private readonly object _gate = new();
    private SubtaskStatus _status = SubtaskStatus.Queued;

    public SubtaskStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public bool IsFinal => Status.IsFinal();

    /// <summary>还在跑（运行中或等确认）：只有这时才能开始新的模型调用、工具调用和交卷。</summary>
    public bool IsActive => Status is SubtaskStatus.Running or SubtaskStatus.WaitingConfirmation;

    /// <summary>取消的原因：user（员工停止）/ disabled（关闭了派遣开关）/ parent（主任务结束）/ timeout。</summary>
    public string CancelReason { get; private set; } = "";

    /// <summary>按规则转换状态；不合法（比如已经是终态）返回 false，什么也不改。</summary>
    public bool TryMove(SubtaskStatus to)
    {
        lock (_gate)
        {
            if (!Allowed(_status, to))
            {
                return false;
            }
            _status = to;
            return true;
        }
    }

    /// <summary>取消或超时：从任何非终态进入，记下原因。</summary>
    public bool TryStop(SubtaskStatus to, string reason)
    {
        lock (_gate)
        {
            if (to is not (SubtaskStatus.Cancelled or SubtaskStatus.TimedOut) || _status.IsFinal())
            {
                return false;
            }
            _status = to;
            CancelReason = reason;
            return true;
        }
    }

    public static bool Allowed(SubtaskStatus from, SubtaskStatus to)
    {
        if (from.IsFinal() || from == to)
        {
            return false;
        }
        return (from, to) switch
        {
            (SubtaskStatus.Queued, SubtaskStatus.Running) => true,
            (SubtaskStatus.Queued, _) => to is SubtaskStatus.Failed or SubtaskStatus.Cancelled,
            (SubtaskStatus.Running, _) => true, // 去等确认，或者进任何终态
            (SubtaskStatus.WaitingConfirmation, _) => to != SubtaskStatus.Queued,
            _ => false,
        };
    }
}

/// <summary>一条结论引用的证据。</summary>
public sealed class SubtaskEvidence
{
    /// <summary>E3 这样的证据编号（系统给每次工具调用编的），或者文件路径。</summary>
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";

    /// <summary>tool（工具调用）/ file（文件）。</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "tool";

    /// <summary>给人看的说明，例如「mes.query_orders 查询 A 车间 10 月工单」。</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>ok（引用的调用存在且成功）/ missing（不存在）/ failed_call（那次调用失败了）。由系统填。</summary>
    [JsonPropertyName("check")] public string Check { get; set; } = "missing";
}

/// <summary>专家交回的一条结论。</summary>
public sealed class SubtaskFinding
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("statement")] public string Statement { get; set; } = "";
    [JsonPropertyName("evidence")] public List<SubtaskEvidence> Evidence { get; set; } = new();

    /// <summary>
    /// 系统核验的结果，分两层：来源是不是真的（source_verified），证据能不能支持这句话（supported）。
    /// supported / source_verified / unverified（没给来源，或者来源说明不了这句话）/ verification_failed（引用的来源不存在或调用失败）。
    /// </summary>
    [JsonPropertyName("verification")] public string Verification { get; set; } = "unverified";

    /// <summary>专家自己写的范围、口径、不确定的地方；系统核验的说明也附在这里。</summary>
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

/// <summary>专家交的卷（submit_result 的参数，经过校验）。</summary>
public sealed class SubtaskSubmission
{
    public string Status { get; set; } = "completed";
    public string Summary { get; set; } = "";
    public List<SubtaskFinding> Findings { get; set; } = new();
    public List<string> OpenQuestions { get; set; } = new();
    public List<string> NextSuggestions { get; set; } = new();
    public List<string> Artifacts { get; set; } = new();
    public double? Confidence { get; set; }
}

/// <summary>一个子任务的完整记录：本地存档、界面展示、交回主助手都从它来。</summary>
public sealed class SubtaskRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("callId")] public string CallId { get; set; } = "";
    [JsonPropertyName("conversationId")] public string ConversationId { get; set; } = "";
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("expertId")] public string ExpertId { get; set; } = "";
    [JsonPropertyName("expertName")] public string ExpertName { get; set; } = "";
    [JsonPropertyName("expertAvatar")] public string ExpertAvatar { get; set; } = "";
    [JsonPropertyName("expertVersion")] public int ExpertVersion { get; set; }

    /// <summary>派遣时的专家配置快照：之后后台改了专家，这条记录照样能追到当时用的是哪一版。</summary>
    [JsonPropertyName("expertSnapshot")] public string ExpertSnapshot { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("context")] public string Context { get; set; } = "";
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("status")] public string Status { get; set; } = "running";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("findings")] public List<SubtaskFinding> Findings { get; set; } = new();
    [JsonPropertyName("openQuestions")] public List<string> OpenQuestions { get; set; } = new();
    [JsonPropertyName("nextSuggestions")] public List<string> NextSuggestions { get; set; } = new();
    [JsonPropertyName("artifacts")] public List<string> Artifacts { get; set; } = new();
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }
    [JsonPropertyName("toolCalls")] public List<ToolCallStat> ToolCalls { get; set; } = new();
    [JsonPropertyName("promptTokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completionTokens")] public int CompletionTokens { get; set; }

    /// <summary>计入预算的 token（含估算）。</summary>
    [JsonPropertyName("budgetTokens")] public long BudgetTokens { get; set; }

    /// <summary>有模型调用的用量是估算的。</summary>
    [JsonPropertyName("estimated")] public bool Estimated { get; set; }
    [JsonPropertyName("tokenCap")] public long TokenCap { get; set; }
    [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; set; }
    [JsonPropertyName("deadlineAt")] public DateTimeOffset DeadlineAt { get; set; }
    [JsonPropertyName("finishedAt")] public DateTimeOffset? FinishedAt { get; set; }
    [JsonPropertyName("cancelReason")] public string CancelReason { get; set; } = "";
    [JsonPropertyName("errorCode")] public string ErrorCode { get; set; } = "";
    [JsonPropertyName("error")] public string Error { get; set; } = "";

    /// <summary>专家的执行记录（和消息上的执行记录同格式的 JSON）。</summary>
    [JsonPropertyName("trace")] public string? Trace { get; set; }

    public long DurationMs => (long)((FinishedAt ?? DateTimeOffset.Now) - StartedAt).TotalMilliseconds;
}

public sealed class ToolCallStat
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
}
