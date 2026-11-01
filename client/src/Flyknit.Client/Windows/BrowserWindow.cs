using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
/// </summary>
public sealed class BrowserWindow : Window, IBrowserDriver
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(600);

    private readonly WebView2 _web = new();
    private readonly TextBlock _action = new() { FontSize = 12.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _url = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xF5)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
    private TaskCompletionSource<bool>? _navigation;
    private bool _quitting;

    /// <summary>读到新页面（地址、标题、截图）或开始做一步时：界面右侧「浏览器」标签跟着变。</summary>
    public event Action<BrowserPage?, string, string?>? StateChanged;

    public BrowserPage? LastPage { get; private set; }

    public BrowserWindow()
    {
        Title = NativeStrings.T("browser.title");
        Width = 1100;
        Height = 760;
        ShowActivated = false; // 弹出时不抢员工手上的焦点
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var bar = new DockPanel { LastChildFill = true };
        bar.Children.Add(_action);
        bar.Children.Add(_url);
        var top = new Border { Background = new SolidColorBrush(Color.FromRgb(0x34, 0x46, 0xC9)), Padding = new Thickness(12, 7, 12, 7), Child = bar };
        DockPanel.SetDock(top, Dock.Top);
        var root = new DockPanel();
        root.Children.Add(top);
        root.Children.Add(_web);
        Content = root;
        SetAction(NativeStrings.T("browser.idle"));
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

    public Task<BrowserPage> OpenAsync(string url, CancellationToken ct) => OnUi(async () =>
    {
        await EnsureAsync();
        SetAction(NativeStrings.T("browser.opening") + " " + url);
        var nav = BeginNavigation();
        _web.CoreWebView2.Navigate(url);
        await WaitAsync(nav, NavigationTimeout, ct);
        await Task.Delay(SettleDelay, ct);
        return await SnapshotAsync(NativeStrings.T("browser.opened"));
    });

    public Task<BrowserPage> ReadAsync(CancellationToken ct) => OnUi(async () =>
    {
        await EnsureAsync();
        return await SnapshotAsync(NativeStrings.T("browser.reading"));
    });

    public Task<(string Result, BrowserPage Page)> RunAsync(string script, string action, CancellationToken ct) => OnUi(async () =>
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
        core.NavigationStarting += (_, _) => _navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, e) =>
        {
            _navigation?.TrySetResult(e.IsSuccess);
            _url.Text = core.Source;
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
        _url.Text = page.Url;
        SetAction(NativeStrings.T("browser.idle"));
        StateChanged?.Invoke(page, action, await ShotAsync());
        return page;
    }

    /// <summary>当前画面的截图（JPEG data URL），给界面右侧「浏览器」标签看。截不到返回 null。</summary>
    private async Task<string?> ShotAsync()
    {
        try
        {
            using var ms = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
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
}
