using System;

namespace Flyknit.Setup;

/// <summary>
/// 这台电脑能不能跑 FlyknitBuddy：Windows 版本、WebView2 运行时版本。
///
/// 只有纯判断逻辑（不碰注册表、不碰 WebView2），安装程序、员工端、运维代理三处共用同一份源文件，
/// 单元测试也直接拿这个文件来测。
/// </summary>
public static class RuntimeCheck
{
    /// <summary>.NET 8 和员工端（SupportedOSPlatformVersion）要求的最低系统：Windows 10 1809（内部版本 17763）。</summary>
    public const int MinWindowsBuild = 17763;

    /// <summary>
    /// 员工端用到的最新的 WebView2 接口（页面拖动区域 IsNonClientRegionSupportEnabled，ICoreWebView2Settings9）
    /// 正式发布时对应的运行时版本。低于它也能打开（员工端会降级，拖动窗口改走宿主），但安装 / 升级时会顺带更新。
    /// </summary>
    public const string RecommendedWebView = "120.0.2210.55";

    /// <summary>发布包里可选的 WebView2 离线安装包（微软 Evergreen Standalone x64），放在 redist 目录下。</summary>
    public const string RedistDir = "redist";
    public const string OfflineInstaller = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe";

    /// <summary>WebView2 运行时在 EdgeUpdate 里的产品 ID（微软文档给的固定值）。</summary>
    public const string RuntimeClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public static bool IsWindowsSupported(Version os) =>
        os.Major > 10 || (os.Major == 10 && os.Build >= MinWindowsBuild);

    /// <summary>
    /// 比较两个 WebView2 版本号（如 "120.0.2210.55"）。认不出的版本号当作 0.0，
    /// 末尾可能带渠道名（"120.0.2210.55 beta"），只取前面的数字。
    /// </summary>
    public static int CompareVersions(string? a, string? b) => Parse(a).CompareTo(Parse(b));

    public static WebViewState Classify(string? version)
    {
        // 卸载后注册表里会留下 pv = 0.0.0.0
        if (Parse(version).Major == 0)
        {
            return WebViewState.Missing;
        }
        return CompareVersions(version, RecommendedWebView) < 0 ? WebViewState.Old : WebViewState.Ok;
    }

    /// <summary>安装程序 / 运维代理要不要运行离线安装包：没装或太旧，而且包里确实带了。</summary>
    public static bool ShouldInstallRuntime(string? installedVersion, bool offlineInstallerPresent) =>
        offlineInstallerPresent && Classify(installedVersion) != WebViewState.Ok;

    private static readonly Version Zero = new(0, 0);

    private static Version Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Zero;
        }
        var head = text.Trim().Split(' ', '-', '+')[0];
        return Version.TryParse(head, out var v) ? v : Zero;
    }
}

public enum WebViewState
{
    /// <summary>没装（或读不到版本）。</summary>
    Missing,

    /// <summary>装了，但低于 RecommendedWebView。</summary>
    Old,

    Ok,
}
