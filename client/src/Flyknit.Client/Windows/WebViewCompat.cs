using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Flyknit.Client.Services;
using Flyknit.Setup;
using Microsoft.Web.WebView2.Core;

namespace Flyknit.Client.Windows;

/// <summary>
/// 兼容电脑上较旧的 WebView2 运行时。
///
/// 页面拖动区域（app-region: drag）要 ICoreWebView2Settings9，运行时太旧时一设置就抛异常，
/// 以前整个界面因此起不来（现场报「登录界面加载失败 … ICoreWebView2Settings9」）。
/// 现在设置失败就降级：告诉页面「拖动区域不可用」，页面在拖动区域按下鼠标时改由宿主拖动窗口。
/// </summary>
public static class WebViewCompat
{
    /// <summary>页面里看这个全局变量决定要不要自己处理拖动（见 client/web/src/appRegion.ts）。</summary>
    private const string FallbackFlagScript = "window.__flyknitDragFallback = true;";

    /// <summary>打开页面拖动区域；运行时不支持时让页面走兜底。必须在导航之前调用。</summary>
    public static async Task EnableDragRegionsAsync(CoreWebView2 core)
    {
        try
        {
            core.Settings.IsNonClientRegionSupportEnabled = true;
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"WebView2 运行时较旧（{SafeVersion()}），页面拖动区域不可用，改由宿主拖动窗口：{ex.GetType().Name}");
        }
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(FallbackFlagScript);
        }
        catch (Exception ex)
        {
            Log.Warn("注入拖动兜底标记失败（窗口只能用系统菜单移动）", ex);
        }
    }

    /// <summary>
    /// 设置一项可选的浏览器选项。老运行时上没有的选项跳过，不影响打开界面
    /// （这些都是关掉自动填充、快捷键之类的「锦上添花」）。
    /// </summary>
    public static void TrySet(Action set, string name)
    {
        try
        {
            set();
        }
        catch (Exception ex)
        {
            Log.Warn($"WebView2 运行时不支持 {name}，已跳过：{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 界面起不来时给员工看的话：运行时没装 / 太旧时直接说清楚要做什么，其他情况带上异常信息方便 IT 排查。
    /// </summary>
    public static string FailureMessage(Exception ex, string prefix)
    {
        if (ex is WebView2RuntimeNotFoundException)
        {
            return NativeStrings.T("error.webview");
        }
        var version = SafeVersion();
        return RuntimeCheck.Classify(version) switch
        {
            WebViewState.Missing => NativeStrings.T("error.webview"),
            WebViewState.Old => string.Format(NativeStrings.T("error.webviewOld"), version, RuntimeCheck.RecommendedWebView)
                + $"\n\n({ex.GetType().Name}: {ex.Message})",
            _ => $"{prefix}{ex.GetType().Name}: {ex.Message}",
        };
    }

    public static string SafeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// 兜底拖动：和按住系统标题栏一样，交给系统的移动窗口流程（到屏幕顶部吸附最大化、Win11 贴靠都照常）。
    /// </summary>
    public static void DragWindow(IntPtr handle)
    {
        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
