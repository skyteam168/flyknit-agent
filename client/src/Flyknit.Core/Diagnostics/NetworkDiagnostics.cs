using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Flyknit.Core.Diagnostics;

public enum CheckStatus
{
    Pass,
    /// <summary>不一定是故障，但值得留意（比如 ICMP 被防火墙挡了、hosts 里写死了地址）。</summary>
    Warn,
    Fail,
    /// <summary>前面一步失败了，这一项没法测。</summary>
    Skip,
}

/// <summary>
/// 一项检测的结果。<see cref="Code"/> 是给界面翻译用的结果代号（noProxy、tcpOk、icmpBlocked…），
/// <see cref="Value"/> 是右侧显示的数据（地址、毫秒数、丢包率），<see cref="Error"/> 是原始报错。
/// </summary>
public sealed record CheckResult(string Id, CheckStatus Status, string Code, string Value, long? Ms = null, string Error = "");

/// <summary>代理在这次检测里实际是怎么走的。</summary>
public sealed record ProxyInfo(string Mode, string Url, bool Valid = true)
{
    public bool InUse => Url.Length > 0;
}

public sealed record NetworkReport(
    DateTimeOffset At,
    string Endpoint,
    string Host,
    int Port,
    string Scheme,
    ProxyInfo Proxy,
    IReadOnlyList<CheckResult> Checks)
{
    public CheckStatus Overall =>
        Checks.Any(c => c.Status == CheckStatus.Fail) ? CheckStatus.Fail
        : Checks.Any(c => c.Status == CheckStatus.Warn) ? CheckStatus.Warn
        : CheckStatus.Pass;
}

/// <summary>检测里要碰网络的那几步。真实实现是 <see cref="SystemNetworkProbe"/>，测试里换成假的。</summary>
public interface INetworkProbe
{
    /// <summary>hosts 文件里给这个主机名写死的地址，没有返回 null。</summary>
    string? HostsEntry(string host);

    Task<string[]> ResolveAsync(string host, CancellationToken ct);

    Task ConnectAsync(string host, int port, CancellationToken ct);

    /// <summary>访问服务端的健康检查地址（走和平时一样的代理设置），返回 HTTP 状态码。</summary>
    Task<int> HttpAsync(CancellationToken ct);

    /// <summary>用这台电脑的设备令牌访问一次服务端，返回 HTTP 状态码。</summary>
    Task<int> AuthAsync(CancellationToken ct);

    /// <summary>ICMP ping 一次，返回往返毫秒数；超时或失败返回 null。</summary>
    Task<long?> PingAsync(string host, CancellationToken ct);
}

/// <summary>
/// 设置 → 网络 → 网络检测：从代理、hosts、DNS、TCP、HTTP、设备认证到丢包，一项项排查员工电脑到服务端的链路，
/// 并生成一份文本报告给 IT。界面只负责展示，判定规则都在这里。
/// </summary>
public static class NetworkDiagnostics
{
    public const int PingCount = 4;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

    public static async Task<NetworkReport> RunAsync(string serverUrl, ProxyInfo proxy, INetworkProbe probe, CancellationToken ct, Func<DateTimeOffset>? now = null)
    {
        var at = (now ?? (() => DateTimeOffset.Now))();
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            return new NetworkReport(at, serverUrl, "", 0, "", proxy,
                [new CheckResult("endpoint", CheckStatus.Fail, "badEndpoint", serverUrl)]);
        }
        var host = uri.Host;
        var port = uri.Port;
        var checks = new List<CheckResult>();

        // 1. 代理：手动代理填错了，后面的 HTTP 一定不通，先指出来
        checks.Add(!proxy.Valid
            ? new CheckResult("proxy", CheckStatus.Fail, "badProxy", proxy.Url)
            : proxy.InUse
                ? new CheckResult("proxy", CheckStatus.Pass, "proxy", proxy.Url)
                : new CheckResult("proxy", CheckStatus.Pass, "noProxy", "No Proxy"));

        // 2. hosts：写死了地址不一定错，但换服务器后忘了改是常见故障，标黄提醒
        var isIp = IPAddress.TryParse(host, out _);
        var hosts = isIp ? null : probe.HostsEntry(host);
        checks.Add(hosts is null
            ? new CheckResult("hosts", CheckStatus.Pass, isIp ? "ipLiteral" : "noHostsEntry", isIp ? host : "No explicit hosts entry")
            : new CheckResult("hosts", CheckStatus.Warn, "hostsEntry", $"{hosts} {host}"));

        // 3. DNS
        var resolved = false;
        if (isIp)
        {
            resolved = true;
            checks.Add(new CheckResult("dns", CheckStatus.Pass, "ipLiteral", host));
        }
        else
        {
            var (addresses, ms, error) = await Timed(() => probe.ResolveAsync(host, ct), ct);
            resolved = addresses is { Length: > 0 };
            checks.Add(resolved
                ? new CheckResult("dns", CheckStatus.Pass, "dnsOk", string.Join(", ", addresses!.Take(3)), ms)
                // 走代理时由代理去解析，本机解析不了也能用
                : new CheckResult("dns", proxy.InUse ? CheckStatus.Warn : CheckStatus.Fail, "dnsFail", host, ms, error));
        }

        // 4~7 互不依赖，一起跑；ping 要好几秒，别让它拖着别的
        var tcpTask = resolved || proxy.InUse ? Tcp() : Task.FromResult(new CheckResult("tcp", CheckStatus.Skip, "skipped", $"{host}:{port}"));
        var httpTask = Http();
        var pingTask = resolved ? Ping() : Task.FromResult(new CheckResult("loss", CheckStatus.Skip, "skipped", ""));
        var http = await httpTask;
        var authTask = http.Status == CheckStatus.Pass ? Auth() : Task.FromResult(new CheckResult("auth", CheckStatus.Skip, "skipped", ""));
        checks.Add(http);
        checks.Add(await authTask);
        checks.Add(await tcpTask);
        checks.Add(await pingTask);

        return new NetworkReport(at, serverUrl.TrimEnd('/'), host, port, uri.Scheme.ToUpperInvariant(), proxy, checks);

        async Task<CheckResult> Tcp()
        {
            var (_, ms, error) = await Timed(async () => { await probe.ConnectAsync(host, port, ct); return true; }, ct);
            if (error.Length == 0)
            {
                return new CheckResult("tcp", CheckStatus.Pass, "tcpOk", $"{host}:{port}", ms);
            }
            // 有些公司只放行代理出去，直连端口不通但走代理能用
            return new CheckResult("tcp", proxy.InUse ? CheckStatus.Warn : CheckStatus.Fail, "tcpFail", $"{host}:{port}", ms, error);
        }

        async Task<CheckResult> Http()
        {
            var (code, ms, error) = await Timed(() => probe.HttpAsync(ct), ct);
            if (error.Length > 0)
            {
                return new CheckResult("http", CheckStatus.Fail, "httpError", "", ms, error);
            }
            return code is >= 200 and < 400
                ? new CheckResult("http", CheckStatus.Pass, "httpOk", code.ToString(), ms)
                : new CheckResult("http", CheckStatus.Fail, "httpStatus", code.ToString(), ms);
        }

        async Task<CheckResult> Auth()
        {
            var (code, ms, error) = await Timed(() => probe.AuthAsync(ct), ct);
            return (code, error.Length > 0) switch
            {
                (_, true) => new CheckResult("auth", CheckStatus.Fail, "authError", "", ms, error),
                (>= 200 and < 300, _) => new CheckResult("auth", CheckStatus.Pass, "authOk", code.ToString(), ms),
                (401, _) => new CheckResult("auth", CheckStatus.Fail, "authInvalid", "401", ms),
                (403, _) => new CheckResult("auth", CheckStatus.Fail, "authDisabled", "403", ms),
                _ => new CheckResult("auth", CheckStatus.Fail, "authStatus", code.ToString(), ms),
            };
        }

        async Task<CheckResult> Ping()
        {
            var watch = Stopwatch.StartNew();
            var replies = new List<long>();
            for (var i = 0; i < PingCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                long? rtt;
                try
                {
                    rtt = await probe.PingAsync(host, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    rtt = null;
                }
                if (rtt is { } r)
                {
                    replies.Add(r);
                }
            }
            var loss = (PingCount - replies.Count) * 100 / PingCount;
            var avg = replies.Count > 0 ? (long?)Math.Round(replies.Average()) : null;
            return replies.Count switch
            {
                // 一个都没回：多半是防火墙不让 ping，不代表网络断了（后面 TCP/HTTP 才是准的）
                0 => new CheckResult("loss", CheckStatus.Warn, "icmpBlocked", "100%", watch.ElapsedMilliseconds),
                PingCount => new CheckResult("loss", CheckStatus.Pass, "lossNone", "0%", avg),
                _ => new CheckResult("loss", CheckStatus.Warn, "lossSome", $"{loss}%", avg),
            };
        }
    }

    /// <summary>跑一步、计时、兜住异常和超时。</summary>
    private static async Task<(T? Value, long Ms, string Error)> Timed<T>(Func<Task<T>> step, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StepTimeout);
        try
        {
            var task = step();
            var done = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeout.Token));
            if (done != task)
            {
                ct.ThrowIfCancellationRequested();
                return (default, watch.ElapsedMilliseconds, "timeout");
            }
            return (await task, watch.ElapsedMilliseconds, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (default, watch.ElapsedMilliseconds, "timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (default, watch.ElapsedMilliseconds, Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        // HttpRequestException 外面那层常常只说「发送请求出错」，真正原因在里面
        var inner = ex;
        while (inner.InnerException is not null && inner is not SocketException)
        {
            inner = inner.InnerException;
        }
        return inner == ex ? ex.Message : $"{ex.Message} ({inner.Message})";
    }

    /// <summary>
    /// 在 hosts 文件内容里找给 <paramref name="host"/> 写死的地址。忽略注释和空行，一行可以写多个主机名。
    /// </summary>
    public static string? FindHostsEntry(string content, string host)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }
            var parts = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts.Skip(1).Any(p => p.Equals(host, StringComparison.OrdinalIgnoreCase)))
            {
                return parts[0];
            }
        }
        return null;
    }

    /// <summary>给 IT 看的文本报告。</summary>
    public static string FormatReport(NetworkReport report, IReadOnlyDictionary<string, string> device)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FlyknitBuddy 网络检测报告");
        sb.AppendLine($"检测时间: {report.At:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"总体结果: {Label(report.Overall)}");
        sb.AppendLine();
        sb.AppendLine("[目标]");
        sb.AppendLine($"服务端点: {report.Endpoint}");
        sb.AppendLine($"目标主机: {report.Host}:{report.Port} ({report.Scheme})");
        sb.AppendLine($"代理模式: {report.Proxy.Mode}{(report.Proxy.InUse ? $" -> {report.Proxy.Url}" : " (直连)")}");
        sb.AppendLine();
        sb.AppendLine("[检测项]");
        foreach (var c in report.Checks)
        {
            sb.Append($"{Label(c.Status),-4} {c.Id,-6} {c.Code}");
            if (c.Value.Length > 0)
            {
                sb.Append($"  {c.Value}");
            }
            if (c.Ms is { } ms)
            {
                sb.Append($"  {ms} ms");
            }
            if (c.Error.Length > 0)
            {
                sb.Append($"  错误: {c.Error}");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("[设备]");
        foreach (var (k, v) in device)
        {
            sb.AppendLine($"{k}: {v}");
        }
        return sb.ToString();
    }

    private static string Label(CheckStatus s) => s switch
    {
        CheckStatus.Pass => "通过",
        CheckStatus.Warn => "注意",
        CheckStatus.Fail => "失败",
        _ => "跳过",
    };
}

/// <summary>真实的网络探测。HTTP 和设备认证交给调用方（要走和平时一样的代理与令牌）。</summary>
public sealed class SystemNetworkProbe(Func<CancellationToken, Task<int>> http, Func<CancellationToken, Task<int>> auth) : INetworkProbe
{
    public string? HostsEntry(string host)
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
            if (!OperatingSystem.IsWindows())
            {
                path = "/etc/hosts";
            }
            return File.Exists(path) ? NetworkDiagnostics.FindHostsEntry(File.ReadAllText(path), host) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<string[]> ResolveAsync(string host, CancellationToken ct) =>
        (await Dns.GetHostAddressesAsync(host, ct)).Select(a => a.ToString()).ToArray();

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
    }

    public Task<int> HttpAsync(CancellationToken ct) => http(ct);

    public Task<int> AuthAsync(CancellationToken ct) => auth(ct);

    public async Task<long?> PingAsync(string host, CancellationToken ct)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(host, 1500);
        return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
    }
}
