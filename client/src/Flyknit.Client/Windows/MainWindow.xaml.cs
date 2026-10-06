using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Flyknit.Client.Bridge;
using Flyknit.Client.Services;
using Microsoft.Web.WebView2.Core;

namespace Flyknit.Client.Windows;

public partial class MainWindow : Window, IWindowActions
{
    private const string VirtualHost = "app.flyknit.local";

    private readonly AgentHost _host;
    private readonly AppSettings _settings;
    private bool _allowClose;

    public WebBridge? Bridge { get; private set; }

    /// <summary>界面语言变化（托盘菜单等原生界面需要同步）。</summary>
    public event Action<string>? UiLanguageChanged;

    public MainWindow(AgentHost host, AppSettings settings)
    {
        InitializeComponent();
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF4, 0xF6, 0xF9);
        LoadingTitle.Text = NativeStrings.T("loading.title");
        LoadingSubtitle.Text = NativeStrings.T("loading.subtitle");
        _host = host;
        _settings = settings;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        PlaceNearTray();
        Loaded += async (_, _) =>
        {
            try
            {
                await InitWebAsync();
            }
            catch (Exception ex)
            {
                // 任何初始化异常都显示在窗口里，避免只看到空白
                Log.Error("WebView2 初始化失败", ex);
                ShowError($"界面加载失败：{ex.GetType().Name}: {ex.Message}");
            }
        };
        SourceInitialized += (_, _) =>
        {
            // 无边框窗口最大化时按显示器工作区计算大小：不盖住任务栏，也不溢出屏幕边缘
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized && !_prewarming)
            {
                _settings.WindowMaximized = WindowState == WindowState.Maximized;
            }
            Bridge?.Post(new { type = "window.state", maximized = WindowState == WindowState.Maximized });
        };
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = ActualWidth;
                _settings.WindowHeight = ActualHeight;
            }
        };
    }

    private async Task InitWebAsync()
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
            Log.Info($"WebView2 环境就绪，耗时 {watch.ElapsedMilliseconds} ms（运行时 {env.BrowserVersionString}）");
            await Web.EnsureCoreWebView2Async(env);
            Log.Info($"WebView2 控件就绪，累计 {watch.ElapsedMilliseconds} ms");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError(NativeStrings.T("error.webview"));
            Web.Visibility = Visibility.Collapsed;
            return;
        }

        var core = Web.CoreWebView2;
        var settings = core.Settings;
        settings.IsNonClientRegionSupportEnabled = true; // 支持网页中的 app-region: drag
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif

        // 外部链接用系统浏览器打开
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };

        Bridge = new WebBridge(core, Dispatcher, _host, _settings, this);

        core.NavigationCompleted += (_, e) =>
        {
            _pageReady.TrySetResult(e.IsSuccess);
            RevealWeb();
            Log.Info($"页面加载完成：成功={e.IsSuccess} 状态={e.WebErrorStatus} HTTP={e.HttpStatusCode}");
            if (!e.IsSuccess)
            {
                ShowError($"页面加载失败：{e.WebErrorStatus}（HTTP {e.HttpStatusCode}）。请确认已在 client\\web 执行 npm run build。");
            }
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Error($"WebView2 进程异常：{e.ProcessFailedKind} {e.Reason}");
            ShowError($"浏览器组件异常：{e.ProcessFailedKind}");
        };
        core.WebResourceResponseReceived += (_, e) =>
        {
            if (e.Response.StatusCode >= 400)
            {
                Log.Warn($"资源加载失败：{e.Request.Uri} → HTTP {e.Response.StatusCode}");
            }
        };

        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Log.Info($"界面目录：{root}（index.html {(File.Exists(Path.Combine(root, "index.html")) ? "存在" : "不存在")}）");
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            ShowError($"找不到界面文件：{root}\\index.html。请先在 client\\web 执行 npm run build，再重新运行。");
            return;
        }
        core.SetVirtualHostNameToFolderMapping(VirtualHost, root, CoreWebView2HostResourceAccessKind.Deny);
        // WebView2 会缓存 index.html；界面重新构建后资源文件名变了，旧缓存会导致白屏。
        // 用 index.html 的修改时间作为版本参数，每次构建后都会加载最新页面。
        var index = Path.Combine(root, "index.html");
        var version = File.Exists(index) ? File.GetLastWriteTimeUtc(index).Ticks : 0;
        Web.Source = new Uri($"https://{VirtualHost}/index.html?v={version}&lang={_settings.ResolveUiLanguage()}");

        // 兜底：15 秒内没有收到加载完成事件也显示页面，避免一直停在加载卡片
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Dispatcher.BeginInvoke(() => RevealWeb()));
    }

    private readonly System.Threading.Tasks.TaskCompletionSource<bool> _pageReady = new();
    private bool _prewarming;
    private double _savedLeft, _savedTop;

    /// <summary>
    /// 启动时在屏幕外预先加载界面（WebView2 在隐藏窗口里不会渲染），
    /// 加载完成后再隐藏，之后点悬浮球就能立刻显示，不会白屏。
    /// </summary>
    public async void Prewarm()
    {
        _prewarming = true;
        _savedLeft = Left;
        _savedTop = Top;
        ShowActivated = false;
        ShowInTaskbar = false;
        Left = -32000;
        Top = -32000;
        Show();
        var done = await System.Threading.Tasks.Task.WhenAny(_pageReady.Task, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(30)));
        if (done == _pageReady.Task)
        {
            await System.Threading.Tasks.Task.Delay(800); // 等页面完成首次渲染
        }
        if (_prewarming)
        {
            EndPrewarm(visible: false);
        }
    }

    private void EndPrewarm(bool visible)
    {
        _prewarming = false;
        if (!visible)
        {
            Hide();
        }
        Left = _savedLeft;
        Top = _savedTop;
        ShowActivated = true;
        ShowInTaskbar = true;
    }

    /// <summary>页面加载完成后显示 WebView（页面里有同样样式的加载卡片，切换时看不出闪烁）。</summary>
    private void RevealWeb()
    {
        if (ErrorText.Visibility == Visibility.Visible)
        {
            return;
        }
        Web.Visibility = Visibility.Visible;
        LoadingCard.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        LoadingCard.Visibility = Visibility.Collapsed;
        ErrorText.Text = message + "\n\n日志：" + AppPaths.Logs;
        ErrorText.Visibility = Visibility.Visible;
        Web.Visibility = Visibility.Collapsed; // WebView2 总是盖在 WPF 内容之上，必须隐藏才能看到提示
    }

    private static void OpenExternal(string uri)
    {
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
    }

    /// <summary>默认出现在屏幕右下角（悬浮球上方）。</summary>
    private void PlaceNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = Math.Max(area.Top + 16, area.Bottom - Height - 96);
    }

    private bool _shownOnce;

    public void ShowAndFocus()
    {
        if (_prewarming)
        {
            // 预加载还没结束时用户就点了悬浮球：直接把窗口移回屏幕内显示
            EndPrewarm(visible: true);
        }
        if (!IsVisible)
        {
            Show();
        }
        if (!_shownOnce)
        {
            _shownOnce = true;
            if (_settings.WindowMaximized)
            {
                WindowState = WindowState.Maximized; // 恢复上次的最大化状态
            }
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Web.Focus();
        Bridge?.Post(new { type = "app.focusInput" });
    }

    /// <summary>
    /// 悬浮球 / 快捷键：窗口可见就收起，否则打开。
    /// 注意不能判断 IsActive——点击悬浮球时焦点在悬浮球上，主窗口永远不是活动窗口。
    /// </summary>
    public void Toggle()
    {
        if (!_prewarming && IsVisible && WindowState != WindowState.Minimized)
        {
            HideMain();
        }
        else
        {
            ShowAndFocus();
        }
    }

    public void ExitForReal()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 关闭按钮只是收起到悬浮球
        if (!_allowClose)
        {
            e.Cancel = true;
            HideMain();
        }
        _settings.Save();
        base.OnClosing(e);
    }

    // ---------- IWindowActions ----------

    public void HideMain() => Hide();

    public void MinimizeMain() => WindowState = WindowState.Minimized;

    public bool IsMaximized => WindowState == WindowState.Maximized;

    public bool ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        return WindowState == WindowState.Maximized;
    }

    /// <summary>当前是否在前台（窗口可见、未最小化且处于激活状态）。用于决定是否需要系统通知。</summary>
    public bool IsInForeground => IsVisible && WindowState != WindowState.Minimized && IsActive && !_prewarming;

    /// <summary>
    /// 从网页边缘开始拖拽调整窗口大小。
    /// 无边框窗口里 WebView2 盖住了整个客户区，鼠标消息都被子窗口吃掉，WPF 的 ResizeBorderThickness 不起作用；
    /// 所以由网页在四边放透明热区，按下时调这里，走系统自己的调整大小流程（和拖标准窗口边框一样）。
    /// </summary>
    /// <summary>快捷键改了之后让 App 重新注册全局热键。</summary>
    public event Action? HotkeyChanged;

    public void RefreshHotkey() => HotkeyChanged?.Invoke();

    public void StartResize(string direction)
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }
        var code = direction switch
        {
            "left" => HtLeft,
            "right" => HtRight,
            "top" => HtTop,
            "topLeft" => HtTopLeft,
            "topRight" => HtTopRight,
            "bottom" => HtBottom,
            "bottomLeft" => HtBottomLeft,
            "bottomRight" => HtBottomRight,
            _ => 0,
        };
        if (code == 0)
        {
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, (IntPtr)code, IntPtr.Zero);
    }

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---------- 最大化尺寸 ----------

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
                mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
                mmi.MaxSize.X = info.Work.Right - info.Work.Left;
                mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            // 不设置 handled，让 WPF 继续处理最小尺寸等限制
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public bool ToggleTopmost()
    {
        Topmost = !Topmost;
        return Topmost;
    }

    public void RequestAttention()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowAndFocus();
        }
        else if (!IsActive)
        {
            FlashWindow(new WindowInteropHelper(this).Handle, true);
        }
    }

    public void LanguageChanged(string language) => UiLanguageChanged?.Invoke(language);

    public IReadOnlyList<string> PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
