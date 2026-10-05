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
        _host = host;
        _settings = settings;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        PlaceNearTray();
        Loaded += async (_, _) => await InitWebAsync();
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
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ErrorText.Text = NativeStrings.T("error.webview");
            ErrorText.Visibility = Visibility.Visible;
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

        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(VirtualHost, root, CoreWebView2HostResourceAccessKind.Deny);
        Web.Source = new Uri($"https://{VirtualHost}/index.html");
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

    public void ShowAndFocus()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Web.Focus();
        Bridge?.Post(new { type = "app.focusInput" });
    }

    public void Toggle()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive)
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

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool invert);
}
