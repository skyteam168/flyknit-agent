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
        LoadingTitle.Text = NativeStrings.T("loading.title");
        LoadingSubtitle.Text = NativeStrings.T("loading.subtitle");
        _host = host;
        _settings = settings;
        ApplyBackground(settings.WindowBackground);
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
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            // WindowStyle=None 会去掉标题栏样式，Windows 就不给这种窗口播放最小化/还原动画（缩进任务栏、从图标里放出来）。
            // 把标题栏和最小化/最大化按钮的样式补回去：WindowChrome 已经把整个窗口当作客户区，补回来也不会画出系统标题栏。
            var style = GetWindowLong(handle, GwlStyle);
            SetWindowLong(handle, GwlStyle, style | WsCaption | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
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
            // 关掉 Chromium 的“窗口被遮挡/最小化就丢弃画面”：不然最小化、隐藏再恢复时，
            // 页面要重新绘制，中间那一下露出的是空白底色（闪白）。界面静止时不重绘，开销可以忽略。
            var options = new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion");
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData, options);
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
        var cloaked = false;
        if (!IsVisible)
        {
            // 先隐身再显示，等 WebView2 把画面贴上来再现身，避免先露出一帧底色
            cloaked = Cloak(true);
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
        if (cloaked)
        {
            _ = UncloakSoonAsync();
        }
    }

    private async Task UncloakSoonAsync()
    {
        try
        {
            await Task.Delay(60);
        }
        finally
        {
            Cloak(false);
        }
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

    /// <summary>
    /// 收起到悬浮球。直接 Hide 时，WebView2 会先停止绘制、窗口后消失，中间有一帧露出底色（闪白）；
    /// 先用 DWM 把窗口从屏幕上撤下（cloak，不重绘、不动画），再隐藏，就看不到中间状态了。
    /// </summary>
    public void HideMain()
    {
        if (!IsVisible)
        {
            return;
        }
        var cloaked = Cloak(true);
        Hide();
        if (cloaked)
        {
            Cloak(false); // 窗口已经隐藏，取消隐身不会显示任何东西；下次 Show 时按正常流程来
        }
    }

    /// <summary>
    /// 最小化：走系统的最小化命令，和普通软件一样播放缩进任务栏的动画。
    /// 底色跟主题一致、WebView2 隐藏时保留画面（见 InitWebAsync），动画过程中不会闪白。
    /// </summary>
    public void MinimizeMain()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            WindowState = WindowState.Minimized;
            return;
        }
        SendMessage(handle, WmSysCommand, (IntPtr)ScMinimize, IntPtr.Zero);
    }

    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public void SetBackground(string color)
    {
        if (TryParseColor(color, out _))
        {
            _settings.WindowBackground = color;
            ApplyBackground(color);
        }
    }

    private void ApplyBackground(string color)
    {
        if (!TryParseColor(color, out var c))
        {
            return;
        }
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        Web.DefaultBackgroundColor = c;
    }

    private static bool TryParseColor(string? color, out System.Drawing.Color result)
    {
        result = default;
        var s = color?.Trim() ?? "";
        if (s.Length == 4 && s[0] == '#')
        {
            s = $"#{s[1]}{s[1]}{s[2]}{s[2]}{s[3]}{s[3]}";
        }
        if (s.Length != 7 || s[0] != '#' || !int.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return false;
        }
        result = System.Drawing.Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    private const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>DWM 隐身：窗口还在，但不参与屏幕合成。失败（老系统、句柄还没创建）就返回 false，按原来的方式处理。</summary>
    private bool Cloak(bool on)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        var value = on ? 1 : 0;
        try
        {
            return DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

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

    public string? PickSaveFile(string defaultName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = defaultName, Filter = filter, AddExtension = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
