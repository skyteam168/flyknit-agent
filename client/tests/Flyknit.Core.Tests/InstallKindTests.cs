using System.Text.Json;
using Flyknit.Core.Gateway;
using Flyknit.Core.Setup;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>员工端怎么装的：后台据此标出「免安装版」（没有运维代理、不能自动升级）。</summary>
public class InstallKindTests
{
    private const string ProgramFiles = @"C:\Program Files";

    [Theory]
    [InlineData(@"C:\Program Files\FlyknitBuddy", true, InstallKind.Managed)]
    [InlineData(@"C:\Program Files\FlyknitBuddy", false, InstallKind.Setup)]       // 老安装包、没带代理
    [InlineData(@"c:\program files\FlyknitBuddy\", false, InstallKind.Setup)]      // 大小写、结尾斜杠
    [InlineData(@"D:\Tools\FlyknitBuddy", false, InstallKind.Portable)]
    [InlineData(@"C:\Users\nguyen\Desktop\FlyknitBuddy", false, InstallKind.Portable)]
    [InlineData(@"C:\Program Files (x86)\FlyknitBuddy", false, InstallKind.Portable)] // 只是前缀相同，不算
    [InlineData(@"C:\Program FilesX\FlyknitBuddy", false, InstallKind.Portable)]
    [InlineData(@"C:\Program Files", false, InstallKind.Portable)]                  // 就是根目录本身
    public void DetectsHowTheClientWasInstalled(string dir, bool marker, string expected)
    {
        Assert.Equal(expected, InstallKind.Detect(dir, ProgramFiles, marker));
    }

    [Fact]
    public void EmptyPathsAreTreatedAsPortable()
    {
        Assert.Equal(InstallKind.Portable, InstallKind.Detect("", ProgramFiles, false));
        Assert.Equal(InstallKind.Portable, InstallKind.Detect(@"C:\Program Files\FlyknitBuddy", "", false));
    }

    [Fact]
    public void HeartbeatCarriesInstallKindAndWebViewVersion()
    {
        var info = new MachineInfo { InstallKind = InstallKind.Portable, WebViewVersion = "109.0.1518.140" };
        var json = JsonDocument.Parse(JsonSerializer.Serialize(info)).RootElement;
        Assert.Equal("portable", json.GetProperty("install_kind").GetString());
        Assert.Equal("109.0.1518.140", json.GetProperty("webview_version").GetString());
    }
}
