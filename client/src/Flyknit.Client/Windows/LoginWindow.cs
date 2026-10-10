using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Flyknit.Client.Services;
using Flyknit.Core.Gateway;
using Flyknit.Core.Setup;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Flyknit.Client.Windows;

/// <summary>
/// 首次启动的登录窗口。界面是 wwwroot/login.html（和主界面同一套网页技术，好看、也好改），这里只负责：
/// - 告诉页面当前 Windows 账号、安装包里有没有服务器地址和安装凭证；
/// - 从服务器拿用户协议和隐私政策；
/// - 校验身份并注册本机：域账号点「授权登录」直接注册（Windows 开机时已验证过）；
///   账号密码方式交给本机 Windows（LogonUser）校验，密码不存不发；
/// - 没有开通文件或员工选了「连接其他服务器」：服务器地址 + 注册密钥（老办法）。
/// 页面发 { id, type, ... }，这里回 { id, ok, data | error, code }。
/// </summary>
public sealed class LoginWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Provision? _provision;
    private readonly WindowsAccount _account = WindowsAccount.Current();
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF5, 0xF8, 0xFF) };

    public LoginWindow(AppSettings settings)
    {
        _settings = settings;
        // 程序目录里的（刚解压的安装包）优先；自动升级换掉程序目录后，用登录时留在数据目录的那份
        _provision = Provisioning.Load(AppContext.BaseDirectory, AppPaths.Root);

        Title = "FlyknitBuddy";
        Width = 440;
        Height = 660;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF8, 0xFF));
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/flyknitbuddy.png"));
        // 去掉系统标题栏但保留窗口阴影和 Win11 圆角：标题栏由页面自己画（可拖动、最小化、关闭）
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        Content = _web;
        // 关窗时把浏览器控件也释放掉，别让它的页面在后台一直挂着
        Closed += (_, _) => _web.Dispose();
        Loaded += async (_, _) =>
        {
            try
            {
                await InitAsync();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(this, NativeStrings.T("error.webview"), "FlyknitBuddy", MessageBoxButton.OK, MessageBoxImage.Error);
                DialogResult = false;
            }
            catch (Exception ex)
            {
                Log.Error("登录窗口初始化失败", ex);
                MessageBox.Show(this, $"登录界面加载失败：{ex.Message}", "FlyknitBuddy", MessageBoxButton.OK, MessageBoxImage.Error);
                DialogResult = false;
            }
        };
    }

    private async Task InitAsync()
    {
        // 和主窗口共用同一个环境：各建各的、参数不一样，主窗口会报 0x8007139F 打不开
        var env = await SharedWebView.EnvironmentAsync();
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.IsNonClientRegionSupportEnabled = true; // 页面里 app-region: drag 的地方能拖动窗口
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{MainWindow.VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        };
        core.WebMessageReceived += OnMessage;

        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(MainWindow.VirtualHost, root, CoreWebView2HostResourceAccessKind.Deny);
        var page = Path.Combine(root, "login.html");
        var version = File.Exists(page) ? File.GetLastWriteTimeUtc(page).Ticks : 0;
        _web.Source = new Uri($"https://{MainWindow.VirtualHost}/login.html?v={version}&lang={_settings.ResolveUiLanguage()}");
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var msg = doc.RootElement;
            id = msg.GetProperty("id").GetString();
            string Str(string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            object? data = Str("type") switch
            {
                "init" => new
                {
                    account = new { display = _account.Display, user = _account.User, domain = _account.Domain, kind = KindName(_account) },
                    provisioned = _provision is not null,
                    server = _provision?.ServerUrl ?? "",
                    lang = _settings.ResolveUiLanguage(),
                    version = typeof(LoginWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                },
                "legal" => await LegalAsync(),
                "login" => await LoginAsync(Str("mode"), Str("username"), Str("password"), Str("agreed")),
                "manual" => await ManualAsync(Str("server"), Str("key")),
                "done" => Done(),
                "window" => WindowAction(Str("action")),
                _ => null,
            };
            Reply(id, true, data, null, null);
        }
        catch (LoginException ex)
        {
            Reply(id, false, null, ex.Message, ex.Code);
        }
        catch (Exception ex)
        {
            Log.Warn("登录失败", ex);
            var code = ex is GatewayException { StatusCode: 409 } ? "legal_outdated" : ex is HttpRequestException or OperationCanceledException or GatewayException ? "server" : "other";
            var message = ex is OperationCanceledException ? NativeStrings.T("login.timeout") : ex.Message;
            Reply(id, false, null, message, code);
        }
    }

    private void Reply(string? id, bool ok, object? data, string? error, string? code)
    {
        if (id is null)
        {
            return;
        }
        var json = JsonSerializer.Serialize(new { id, ok, data, error, code });
        _web.CoreWebView2?.PostWebMessageAsJson(json);
    }

    private static string KindName(WindowsAccount account) => account.Kind == LoginKind.Domain ? "domain" : "local";

    private async Task<object?> LegalAsync()
    {
        var provision = _provision ?? throw new LoginException(NativeStrings.T("login.noProvision"), "other");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var json = await Client(provision.ServerUrl).GetLegalAsync(cts.Token);
        return JsonNode.Parse(json);
    }

    private async Task<object?> LoginAsync(string mode, string username, string password, string agreed)
    {
        var provision = _provision ?? throw new LoginException(NativeStrings.T("login.noProvision"), "other");
        var account = _account;
        if (mode == "password")
        {
            // 账号密码：只问本机 Windows 这个密码对不对，密码不存、不发出去
            var input = AccountInput.Parse(username, Environment.MachineName) ?? throw new LoginException(NativeStrings.T("login.noAccount"), "other");
            var (result, message) = await Task.Run(() => WindowsLogon.Verify(input.LogonUser, input.LogonDomain, password));
            if (result == WindowsLogon.Result.WrongPassword)
            {
                throw new LoginException(NativeStrings.T("login.wrongPassword"), "wrong_password");
            }
            if (result != WindowsLogon.Result.Ok)
            {
                throw new LoginException(string.Format(NativeStrings.T("login.verifyFailed"), message), "other");
            }
            account = input.Account;
        }
        else if (account.Kind != LoginKind.Domain)
        {
            // 本机账号：Windows 没法替他担保，要输一次密码
            throw new LoginException("need password", "need_password");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reg = await Client(provision.ServerUrl).RegisterWithTicketAsync(provision.Ticket, account, agreed, _settings.ResolveUiLanguage(), Version(), cts.Token);
        Log.Info($"已登录：{account.Display}（{account.Kind}），服务器 {provision.ServerUrl}");
        try
        {
            Provisioning.Keep(AppContext.BaseDirectory, AppPaths.Root);
        }
        catch (Exception ex)
        {
            Log.Warn("保留开通文件失败（不影响这次登录）", ex);
        }
        Save(provision.ServerUrl, reg.Token);
        return null;
    }

    private async Task<object?> ManualAsync(string server, string key)
    {
        var url = server.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new LoginException(NativeStrings.T("login.badServer"), "other");
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reg = await Client(url).RegisterAsync(key.Trim(), _settings.ResolveUiLanguage(), Version(), cts.Token);
        Save(url, reg.Token);
        return null;
    }

    /// <summary>注册成功先存好；窗口等页面放完“登录成功”的动画（done）再关。</summary>
    private void Save(string url, string token)
    {
        _settings.ServerUrl = url;
        _settings.DeviceToken = token;
        _settings.Save();
    }

    private object? Done()
    {
        if (_settings.IsRegistered)
        {
            DialogResult = true;
        }
        return null;
    }

    private object? WindowAction(string action)
    {
        if (action == "minimize")
        {
            WindowState = WindowState.Minimized;
        }
        else if (action == "close")
        {
            DialogResult = _settings.IsRegistered;
        }
        return null;
    }

    private FlyknitServerClient Client(string url) => new(ProxyFactory.CreateHttpClient(_settings), url, null);

    private static string Version() => typeof(LoginWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private sealed class LoginException(string message, string code) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
