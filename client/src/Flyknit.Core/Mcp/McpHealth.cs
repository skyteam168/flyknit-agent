namespace Flyknit.Core.Mcp;

/// <summary>
/// 断线后要不要重连、等多久。
///
/// 远程服务：按 1、2、4、8、16 秒退避重连五次（和 Claude Code 一样）。
/// 本机进程：以前一律不重启——一启动就崩的多半是配置或环境问题，反复拉起只会反复失败。
/// 但逆向分析、浏览器自动化这类工具跑了很久才崩（内存、偶发异常），重启一下就能接着用，
/// 所以改成：连上不到 <see cref="MinStdioUptime"/> 就崩的照旧不重启；跑稳过的，10 分钟内最多重启 <see cref="MaxStdioRestarts"/> 次。
/// </summary>
public static class McpReconnectPolicy
{
    public const int MaxRemoteRetries = 5;
    public const int MaxStdioRestarts = 3;
    public static readonly TimeSpan MinStdioUptime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StdioWindow = TimeSpan.FromMinutes(10);

    /// <param name="retries">远程：这次断线后已经重连了几次。</param>
    /// <param name="uptime">本机进程：这次连上后跑了多久才断。</param>
    /// <param name="recentRestarts">本机进程：最近 10 分钟里已经自动重启过几次。</param>
    /// <returns>等多久再连；null 表示不重连，标成失败等员工处理。</returns>
    public static TimeSpan? Next(string transport, int retries, TimeSpan uptime, int recentRestarts)
    {
        if (transport != "stdio")
        {
            return retries < MaxRemoteRetries ? TimeSpan.FromSeconds(Math.Pow(2, retries)) : null;
        }
        if (uptime < MinStdioUptime || recentRestarts >= MaxStdioRestarts)
        {
            return null;
        }
        return TimeSpan.FromSeconds(Math.Pow(2, recentRestarts));
    }

    /// <summary>只留窗口期内的重启时间，返回剩下几次。</summary>
    public static int Recent(List<DateTimeOffset> restarts, DateTimeOffset now)
    {
        restarts.RemoveAll(t => now - t > StdioWindow);
        return restarts.Count;
    }
}

/// <summary>
/// 定时 ping 一个连接，记下往返耗时；连续 <see cref="FailuresBeforeUnhealthy"/> 次没回应就报一次 <see cref="Unhealthy"/>，然后停下
/// （之后由调用方断开重连，新连接配新的监视器）。
///
/// 有调用还没回来时这一轮不 ping：很多 MCP 服务是单线程的，正在跑一个几分钟的分析时 ping 回不来不代表它坏了，
/// 误判成断线去重连反而会把正在跑的任务杀掉。
/// </summary>
public sealed class McpHealthMonitor : IAsyncDisposable
{
    public const int FailuresBeforeUnhealthy = 2;

    private readonly Func<CancellationToken, Task<TimeSpan>> _ping;
    private readonly Func<int> _pending;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _failures;

    public McpHealthMonitor(McpConnection connection, TimeSpan interval, TimeSpan timeout)
        : this(ct => connection.PingAsync(timeout, ct), () => connection.Client.PendingCount, interval, timeout)
    {
        Latency += l => connection.LastLatency = l;
        connection.Log?.Write($"健康检查：每 {interval.TotalSeconds:0} 秒 ping 一次");
    }

    public McpHealthMonitor(Func<CancellationToken, Task<TimeSpan>> ping, Func<int> pending, TimeSpan interval, TimeSpan timeout)
    {
        _ping = ping;
        _pending = pending;
        _interval = interval;
        _timeout = timeout;
    }

    /// <summary>每次 ping 成功的往返耗时。</summary>
    public event Action<TimeSpan>? Latency;

    /// <summary>连续几次 ping 不通。只报一次。</summary>
    public event Action<string>? Unhealthy;

    public void Start() => _loop ??= Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (_pending() > 0)
            {
                _failures = 0; // 正在干活就是活着
                continue;
            }
            string? error = null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                cts.CancelAfter(_timeout + TimeSpan.FromSeconds(1));
                var latency = await _ping(cts.Token);
                _failures = 0;
                Latency?.Invoke(latency);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            if (error is not null && ++_failures >= FailuresBeforeUnhealthy)
            {
                Unhealthy?.Invoke($"连续 {_failures} 次健康检查没有回应（{error}）");
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        _stop.Dispose();
    }
}
