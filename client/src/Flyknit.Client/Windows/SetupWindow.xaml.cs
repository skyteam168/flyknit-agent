using System;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Flyknit.Client.Services;
using Flyknit.Core.Gateway;

namespace Flyknit.Client.Windows;

/// <summary>首次启动：填写服务器地址与注册密钥，注册本机设备。</summary>
public partial class SetupWindow : Window
{
    private readonly AppSettings _settings;

    public SetupWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        TitleText.Text = NativeStrings.T("setup.title");
        IntroText.Text = NativeStrings.T("setup.intro");
        ServerLabel.Text = NativeStrings.T("setup.server");
        KeyLabel.Text = NativeStrings.T("setup.key");
        ConnectButton.Content = NativeStrings.T("setup.connect");
        if (!string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            ServerBox.Text = settings.ServerUrl;
        }
        Loaded += (_, _) => ServerBox.Focus();
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var url = ServerBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ShowError("URL");
            return;
        }

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = NativeStrings.T("setup.connecting");
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var client = new FlyknitServerClient(new HttpClient(), url, null);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var version = typeof(SetupWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            var reg = await client.RegisterAsync(KeyBox.Text.Trim(), _settings.ResolveUiLanguage(), version, cts.Token);
            _settings.ServerUrl = url;
            _settings.DeviceToken = reg.Token;
            _settings.Save();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Warn("设备注册失败", ex);
            ShowError(ex is OperationCanceledException ? "Timeout" : ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = NativeStrings.T("setup.connect");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = string.Format(NativeStrings.T("setup.failed"), message);
        ErrorText.Visibility = Visibility.Visible;
    }
}
