using Flyknit.Setup;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>安装程序 / 员工端 / 运维代理共用的系统版本、WebView2 版本判断。</summary>
public class RuntimeCheckTests
{
    [Theory]
    [InlineData(6, 1, 7601, false)]   // Windows 7 SP1
    [InlineData(6, 3, 9600, false)]   // Windows 8.1
    [InlineData(10, 0, 14393, false)] // Windows 10 1607（LTSB 2016）
    [InlineData(10, 0, 17134, false)] // Windows 10 1803
    [InlineData(10, 0, 17763, true)]  // Windows 10 1809（LTSC 2019）
    [InlineData(10, 0, 19045, true)]  // Windows 10 22H2
    [InlineData(10, 0, 26100, true)]  // Windows 11 24H2
    public void WindowsVersionFloorIs1809(int major, int minor, int build, bool supported)
    {
        Assert.Equal(supported, RuntimeCheck.IsWindowsSupported(new Version(major, minor, build)));
    }

    [Theory]
    [InlineData(null, WebViewState.Missing)]
    [InlineData("", WebViewState.Missing)]
    [InlineData("0.0.0.0", WebViewState.Missing)]          // 卸载后注册表留下的值
    [InlineData("不是版本号", WebViewState.Missing)]
    [InlineData("86.0.616.0", WebViewState.Old)]
    [InlineData("109.0.1518.140", WebViewState.Old)]       // Win7 上最后一个版本
    [InlineData("119.0.2151.97", WebViewState.Old)]
    [InlineData("120.0.2210.55", WebViewState.Ok)]
    [InlineData("120.0.2210.61", WebViewState.Ok)]
    [InlineData("131.0.2903.112", WebViewState.Ok)]
    [InlineData("131.0.2903.112 beta", WebViewState.Ok)]   // 带渠道名
    public void ClassifiesRuntimeVersion(string? version, WebViewState expected)
    {
        Assert.Equal(expected, RuntimeCheck.Classify(version));
    }

    [Fact]
    public void ComparesNumericallyNotAsText()
    {
        // 按字符串比 "99" > "120"，按数字比才对
        Assert.True(RuntimeCheck.CompareVersions("99.0.1150.30", "120.0.2210.55") < 0);
        Assert.True(RuntimeCheck.CompareVersions("120.0.2210.100", "120.0.2210.55") > 0);
        Assert.Equal(0, RuntimeCheck.CompareVersions("120.0.2210.55", "120.0.2210.55"));
    }

    [Theory]
    [InlineData("", true, true)]                 // 没装、包里有 → 装
    [InlineData("109.0.1518.140", true, true)]   // 太旧、包里有 → 装
    [InlineData("131.0.2903.112", true, false)]  // 已经够新 → 不重复装
    [InlineData("", false, false)]               // 包里没带 → 什么都不做（只提醒）
    [InlineData("109.0.1518.140", false, false)]
    public void InstallsRuntimeOnlyWhenNeededAndBundled(string installed, bool bundled, bool expected)
    {
        Assert.Equal(expected, RuntimeCheck.ShouldInstallRuntime(installed, bundled));
    }
}
