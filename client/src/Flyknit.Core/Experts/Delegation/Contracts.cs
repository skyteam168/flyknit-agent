using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Security;
using Flyknit.Core.Gateway;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Experts.Delegation;

/// <summary>向服务端登记一次派遣。</summary>
public sealed record SubtaskRegistration(
    string RunId,
    string ConversationId,
    string ExpertId,
    int ExpertVersion,
    string Task,
    long TokenCap,
    int TimeoutSeconds);

/// <summary>服务端同意派遣后给的凭据。</summary>
public sealed record SubtaskTicket(string Id, int Seq, long TokenCap, DateTimeOffset DeadlineAt, int PublishedVersion);

/// <summary>结束子任务时报给服务端的内容。</summary>
public sealed record SubtaskFinish(
    string Status,
    string Summary,
    JsonNode? Result,
    string ErrorCode,
    string Error,
    int ToolCalls,
    long LocalTokens);

/// <summary>服务端那边子任务的状态。</summary>
public sealed record SubtaskServerState(
    string Id,
    string Status,
    bool Final,
    bool AlreadyFinal,
    int PromptTokens,
    int CompletionTokens,
    int EstimatedCalls,
    string ErrorCode);

/// <summary>服务端拒绝派遣：开关关着、专家不可用、次数用完。</summary>
public sealed class DelegationRejectedException : Exception
{
    public DelegationRejectedException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>delegation_disabled / expert_unavailable / delegation_limit / …</summary>
    public string Code { get; }
}

/// <summary>
/// 服务端的派遣登记。授权、计次（每个主任务最多 3 次）、子任务的时限和 token 硬上限都在服务端：
/// 员工端说了不算，模型想绕也绕不过去。
/// </summary>
public interface ISubtaskRegistry
{
    /// <summary>登记一次派遣；服务端不同意时抛 <see cref="DelegationRejectedException"/>。</summary>
    Task<SubtaskTicket> RegisterAsync(SubtaskRegistration registration, CancellationToken ct);

    /// <summary>报告结果。服务端已经是终态（超时、开关关闭被它终止）时不覆盖，返回它那边的状态。</summary>
    Task<SubtaskServerState> FinishAsync(string id, SubtaskFinish finish, CancellationToken ct);

    Task<SubtaskServerState> CancelAsync(string id, string reason, CancellationToken ct);
}

/// <summary>本机的子任务存档（界面恢复、详情、执行记录都从这里取）。</summary>
public interface ISubtaskStore
{
    void SaveSubtask(SubtaskRecord record);
    SubtaskRecord? GetSubtask(string id);
    IReadOnlyList<SubtaskRecord> SubtasksOf(string conversationId);

    /// <summary>
    /// 程序上次退出时还没结束的子任务：进程都没了，没法恢复，标成失败（interrupted）并返回，
    /// 由调用方再通知服务端。不假装它们还在跑或者已经成功。
    /// </summary>
    IReadOnlyList<SubtaskRecord> MarkInterruptedSubtasks();
}

/// <summary>派遣过程推给界面的事件。seq 在一个子任务内单调递增，界面据此丢掉迟到的旧事件。</summary>
public interface IDelegationEvents
{
    void Started(SubtaskRecord record, int seq);
    void Step(SubtaskRecord record, int seq, string kind, string name, string summary, bool? ok);
    void Finished(SubtaskRecord record, int seq);
}

/// <summary>专家这一趟运行要用的东西，由宿主按专家准备（提示词、筛过的工具、模型、选项）。</summary>
public sealed class ExpertRunSetup
{
    /// <summary>专家的系统提示词：基础规则 + 专家身份（工作方式、职责、知识）。</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>按专家限制筛过的工具。派遣模块还会再去掉禁用的、套上守卫。</summary>
    public required ToolRegistry Tools { get; init; }

    public int? ModelId { get; init; }
}

/// <summary>宿主提供的、和具体运行环境有关的部分。</summary>
public interface IExpertRunHost
{
    ExpertRunSetup Prepare(Expert expert, string brief, ToolContext parent);

    /// <summary>专家循环用的选项（步数、工具超时、规划提醒……），token 上限由派遣模块给。</summary>
    AgentOptions Options(long maxTokens);

    /// <summary>执行记录序列化成和消息上一样的 JSON。</summary>
    string SerializeTrace(Trace trace, string stopReason);

    /// <summary>员工确认（主助手用的那个）。</summary>
    IConfirmationHandler Confirm { get; }

    IAuditSink Audit { get; }

    ApprovalStore Approvals { get; }

    IChatGateway Gateway { get; }

    /// <summary>这个文件是不是本对话里系统登记过的（附件、资料库里的文件）。</summary>
    bool IsRegisteredFile(string conversationId, string fullPath);
}

/// <summary>派遣的各项上限。</summary>
public sealed class DelegationLimits
{
    /// <summary>一次主任务最多派遣几次（服务端同样限制，以服务端为准）。</summary>
    public int MaxPerRun { get; init; } = 3;

    /// <summary>一次派遣最多用多少 token。</summary>
    public long MaxTokensPerSubtask { get; init; } = 400_000;

    /// <summary>派遣后给主助手留着收尾用的 token。</summary>
    public long ReserveForParent { get; init; } = 30_000;

    /// <summary>剩余预算少于这个就不派遣了：专家刚开始干就会被叫停，不如不派。</summary>
    public long MinTokensPerSubtask { get; init; } = 20_000;

    /// <summary>一次派遣的时限。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>专家最多走几步。</summary>
    public int MaxSteps { get; init; } = 40;

    /// <summary>交回主助手的结果最多这么多字，完整内容在子任务记录里。</summary>
    public int MaxResultChars { get; init; } = 6000;

    /// <summary>多久看一次派遣开关有没有被关掉。</summary>
    public TimeSpan SwitchPoll { get; init; } = TimeSpan.FromSeconds(2);
}
