using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Flyknit.Client.Services;
using Flyknit.Core.Browser;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Flyknit.Client.Windows;

/// <summary>
/// AI 浏览器：一个看得见的浏览器窗口，AI 在里面打开网页、读内容、点击、填表，员工随时能看、也能自己点
/// （比如自己输入密码登录）。和主界面共用一个 WebView2 环境，所以登录过的内部系统下次还是登录状态。
///
/// 所有 WebView2 调用都切到界面线程；工具在后台线程上调用这里的方法。关窗口只是隐藏，任务下次还能用。
///
/// 工具栏：后退 / 前进 / 刷新 / 可输入的地址栏（只开 http / https）。
/// AI 操作时（参照 Cursor 的 Take Control）：网页上盖一层遮罩挡住员工的点击和按键，免得打乱 AI；
/// 员工点「接管」后遮罩去掉，AI 下一步浏览器操作等员工点「交还给 AI」再继续（<see cref="BrowserControl"/>）。
/// 遮罩画在网页里（BrowserScripts.Guard）：WebView2 是原生子窗口，WPF 的控件盖不到它上面。
/// </summary>
public sealed class BrowserWindow : Window, IBrowserDriver
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(600);

    private readonly WebView2 _web = new();
    private readonly TextBlock _action = new() { FontSize = 12.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _address = new()
    {
        FontSize = 13,
        Padding = new Thickness(8, 4, 8, 4),
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xD9, 0xE3)),
        Margin = new Thickness(6, 0, 6, 0),
    };
    private readonly Button _back = NavButton("\uE72B", "browser.back");
    private readonly Button _forward = NavButton("\uE72A", "browser.forward");
    private readonly Button _reload = NavButton("\uE72C", "browser.reload");
    private readonly Button _control = new()
    {
        Padding = new Thickness(12, 4, 12, 4),
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromRgb(0x34, 0x46, 0xC9)),
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand,
        Visibility = Visibility.Collapsed,
    };
    private readonly Border _status = new() { Padding = new Thickness(12, 6, 12, 6) };
    private TaskCompletionSource<bool>? _navigation;
    private bool _quitting;

    /// <summary>读到新页面（地址、标题、截图）或开始做一步时：界面右侧「浏览器」标签跟着变。</summary>
    public event Action<BrowserPage?, string, string?>? StateChanged;

    public BrowserPage? LastPage { get; private set; }

    /// <summary>谁在操作这个浏览器（AI / 员工接管 / 没人）。宿主在一轮任务结束时调 Release。</summary>
    public BrowserControl Control { get; } = new();

    public BrowserWindow()
    {
        Title = NativeStrings.T("browser.title");
        Width = 1100;
        Height = 760;
        ShowActivated = false; // 弹出时不抢员工手上的焦点
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _address.ToolTip = NativeStrings.T("browser.address");
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = NavigateByUserAsync(_address.Text);
            }
            else if (e.Key == Key.Escape)
            {
                _address.Text = _web.CoreWebView2?.Source ?? "";
            }
        };
        _address.GotKeyboardFocus += (_, _) => _address.SelectAll();
        _back.Click += (_, _) => { if (_web.CoreWebView2?.CanGoBack == true) _web.CoreWebView2.GoBack(); };
        _forward.Click += (_, _) => { if (_web.CoreWebView2?.CanGoForward == true) _web.CoreWebView2.GoForward(); };
        _reload.Click += (_, _) => _web.CoreWebView2?.Reload();
        _control.Click += (_, _) =>
        {
            if (Control.State == BrowserController.User)
            {
                Control.HandBack();
            }
            else
            {
                Control.TakeOver();
            }
        };
        Control.Changed += _ => Dispatcher.InvokeAsync(ApplyControl);

        // 工具栏：← → ⟳ [地址栏] [接管 / 交还给 AI]
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 6, 8, 6) };
        foreach (var b in new[] { _back, _forward, _reload })
        {
            DockPanel.SetDock(b, Dock.Left);
            toolbar.Children.Add(b);
        }
        DockPanel.SetDock(_control, Dock.Right);
        toolbar.Children.Add(_control);
        toolbar.Children.Add(_address);
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xEC)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbar,
        };
        _status.Child = _action;
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        var root = new DockPanel();
        root.Children.Add(top);
        root.Children.Add(_web);
        Content = root;
        UpdateNavButtons();
        ApplyControl();
    }

    /// <summary>程序退出时真正关掉；平时点关闭只是隐藏。</summary>
    public void Quit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 程序退出（Dispatcher 正在关）时照常关掉，平时只隐藏
        if (!_quitting && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ---------- IBrowserDriver ----------

    public Task<BrowserPage> OpenAsync(string url, CancellationToken ct) => AsAi(ct, async () =>
    {
        await EnsureAsync();
        SetAction(NativeStrings.T("browser.opening") + " " + url);
        var nav = BeginNavigation();
        _web.CoreWebView2.Navigate(url);
        await WaitAsync(nav, NavigationTimeout, ct);
        await Task.Delay(SettleDelay, ct);
        return await SnapshotAsync(NativeStrings.T("browser.opened"));
    });

    public Task<BrowserPage> ReadAsync(CancellationToken ct) => AsAi(ct, async () =>
    {
        await EnsureAsync();
        return await SnapshotAsync(NativeStrings.T("browser.reading"));
    });

    public Task<(string Result, BrowserPage Page)> RunAsync(string script, string action, CancellationToken ct) => AsAi(ct, async () =>
    {
        await EnsureAsync();
        SetAction(action);
        StateChanged?.Invoke(LastPage, action, null);
        _navigation = null;
        var raw = await _web.CoreWebView2.ExecuteScriptAsync(script);
        var result = Decode(raw);
        // 点了链接、提交了表单会跳页：等新页面加载完；没跳就稍等一下让页面上的脚本跑完
        await Task.Delay(250, ct);
        if (_navigation is { } nav)
        {
            await WaitAsync(nav, NavigationTimeout, ct);
        }
        await Task.Delay(SettleDelay, ct);
        return (result, await SnapshotAsync(action));
    });

    // ---------- 内部 ----------

    private async Task EnsureAsync()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (_web.CoreWebView2 is not null)
        {
            return;
        }
        await _web.EnsureCoreWebView2Async(await SharedWebView.EnvironmentAsync());
        var core = _web.CoreWebView2!;
        // 每个新页面都带上遮罩脚本；开不开由 ApplyGuard 决定
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserScripts.Guard);
        core.NavigationStarting += (_, _) => _navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, e) =>
        {
            _navigation?.TrySetResult(e.IsSuccess);
            ShowAddress(core.Source);
            UpdateNavButtons();
            _ = ApplyGuardAsync();
        };
        core.SourceChanged += (_, _) => ShowAddress(core.Source);
        core.DOMContentLoaded += (_, _) => _ = ApplyGuardAsync();
        core.HistoryChanged += (_, _) => UpdateNavButtons();
        core.WebMessageReceived += (_, e) =>
        {
            if (e.TryGetWebMessageAsString() == BrowserScripts.TakeControlMessage)
            {
                Control.TakeOver();
            }
        };
        // 新窗口（target=_blank）在同一个窗口里打开，AI 才跟得上
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        // 不让网页弹下载框、通知权限之类打断
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
    }

    private TaskCompletionSource<bool> BeginNavigation()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigation = tcs;
        return tcs;
    }

    private static async Task WaitAsync(TaskCompletionSource<bool> tcs, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            // 慢页面：不干等，按已经加载出来的部分读
        }
    }

    private async Task<BrowserPage> SnapshotAsync(string action)
    {
        var raw = await _web.CoreWebView2.ExecuteScriptAsync(BrowserScripts.Snapshot);
        var json = Decode(raw);
        var page = json.StartsWith('{') ? BrowserScripts.ParsePage(json) : new BrowserPage { Url = _web.CoreWebView2.Source };
        LastPage = page;
        ShowAddress(page.Url);
        ApplyControl();
        StateChanged?.Invoke(page, action, await ShotAsync());
        return page;
    }

    /// <summary>当前画面的截图（JPEG data URL），给界面右侧「浏览器」标签看。截不到返回 null。</summary>
    private async Task<string?> ShotAsync()
    {
        try
        {
            using var ms = new MemoryStream();
            // 截图里不要遮罩（界面右侧看的是网页本身）
            var guarded = Control.State == BrowserController.Ai;
            if (guarded)
            {
                await _web.CoreWebView2.ExecuteScriptAsync(BrowserScripts.SetGuard(false, "", ""));
            }
            try
            {
                await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
            }
            finally
            {
                if (guarded)
                {
                    await ApplyGuardAsync();
                }
            }
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.Warn($"浏览器截图失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>ExecuteScriptAsync 返回的是 JSON 编码的结果（字符串带引号），解开成普通字符串。</summary>
    private static string Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            return "";
        }
        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? "";
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private void SetAction(string text) => _action.Text = text;

    private Task<T> OnUi<T>(Func<Task<T>> work) => Dispatcher.InvokeAsync(work).Task.Unwrap();

    /// <summary>AI 的每一步：员工接管着就先等交还（右侧标签显示「等你交还」），再到界面线程上做。</summary>
    private async Task<T> AsAi<T>(CancellationToken ct, Func<Task<T>> work)
    {
        await Control.AcquireAsync(ct, () => StateChanged?.Invoke(LastPage, NativeStrings.T("browser.waitingUser"), null));
        return await OnUi(work);
    }

    // ---------- 工具栏 ----------

    private static Button NavButton(string glyph, string tip) => new()
    {
        Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 },
        ToolTip = NativeStrings.T(tip),
        Width = 30,
        Height = 28,
        Margin = new Thickness(0, 0, 2, 0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand,
    };

    /// <summary>员工在地址栏输入后回车。只开 http / https；AI 操作时地址栏是灰的（先点「接管」）。</summary>
    private async Task NavigateByUserAsync(string text)
    {
        if (Control.State == BrowserController.Ai)
        {
            return;
        }
        if (BrowserAddress.Normalize(text) is not { } url)
        {
            SetAction(NativeStrings.T("browser.badUrl"));
            return;
        }
        try
        {
            await EnsureAsync();
            _web.CoreWebView2.Navigate(url);
            _web.Focus();
        }
        catch (Exception ex)
        {
            Log.Warn($"浏览器打开网址失败：{ex.Message}");
            SetAction(NativeStrings.T("browser.badUrl"));
        }
    }

    /// <summary>员工正在地址栏里打字时不覆盖。</summary>
    private void ShowAddress(string? url)
    {
        if (!_address.IsKeyboardFocusWithin && url is not null)
        {
            _address.Text = url;
        }
    }

    private void UpdateNavButtons()
    {
        var core = _web.CoreWebView2;
        var mine = Control.State != BrowserController.Ai;
        _back.IsEnabled = mine && core?.CanGoBack == true;
        _forward.IsEnabled = mine && core?.CanGoForward == true;
        _reload.IsEnabled = mine && core is not null;
        _address.IsEnabled = mine;
    }

    /// <summary>按控制权更新工具栏、提示条和网页上的遮罩。</summary>
    private void ApplyControl()
    {
        switch (Control.State)
        {
            case BrowserController.Ai:
                _control.Content = NativeStrings.T("browser.takeControl");
                _control.Visibility = Visibility.Visible;
                _status.Background = new SolidColorBrush(Color.FromRgb(0x34, 0x46, 0xC9));
                _action.Foreground = Brushes.White;
                SetAction(NativeStrings.T("browser.aiActive"));
                break;
            case BrowserController.User:
                _control.Content = NativeStrings.T("browser.handBack");
                _control.Visibility = Visibility.Visible;
                _status.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE0));
                _action.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x52, 0x00));
                SetAction(NativeStrings.T("browser.userControl"));
                StateChanged?.Invoke(LastPage, NativeStrings.T("browser.userControl"), null);
                break;
            default:
                _control.Visibility = Visibility.Collapsed;
                _status.Background = new SolidColorBrush(Color.FromRgb(0x34, 0x46, 0xC9));
                _action.Foreground = Brushes.White;
                SetAction(NativeStrings.T("browser.idle"));
                break;
        }
        UpdateNavButtons();
        _ = ApplyGuardAsync();
    }

    private async Task ApplyGuardAsync()
    {
        if (_web.CoreWebView2 is not { } core)
        {
            return;
        }
        try
        {
            await core.ExecuteScriptAsync(BrowserScripts.SetGuard(
                Control.State == BrowserController.Ai, NativeStrings.T("browser.guard"), NativeStrings.T("browser.takeControl")));
        }
        catch (Exception ex)
        {
            // 页面正在跳转等：下一次 DOMContentLoaded / NavigationCompleted 会再设
            Log.Info($"浏览器遮罩没设上：{ex.Message}");
        }
    }
}
