using Flyknit.Core.Diagnostics;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>设置 → 网络检测：每一项怎么判定。</summary>
public class NetworkDiagnosticsTests
{
    private sealed class FakeProbe : INetworkProbe
    {
        public string? Hosts;
        public string[] Addresses = ["10.0.0.5"];
        public Exception? DnsError;
        public Exception? TcpError;
        public int Http = 200;
        public Exception? HttpError;
        public int Auth = 200;
        public long?[] Pings = [12, 14, 13, 15];
        private int _ping;
        public bool AuthCalled;

        public string? HostsEntry(string host) => Hosts;
        public Task<string[]> ResolveAsync(string host, CancellationToken ct) => DnsError is null ? Task.FromResult(Addresses) : Task.FromException<string[]>(DnsError);
        public Task ConnectAsync(string host, int port, CancellationToken ct) => TcpError is null ? Task.CompletedTask : Task.FromException(TcpError);
        public Task<int> HttpAsync(CancellationToken ct) => HttpError is null ? Task.FromResult(Http) : Task.FromException<int>(HttpError);
        public Task<int> AuthAsync(CancellationToken ct) { AuthCalled = true; return Task.FromResult(Auth); }
        public Task<long?> PingAsync(string host, CancellationToken ct) => Task.FromResult(Pings[_ping++ % Pings.Length]);
    }

    private static readonly ProxyInfo Direct = new("direct", "");

    private static Task<NetworkReport> Run(FakeProbe probe, ProxyInfo? proxy = null, string url = "https://ai.corp.local:8443/") =>
        NetworkDiagnostics.RunAsync(url, proxy ?? Direct, probe, CancellationToken.None);

    private static CheckResult Get(NetworkReport r, string id) => r.Checks.Single(c => c.Id == id);

    [Fact]
    public void AHealthyNetworkPassesEverything()
    {
        var r = Run(new FakeProbe()).Result;
        Assert.Equal(CheckStatus.Pass, r.Overall);
        Assert.Equal(("ai.corp.local", 8443, "HTTPS"), (r.Host, r.Port, r.Scheme));
        Assert.Equal(["proxy", "hosts", "dns", "http", "auth", "tcp", "loss"], r.Checks.Select(c => c.Id));
        Assert.Equal("noProxy", Get(r, "proxy").Code);
        Assert.Equal("ai.corp.local:8443", Get(r, "tcp").Value);
        Assert.Equal(("0%", 14L), (Get(r, "loss").Value, Get(r, "loss").Ms!.Value));
    }

    [Fact]
    public void ServerDownFailsAndSkipsAuth()
    {
        var probe = new FakeProbe { TcpError = new IOException("refused"), HttpError = new HttpRequestException("refused") };
        var r = Run(probe).Result;
        Assert.Equal(CheckStatus.Fail, r.Overall);
        Assert.Equal(CheckStatus.Fail, Get(r, "tcp").Status);
        Assert.Equal("httpError", Get(r, "http").Code);
        Assert.Equal(CheckStatus.Skip, Get(r, "auth").Status);
        Assert.False(probe.AuthCalled);
    }

    [Theory]
    [InlineData(401, "authInvalid")]
    [InlineData(403, "authDisabled")]
    [InlineData(500, "authStatus")]
    public void ARejectedDeviceTokenIsReported(int status, string code)
    {
        var r = Run(new FakeProbe { Auth = status }).Result;
        Assert.Equal((CheckStatus.Fail, code), (Get(r, "auth").Status, Get(r, "auth").Code));
    }

    [Fact]
    public void BlockedPingIsOnlyAWarning()
    {
        var r = Run(new FakeProbe { Pings = [null] }).Result;
        Assert.Equal((CheckStatus.Warn, "icmpBlocked", "100%"), (Get(r, "loss").Status, Get(r, "loss").Code, Get(r, "loss").Value));
        Assert.Equal(CheckStatus.Warn, r.Overall);

        var some = Run(new FakeProbe { Pings = [10, null, 12, 14] }).Result;
        Assert.Equal(("lossSome", "25%"), (Get(some, "loss").Code, Get(some, "loss").Value));
    }

    [Fact]
    public void BehindAProxyDirectFailuresAreWarnings()
    {
        var proxy = new ProxyInfo("manual", "http://10.0.0.8:8080");
        var r = Run(new FakeProbe { DnsError = new IOException("no such host") }, proxy).Result;
        Assert.Equal(("proxy", "http://10.0.0.8:8080"), (Get(r, "proxy").Code, Get(r, "proxy").Value));
        Assert.Equal(CheckStatus.Warn, Get(r, "dns").Status);
        Assert.Equal(CheckStatus.Pass, Get(r, "http").Status);
        Assert.Equal(CheckStatus.Skip, Get(r, "loss").Status);

        var bad = Run(new FakeProbe(), new ProxyInfo("manual", "not a url", Valid: false)).Result;
        Assert.Equal((CheckStatus.Fail, "badProxy"), (Get(bad, "proxy").Status, Get(bad, "proxy").Code));
    }

    [Fact]
    public void DnsFailureWithoutProxyFails()
    {
        var r = Run(new FakeProbe { DnsError = new IOException("no such host") }).Result;
        Assert.Equal(CheckStatus.Fail, Get(r, "dns").Status);
        Assert.Equal(CheckStatus.Skip, Get(r, "tcp").Status);
    }

    [Fact]
    public void IpAddressesSkipHostsAndDns()
    {
        var probe = new FakeProbe { Hosts = "1.2.3.4" };
        var r = Run(probe, url: "http://10.0.0.5:8000").Result;
        Assert.Equal(("ipLiteral", CheckStatus.Pass), (Get(r, "hosts").Code, Get(r, "hosts").Status));
        Assert.Equal("ipLiteral", Get(r, "dns").Code);
    }

    [Fact]
    public void AHostsOverrideIsFlagged()
    {
        var r = Run(new FakeProbe { Hosts = "192.168.1.9" }).Result;
        Assert.Equal((CheckStatus.Warn, "hostsEntry", "192.168.1.9 ai.corp.local"), (Get(r, "hosts").Status, Get(r, "hosts").Code, Get(r, "hosts").Value));
    }

    [Fact]
    public void HostsFileParsing()
    {
        const string hosts = "# 127.0.0.1 ai.corp.local\r\n\r\n10.0.0.1\tfoo bar   # comment\r\n 10.0.0.2 AI.Corp.Local\r\n";
        Assert.Equal("10.0.0.2", NetworkDiagnostics.FindHostsEntry(hosts, "ai.corp.local"));
        Assert.Equal("10.0.0.1", NetworkDiagnostics.FindHostsEntry(hosts, "bar"));
        Assert.Null(NetworkDiagnostics.FindHostsEntry(hosts, "comment"));
        Assert.Null(NetworkDiagnostics.FindHostsEntry(hosts, "10.0.0.1"));
    }

    [Fact]
    public void ABadEndpointIsReported()
    {
        var r = Run(new FakeProbe(), url: "not a url").Result;
        Assert.Equal(CheckStatus.Fail, r.Overall);
        Assert.Equal("badEndpoint", r.Checks.Single().Code);
    }

    [Fact]
    public void TheReportListsEveryCheck()
    {
        var r = Run(new FakeProbe { Auth = 401 }).Result;
        var text = NetworkDiagnostics.FormatReport(r, new Dictionary<string, string> { ["version"] = "1.2.3" });
        Assert.Contains("总体结果: 失败", text);
        Assert.Contains("服务端点: https://ai.corp.local:8443", text);
        Assert.Contains("authInvalid", text);
        Assert.Contains("version: 1.2.3", text);
        Assert.Equal(7, r.Checks.Count(c => text.Contains($" {c.Id}")));
    }
}
