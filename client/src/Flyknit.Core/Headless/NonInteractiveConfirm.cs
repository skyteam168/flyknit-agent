using Flyknit.Core.Agent;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;

namespace Flyknit.Core.Headless;

/// <summary>
/// 命令行 -p 的确认：没有人在看，所以默认一律拒绝。
///
/// 加了 --allow-writes 时，只放行「员工在聊天窗口里也能设成『以后自动执行』」的那一类：
/// 有可记规则（PolicyDecision.Rule）、IT 没要求每次确认（AlwaysAsk）、不是高危（Destructive）。
/// 删除、覆盖、改系统设置、解释器 / 间接执行这些拿不到规则，任何参数都放不行（不变量 3）。
/// 被拒绝的操作记下来，最后写进输出，调用方知道还差哪一步。
/// </summary>
public sealed class NonInteractiveConfirm : IConfirmationHandler
{
    private readonly bool _allowWrites;
    private readonly object _gate = new();

    public NonInteractiveConfirm(bool allowWrites) => _allowWrites = allowWrites;

    public List<(string Tool, string Summary, string Reason)> Declined { get; } = new();
    public List<(string Tool, string Summary)> Approved { get; } = new();

    /// <summary>--allow-writes 能放行的：和「以后自动执行」规则能放行的范围完全一样。</summary>
    public static bool CanAutoApprove(PolicyDecision d) =>
        d.Level == RiskLevel.Confirm && d.Rememberable && d.Effect != CommandEffect.Destructive;

    public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_allowWrites && CanAutoApprove(request.Decision))
            {
                Approved.Add((request.Call.Name, request.Summary));
                return Task.FromResult(ConfirmChoice.AllowOnce);
            }
            // 只有 --allow-writes 真能放行的才提它；运行脚本、访问网络这类加了也不放行，提了反而误导
            var reason = request.Decision.Effect == CommandEffect.Destructive ? "高危操作（删除、覆盖、改系统设置等）只能由员工确认：在员工端或 flyknit 对话模式里做"
                : request.Decision.AlwaysAsk ? "IT 要求这类操作每次都由员工确认：在员工端或 flyknit 对话模式里做"
                : !_allowWrites && CanAutoApprove(request.Decision) ? "命令行默认不放行需要确认的操作（可加 --allow-writes 放行这类普通写入）"
                : "这类操作（运行脚本、访问网络等）不能自动放行，只能由员工确认：在员工端或 flyknit 对话模式里做";
            Declined.Add((request.Call.Name, request.Summary, reason));
            // AllowOnce 以外一律 Reject：命令行从不写「以后自动执行」规则，也不做本次会话授权
            return Task.FromResult(ConfirmChoice.Reject);
        }
    }
}

/// <summary>记下最后一次模型服务错误的状态码：命令行据此给出不同的退出码（额度用完、没登录、连不上）。</summary>
public sealed class RecordingGateway : IChatGateway
{
    private readonly IChatGateway _inner;

    public RecordingGateway(IChatGateway inner) => _inner = inner;

    public GatewayException? LastError { get; private set; }

    public async Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
    {
        try
        {
            var turn = await _inner.CompleteAsync(request, sink, ct);
            LastError = null;
            return turn;
        }
        catch (GatewayException ex)
        {
            LastError = ex;
            throw;
        }
    }
}
