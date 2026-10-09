using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Flyknit.Client.Services;
using Flyknit.Core.Gateway;
using Flyknit.Core.Setup;

namespace Flyknit.Client.Windows;

/// <summary>
/// 首次启动：注册本机设备。
/// - 安装包里带了开通文件（IT 从后台下载的员工端安装包）：显示「登录」。域账号点一下就行（Windows 开机时已经验证过），
///   本机账号输入这台电脑的 Windows 密码、由本机校验；
/// - 没有开通文件，或者员工点了「手动连接服务器」：填服务器地址和注册密钥（老办法）。
/// </summary>
public partial class SetupWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Provision? _provision;
    private readonly WindowsAccount _account = WindowsAccount.Current();
    private bool _manual;

    public SetupWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        // 程序目录里的（刚解压的安装包）优先；自动升级换掉程序目录后，用登录时留在数据目录的那份
        _provision = Provisioning.Load(AppContext.BaseDirectory, AppPaths.Root);

        IntroText.Text = NativeStrings.T("setup.intro");
        ServerLabel.Text = NativeStrings.T("setup.server");
        KeyLabel.Text = NativeStrings.T("setup.key");
        if (!string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            ServerBox.Text = settings.ServerUrl;
        }

        if (_provision is not null)
        {
            LoginIntro.Text = NativeStrings.T("login.intro");
            AccountText.Text = _account.Display;
            AvatarText.Text = _account.User.Length > 0 ? _account.User[..1].ToUpperInvariant() : "?";
            AccountHint.Text = NativeStrings.T(_account.Kind == LoginKind.Domain ? "login.domainHint" : "login.localHint");
            PasswordLabel.Text = NativeStrings.T("login.password");
            ServerText.Text = string.Format(NativeStrings.T("login.server"), _provision.ServerUrl);
        }
        Show(manual: _provision is null);
        Loaded += (_, _) =>
        {
            if (_manual)
            {
                ServerBox.Focus();
            }
            else if (_account.Kind == LoginKind.Local)
            {
                PasswordBox.Focus();
            }
        };
    }

    private void Show(bool manual)
    {
        _manual = manual;
        TitleText.Text = NativeStrings.T(manual ? "setup.title" : "login.title");
        LoginPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        ManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        PasswordArea.Visibility = !manual && _account.Kind == LoginKind.Local ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.Content = NativeStrings.T(manual ? "setup.connect" : "login.button");
        // 有开通文件时两种方式都能切；没有时只有手动这一种，不显示切换
        SwitchButton.Visibility = _provision is null ? Visibility.Collapsed : Visibility.Visible;
        SwitchButton.Content = NativeStrings.T(manual ? "login.useLogin" : "login.useManual");
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnSwitch(object sender, RoutedEventArgs e) => Show(!_manual);

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        SwitchButton.IsEnabled = false;
        ConnectButton.Content = NativeStrings.T("setup.connecting");
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            if (_manual || _provision is null)
            {
                await ConnectManuallyAsync();
            }
            else
            {
                await LogInAsync(_provision);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("设备注册失败", ex);
            ShowError(string.Format(NativeStrings.T("setup.failed"), ex is OperationCanceledException ? "Timeout" : ex.Message));
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            SwitchButton.IsEnabled = true;
            ConnectButton.Content = NativeStrings.T(_manual ? "setup.connect" : "login.button");
        }
    }

    private async Task LogInAsync(Provision provision)
    {
        if (_account.Kind == LoginKind.Local)
        {
            // 本机账号：服务端校验不了，只能问 Windows 这个密码对不对。密码不存、不发出去
            var password = PasswordBox.Password;
            var (result, message) = await Task.Run(() => WindowsLogon.Verify(_account.User, password));
            if (result != WindowsLogon.Result.Ok)
            {
                ShowError(result == WindowsLogon.Result.WrongPassword
                    ? NativeStrings.T("login.wrongPassword")
                    : string.Format(NativeStrings.T("login.verifyFailed"), message));
                PasswordBox.Clear();
                PasswordBox.Focus();
                return;
            }
        }
        var client = new FlyknitServerClient(ProxyFactory.CreateHttpClient(_settings), provision.ServerUrl, null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reg = await client.RegisterWithTicketAsync(provision.Ticket, _account, _settings.ResolveUiLanguage(), Version(), cts.Token);
        Log.Info($"已登录：{_account.Display}（{_account.Kind}），服务器 {provision.ServerUrl}");
        try
        {
            Provisioning.Keep(AppContext.BaseDirectory, AppPaths.Root);
        }
        catch (Exception ex)
        {
            Log.Warn("保留开通文件失败（不影响这次登录）", ex);
        }
        Save(provision.ServerUrl, reg.Token);
    }

    private async Task ConnectManuallyAsync()
    {
        var url = ServerBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ShowError(string.Format(NativeStrings.T("setup.failed"), "URL"));
            return;
        }
        var client = new FlyknitServerClient(new HttpClient(), url, null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reg = await client.RegisterAsync(KeyBox.Text.Trim(), _settings.ResolveUiLanguage(), Version(), cts.Token);
        Save(url, reg.Token);
    }

    private void Save(string url, string token)
    {
        _settings.ServerUrl = url;
        _settings.DeviceToken = token;
        _settings.Save();
        DialogResult = true;
    }

    private static string Version() => typeof(SetupWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
