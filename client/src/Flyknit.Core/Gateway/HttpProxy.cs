using System.Net;

namespace Flyknit.Core.Gateway;

/// <summary>
/// 按员工在设置里选的代理方式建 HttpClientHandler：direct（直连）/ system（系统代理，默认）/ manual（手填地址，可带账号）。
/// 员工端和命令行用同一份设置、同一套规则，所以放在 Core。
/// </summary>
public static class HttpProxy
{
    public const string Direct = "direct";
    public const string System = "system";
    public const string Manual = "manual";

    public static HttpClientHandler CreateHandler(string? mode, string? url, string? user, string? password, Action<string>? warn = null)
    {
        var handler = new HttpClientHandler();
        switch ((mode ?? System).ToLowerInvariant())
        {
            case Direct:
                handler.UseProxy = false;
                break;

            case Manual:
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var proxy = new WebProxy(uri);
                    if (!string.IsNullOrWhiteSpace(user))
                    {
                        proxy.Credentials = new NetworkCredential(user, password);
                    }
                    handler.Proxy = proxy;
                    handler.UseProxy = true;
                }
                else
                {
                    warn?.Invoke($"代理地址无效，按直连处理：{url}");
                    handler.UseProxy = false;
                }
                break;

            default: // system
                handler.UseProxy = true;
                handler.Proxy = WebRequest.GetSystemWebProxy();
                handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
                break;
        }
        return handler;
    }
}
