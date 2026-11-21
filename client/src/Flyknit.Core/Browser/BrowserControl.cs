namespace Flyknit.Core.Browser;

/// <summary>
/// 员工在 AI 浏览器地址栏里输入的内容 → 要打开的网址。只认 http / https（javascript:、file: 这类一律不开）；
/// 没写协议时按网址补上：内网主机名、localhost、IP 用 http，其余用 https。不是网址的（有空格等）返回 null，
/// 不拿去搜索引擎搜（员工输入的内容不该被送到外部网站）。
/// </summary>
public static class BrowserAddress
{
    public static string? Normalize(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
        {
            return null;
        }
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(text, UriKind.Absolute, out var full) && full.Scheme is "http" or "https" && full.Host.Length > 0
                ? full.ToString()
                : null;
        }
        // 没写协议：开头到第一个 / ? # 是「主机[:端口]」。「javascript:alert(1)」「mailto:a@b」这类冒号后面不是端口的，不开
        var hostPort = text.Split('/', '?', '#')[0].Split(':');
        if (hostPort.Length > 2 || hostPort[0].Length == 0 || (hostPort.Length == 2 && (hostPort[1].Length == 0 || !hostPort[1].All(char.IsAsciiDigit))))
        {
            return null;
        }
        var host = hostPort[0];
        var plain = host == "localhost" || !host.Contains('.') || System.Net.IPAddress.TryParse(host, out _);
        return Uri.TryCreate((plain ? "http://" : "https://") + text, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.ToString()
            : null;
    }
}

/// <summary>谁在操作 AI 浏览器。</summary>
public enum BrowserController
{
    /// <summary>没人在用（或 AI 这一轮已结束）：员工可以随便用。</summary>
    Idle,

    /// <summary>AI 正在操作：页面上盖一层遮罩，员工点不了，免得打乱 AI（点「接管」可以拿回来）。</summary>
    Ai,

    /// <summary>员工点了「接管」：AI 下一步浏览器操作会等员工点「交还给 AI」。</summary>
    User,
}

/// <summary>
/// AI 浏览器的控制权（参照 Cursor 的 Take Control）：
/// - AI 一开始操作就进入 <see cref="BrowserController.Ai"/>，直到这一轮任务结束（<see cref="Release"/>）；
/// - 员工点「接管」→ <see cref="BrowserController.User"/>，遮罩去掉；AI 再要操作浏览器时等着，
///   员工点「交还给 AI」后接着做（任务被停止时等待随之取消，不会一直挂着）。
/// 不涉及界面，窗口根据 <see cref="Changed"/> 更新遮罩和工具栏。
/// </summary>
public sealed class BrowserControl
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _handBack;
    private int _waiters;

    public BrowserController State { get; private set; } = BrowserController.Idle;

    /// <summary>状态变了（在调用方线程上触发）。</summary>
    public event Action<BrowserController>? Changed;

    /// <summary>
    /// AI 要做一步浏览器操作之前调用：员工接管着就等交还；否则进入 AI 操作状态。
    /// <paramref name="waiting"/> 在开始等待时调用一次（让 AI 那边的状态显示「等你交还」）。
    /// </summary>
    public async Task AcquireAsync(CancellationToken ct, Action? waiting = null)
    {
        Task? wait;
        lock (_gate)
        {
            wait = State == BrowserController.User ? (_handBack ??= NewSignal()).Task : null;
            if (wait is not null)
            {
                _waiters++;
            }
        }
        if (wait is not null)
        {
            waiting?.Invoke();
            try
            {
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 任务被停止：没人再等了就收回信号，免得员工交还时被当成「AI 在操作」、遮罩一直盖着
                lock (_gate)
                {
                    if (--_waiters == 0 && State == BrowserController.User)
                    {
                        _handBack = null;
                    }
                }
                throw;
            }
            lock (_gate)
            {
                _waiters = Math.Max(0, _waiters - 1);
            }
        }
        Set(BrowserController.Ai, only: BrowserController.Idle);
    }

    /// <summary>员工点「接管」。没人在用时也可以点（之后 AI 要用时会先等交还）。</summary>
    public void TakeOver() => Set(BrowserController.User);

    /// <summary>员工点「交还给 AI」：在等的 AI 操作接着做。</summary>
    public void HandBack()
    {
        TaskCompletionSource<bool>? signal;
        lock (_gate)
        {
            if (State != BrowserController.User)
            {
                return;
            }
            signal = _handBack;
            _handBack = null;
            State = signal is null ? BrowserController.Idle : BrowserController.Ai;
        }
        signal?.TrySetResult(true);
        Changed?.Invoke(State);
    }

    /// <summary>这一轮任务结束：AI 不再操作，遮罩去掉（员工接管着的保持不变）。</summary>
    public void Release() => Set(BrowserController.Idle, only: BrowserController.Ai);

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Set(BrowserController next, BrowserController? only = null)
    {
        lock (_gate)
        {
            if (State == next || (only is { } from && State != from))
            {
                return;
            }
            State = next;
        }
        Changed?.Invoke(next);
    }
}
