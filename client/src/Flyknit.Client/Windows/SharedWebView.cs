using System.Threading.Tasks;
using Flyknit.Client.Services;
using Microsoft.Web.WebView2.Core;

namespace Flyknit.Client.Windows;

/// <summary>
/// 整个程序共用的一个 WebView2 环境。
///
/// 同一个数据目录（AppPaths.WebViewData）只能对应一套启动参数：登录窗口用默认参数建了环境、
/// 浏览器进程还没退，主窗口再带别的参数建一个，WebView2 就报 0x8007139F
///（“组或资源的状态不是执行请求操作的正确状态”），界面一片空白。所以只建一次，大家都用这一个。
/// </summary>
public static class SharedWebView
{
    private static Task<CoreWebView2Environment>? _environment;

    public static async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        // 关掉 Chromium 的“窗口被遮挡/最小化就丢弃画面”：不然最小化、隐藏再恢复时，
        // 页面要重新绘制，中间那一下露出的是空白底色（闪白）。界面静止时不重绘，开销可以忽略。
        var task = _environment ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData,
            new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion"));
        try
        {
            return await task;
        }
        catch
        {
            // 建失败（比如没装 WebView2 运行时）不缓存，下次还能再试
            if (_environment == task)
            {
                _environment = null;
            }
            throw;
        }
    }
}
