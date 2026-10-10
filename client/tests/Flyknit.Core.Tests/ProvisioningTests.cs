using Flyknit.Core.Setup;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>安装包自带的开通文件，以及“这是域账号还是本机账号”的判断。</summary>
public class ProvisioningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-provision").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private void Write(string json) => File.WriteAllText(Path.Combine(_dir, Provisioning.FileName), json);

    [Fact]
    public void AValidFileGivesTheServerAndTicket()
    {
        Write("""{"server_url": "http://10.0.0.5:8000/", "ticket": "abc123", "label": "三车间", "version": "0.3.0"}""");
        Assert.Equal(new Provision("http://10.0.0.5:8000", "abc123", "三车间"), Provisioning.Load(_dir));
    }

    [Theory]
    [InlineData(null)]                                                     // 没有文件：手动填服务器
    [InlineData("not json")]
    [InlineData("""{"server_url": "http://10.0.0.5:8000"}""")]             // 没有凭证
    [InlineData("""{"server_url": "10.0.0.5:8000", "ticket": "abc"}""")]   // 地址不完整
    [InlineData("""{"server_url": "ftp://x", "ticket": "abc"}""")]
    [InlineData("""{"server_url": 5, "ticket": "abc"}""")]
    public void AnythingElseFallsBackToManualSetup(string? json)
    {
        if (json is not null)
        {
            Write(json);
        }
        Assert.Null(Provisioning.Load(_dir));
    }

    [Theory]
    // IT 第一次下载时填成了 localhost，重新下载正确地址的安装程序重装：员工要按新地址重新登录
    [InlineData("http://localhost:5180", "http://10.0.0.5:8000", "http://localhost:5180", true)]
    [InlineData("http://localhost:5180", "http://10.0.0.5:8000", "http://localhost:5180/", true)]
    // 没换过地址
    [InlineData("http://10.0.0.5:8000", "http://10.0.0.5:8000", "http://10.0.0.5:8000", false)]
    // 员工自己用「连接其他服务器」登录的，不去动它
    [InlineData("http://10.0.0.5:8000", "http://10.0.0.6:8000", "http://192.168.1.9:8000", false)]
    // 已经按新地址登录过了
    [InlineData("http://localhost:5180", "http://10.0.0.5:8000", "http://10.0.0.5:8000", false)]
    public void ReinstallingWithANewServerAsksToLogInAgain(string kept, string installed, string current, bool expected)
    {
        var program = Directory.CreateDirectory(Path.Combine(_dir, "program")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(_dir, "data")).FullName;
        File.WriteAllText(Path.Combine(data, Provisioning.FileName), $$"""{"server_url": "{{kept}}", "ticket": "old"}""");
        File.WriteAllText(Path.Combine(program, Provisioning.FileName), $$"""{"server_url": "{{installed}}", "ticket": "new"}""");
        Assert.Equal(expected, Provisioning.ServerReplaced(program, data, current));
    }

    [Fact]
    public void WithoutBothFilesNothingIsReplaced()
    {
        var program = Directory.CreateDirectory(Path.Combine(_dir, "program")).FullName;
        Write("""{"server_url": "http://localhost:5180", "ticket": "old"}""");
        Assert.False(Provisioning.ServerReplaced(program, _dir, "http://localhost:5180"));
    }

    [Fact]
    public void ACopyIsKeptForAfterTheProgramFolderIsReplaced()
    {
        var program = Path.Combine(_dir, "program");
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, Provisioning.FileName), """{"server_url": "http://10.0.0.5:8000", "ticket": "abc"}""");

        Provisioning.Keep(program, data);
        Directory.Delete(program, true);                    // 自动升级换掉了程序目录
        Directory.CreateDirectory(program);
        Assert.Equal("abc", Provisioning.Load(program, data)!.Ticket);

        // 新解压的安装包优先
        File.WriteAllText(Path.Combine(program, Provisioning.FileName), """{"server_url": "http://10.0.0.6:8000", "ticket": "new"}""");
        Assert.Equal("new", Provisioning.Load(program, data)!.Ticket);
    }

    [Theory]
    [InlineData("SHENZHOU\\nguyen.van.a", "nguyen.van.a", "SHENZHOU", "Domain", "SHENZHOU\\nguyen.van.a")]
    [InlineData(" worker ", "worker", ".", "Local", "PC-QC-01\\worker")]
    [InlineData(".\\worker", "worker", ".", "Local", "PC-QC-01\\worker")]
    [InlineData("pc-qc-01\\worker", "worker", ".", "Local", "PC-QC-01\\worker")]
    [InlineData("nguyen@shenzhou.com", "nguyen@shenzhou.com", null, "Domain", "shenzhou.com\\nguyen")]
    public void TypedAccountsAreHandedToWindowsCorrectly(string input, string logonUser, string? logonDomain, string kind, string display)
    {
        var parsed = AccountInput.Parse(input, "PC-QC-01")!;
        Assert.Equal(logonUser, parsed.LogonUser);
        Assert.Equal(logonDomain, parsed.LogonDomain);
        Assert.Equal(kind, parsed.Account.Kind.ToString());
        Assert.Equal(display, parsed.Account.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SHENZHOU\\")]
    public void EmptyAccountsAreRejected(string input) => Assert.Null(AccountInput.Parse(input, "PC-QC-01"));

    [Fact]
    public void DomainAccountsAreThoseNotOwnedByThisMachine()
    {
        var domain = new WindowsAccount("CORP", "nguyen.van.a", "PC-QC-01");
        Assert.Equal(LoginKind.Domain, domain.Kind);
        Assert.Equal("CORP\\nguyen.van.a", domain.Display);

        // 本机账号（包括用微软账号登录的本机用户）：域名就是计算机名
        Assert.Equal(LoginKind.Local, new WindowsAccount("PC-QC-01", "worker", "PC-QC-01").Kind);
        Assert.Equal(LoginKind.Local, new WindowsAccount("pc-qc-01", "worker", "PC-QC-01").Kind);
        Assert.Equal(LoginKind.Local, new WindowsAccount("", "worker", "PC-QC-01").Kind);

        // Azure AD 加入的电脑：账号由云端目录验证，也算域账号
        Assert.Equal(LoginKind.Domain, new WindowsAccount("AzureAD", "nguyen", "PC-QC-02").Kind);
    }
}
