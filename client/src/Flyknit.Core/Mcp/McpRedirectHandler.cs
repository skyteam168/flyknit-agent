using System.Net;

namespace Flyknit.Core.Mcp;

/// <summary>
/// MCP 请求自己处理重定向。
///
/// .NET 自动跟随重定向时会把 Authorization 头丢掉（防止令牌被转发到别的网站）。很多 MCP 服务
/// 部署时 /mcp 会 307 到 /mcp/（Starlette、FastMCP 的默认行为），结果是：浏览器里登录成功、
/// 令牌也拿到了，可每次请求一跳转令牌就没了，对方只看到「没登录」，一直回 401。
///
/// 这里的规则：同一个站点（协议 + 主机 + 端口都相同）的跳转带着所有请求头照跳；
/// 跳到别的站点就去掉 Authorization 和 Cookie，跟浏览器、curl 的安全做法一致。
/// 307 / 308 原样重发方法和正文；303 改成 GET；301 / 302 也保留方法——MCP 只用 POST，改成 GET 只会换来 405。
/// </summary>
public sealed class McpRedirectHandler : DelegatingHandler
{
    public const int MaxRedirects = 5;

    public McpRedirectHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        var current = request;
        for (var hop = 0; hop < MaxRedirects && IsRedirect(response.StatusCode) && response.Headers.Location is { } location; hop++)
        {
            var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp)
            {
                break;
            }
            var next = new HttpRequestMessage(response.StatusCode == HttpStatusCode.SeeOther ? HttpMethod.Get : current.Method, target)
            {
                Version = current.Version,
            };
            if (next.Method != HttpMethod.Get && next.Method != HttpMethod.Head)
            {
                next.Content = current.Content;
            }
            var sameOrigin = SameOrigin(current.RequestUri!, target);
            foreach (var header in current.Headers)
            {
                if (!sameOrigin && (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                                    || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Dispose();
            current = next;
            response = await base.SendAsync(next, ct);
        }
        return response;
    }

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase)
        && a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
