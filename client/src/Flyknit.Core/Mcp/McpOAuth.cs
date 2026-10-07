using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Mcp;

/// <summary>OAuth 拿到的令牌，加密存在员工电脑上。</summary>
public sealed class McpTokens
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("client_secret")] public string ClientSecret { get; set; } = "";
    [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; set; } = "";
    [JsonPropertyName("resource")] public string Resource { get; set; } = "";

    /// <summary>快过期了（留一分钟余量）。</summary>
    public bool NeedsRefresh(DateTimeOffset now) => ExpiresAt is { } at && at - now < TimeSpan.FromMinutes(1);
}

/// <summary>一次授权流程中间要记住的东西。</summary>
public sealed record McpAuthorization(
    Uri AuthorizeUrl,
    string State,
    string CodeVerifier,
    string RedirectUri,
    string ClientId,
    string ClientSecret,
    string TokenEndpoint,
    string Resource);

/// <summary>
/// MCP 的 OAuth 2.1 授权（2025-06-18 版规范）：
/// 受保护资源元数据 → 授权服务器元数据 → 动态注册客户端（管理员没给 client_id 时）→
/// 浏览器里登录（PKCE）→ 回调到本机 127.0.0.1 → 换令牌 → 过期自动刷新。
/// </summary>
public static partial class McpOAuth
{
    [GeneratedRegex("resource_metadata\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceMetadata();

    /// <summary>PKCE：随机 verifier 和它的 S256 challenge。</summary>
    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>资源标识：MCP 地址去掉片段，作为 resource 参数（RFC 8707），令牌只对这个服务有效。</summary>
    public static string CanonicalResource(Uri mcpUrl)
    {
        var s = mcpUrl.GetLeftPart(UriPartial.Path);
        return mcpUrl.AbsolutePath == "/" ? s.TrimEnd('/') : s;
    }

    /// <summary>
    /// 走一遍发现和注册，拿到浏览器要打开的地址。
    /// </summary>
    /// <param name="wwwAuthenticate">401 回应里的 WWW-Authenticate，可能带着元数据地址。</param>
    /// <param name="configuredClientId">管理员在后台填的 client_id；空则动态注册。</param>
    public static async Task<McpAuthorization> BeginAsync(
        HttpClient http, Uri mcpUrl, string? wwwAuthenticate, string redirectUri,
        string? configuredClientId, string? configuredScopes, CancellationToken ct)
    {
        // 1. 受保护资源元数据：告诉我们去哪个授权服务器登录
        var prm = await FetchProtectedResourceAsync(http, mcpUrl, wwwAuthenticate, ct);

        // 资源标识以对方元数据里声明的为准：令牌的受众要和它一模一样，
        // 自己算的版本差一个结尾斜杠，对方就会认为令牌不是给它的，照样回 401
        var resource = prm?["resource"]?.ToString() is { Length: > 0 } declared && Uri.TryCreate(declared, UriKind.Absolute, out _)
            ? declared
            : CanonicalResource(mcpUrl);
        var issuer = prm?["authorization_servers"] is JsonArray servers && servers.Count > 0 && Uri.TryCreate(servers[0]?.ToString(), UriKind.Absolute, out var s)
            ? s
            : new Uri(mcpUrl.GetLeftPart(UriPartial.Authority)); // 老服务没有这一步，授权服务器就是它自己
        // scope 的优先级：管理员配置的 > 401 里对方点名要的（2025-06-18 起规范建议这样给）> 元数据里支持的全部
        var scopes = configuredScopes;
        if (string.IsNullOrWhiteSpace(scopes))
        {
            scopes = McpAuthRequiredException.ChallengeParam(wwwAuthenticate, "scope");
        }
        if (string.IsNullOrWhiteSpace(scopes) && prm?["scopes_supported"] is JsonArray supported)
        {
            scopes = string.Join(' ', supported.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)));
        }

        // 2. 授权服务器元数据
        var meta = await FetchAuthServerMetadataAsync(http, issuer, ct);
        var root = issuer.GetLeftPart(UriPartial.Authority);
        var authorize = meta?["authorization_endpoint"]?.ToString() ?? root + "/authorize";
        var token = meta?["token_endpoint"]?.ToString() ?? root + "/token";
        var register = meta?["registration_endpoint"]?.ToString() ?? (meta is null ? root + "/register" : null);

        // 3. 客户端：管理员给了就用，没给就动态注册一个
        string clientId, clientSecret = "";
        if (!string.IsNullOrWhiteSpace(configuredClientId))
        {
            clientId = configuredClientId.Trim();
        }
        else if (register is not null)
        {
            (clientId, clientSecret) = await RegisterAsync(http, new Uri(register), redirectUri, ct);
        }
        else
        {
            throw new McpException("这个服务不支持自动注册，请让 IT 在后台给它填上 OAuth client_id");
        }

        // 4. 浏览器要打开的地址
        var (verifier, challenge) = Pkce();
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
            new("state", state),
            new("resource", resource),
        };
        if (!string.IsNullOrWhiteSpace(scopes))
        {
            query.Add(new("scope", scopes!));
        }
        var url = AppendQuery(authorize, query);
        return new McpAuthorization(url, state, verifier, redirectUri, clientId, clientSecret, token, resource);
    }

    private static async Task<JsonObject?> FetchProtectedResourceAsync(HttpClient http, Uri mcpUrl, string? challenge, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (challenge is not null && ResourceMetadata().Match(challenge) is { Success: true } m)
        {
            candidates.Add(m.Groups[1].Value);
        }
        var origin = mcpUrl.GetLeftPart(UriPartial.Authority);
        var path = mcpUrl.AbsolutePath.TrimEnd('/');
        if (path.Length > 0)
        {
            candidates.Add($"{origin}/.well-known/oauth-protected-resource{path}");
        }
        candidates.Add($"{origin}/.well-known/oauth-protected-resource");
        foreach (var url in candidates)
        {
            if (await GetJsonAsync(http, url, ct) is { } json)
            {
                return json;
            }
        }
        return null;
    }

    private static async Task<JsonObject?> FetchAuthServerMetadataAsync(HttpClient http, Uri issuer, CancellationToken ct)
    {
        var origin = issuer.GetLeftPart(UriPartial.Authority);
        var path = issuer.AbsolutePath.TrimEnd('/');
        var candidates = path.Length > 0
            ? new[]
            {
                $"{origin}/.well-known/oauth-authorization-server{path}",
                $"{origin}/.well-known/openid-configuration{path}",
                $"{origin}{path}/.well-known/openid-configuration",
            }
            : new[]
            {
                $"{origin}/.well-known/oauth-authorization-server",
                $"{origin}/.well-known/openid-configuration",
            };
        foreach (var url in candidates)
        {
            if (await GetJsonAsync(http, url, ct) is { } json)
            {
                return json;
            }
        }
        return null;
    }

    private static async Task<(string ClientId, string ClientSecret)> RegisterAsync(HttpClient http, Uri endpoint, string redirectUri, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["client_name"] = "FlyknitBuddy",
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        };
        using var resp = await http.PostAsync(endpoint, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new McpException($"向对方注册客户端失败（HTTP {(int)resp.StatusCode}）：{Clip(text)}");
        }
        var json = StreamableHttpTransport.TryParse(text) as JsonObject ?? throw new McpException("注册客户端时对方返回的不是 JSON");
        var id = json["client_id"]?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            throw new McpException("注册客户端时对方没有给 client_id");
        }
        return (id, json["client_secret"]?.ToString() ?? "");
    }

    /// <summary>用回调拿到的 code 换令牌。</summary>
    public static async Task<McpTokens> ExchangeAsync(HttpClient http, McpAuthorization auth, string code, DateTimeOffset now, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", auth.RedirectUri),
            new("client_id", auth.ClientId),
            new("code_verifier", auth.CodeVerifier),
            new("resource", auth.Resource),
        };
        if (auth.ClientSecret.Length > 0)
        {
            form.Add(new("client_secret", auth.ClientSecret));
        }
        var tokens = await PostTokenAsync(http, auth.TokenEndpoint, form, now, ct);
        tokens.ClientId = auth.ClientId;
        tokens.ClientSecret = auth.ClientSecret;
        tokens.TokenEndpoint = auth.TokenEndpoint;
        tokens.Resource = auth.Resource;
        return tokens;
    }

    /// <summary>用 refresh_token 换新的 access_token。对方没给新的 refresh_token 时沿用旧的。</summary>
    public static async Task<McpTokens> RefreshAsync(HttpClient http, McpTokens current, DateTimeOffset now, CancellationToken ct)
    {
        if (current.RefreshToken.Length == 0)
        {
            throw new McpAuthRequiredException("登录已过期，请重新连接", null);
        }
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", current.RefreshToken),
            new("client_id", current.ClientId),
            new("resource", current.Resource),
        };
        if (current.ClientSecret.Length > 0)
        {
            form.Add(new("client_secret", current.ClientSecret));
        }
        McpTokens fresh;
        try
        {
            fresh = await PostTokenAsync(http, current.TokenEndpoint, form, now, ct);
        }
        catch (McpException ex)
        {
            throw new McpAuthRequiredException($"登录已过期，请重新连接（{ex.Message}）", null);
        }
        fresh.RefreshToken = fresh.RefreshToken.Length > 0 ? fresh.RefreshToken : current.RefreshToken;
        fresh.ClientId = current.ClientId;
        fresh.ClientSecret = current.ClientSecret;
        fresh.TokenEndpoint = current.TokenEndpoint;
        fresh.Resource = current.Resource;
        return fresh;
    }

    private static async Task<McpTokens> PostTokenAsync(HttpClient http, string endpoint, List<KeyValuePair<string, string>> form, DateTimeOffset now, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var json = StreamableHttpTransport.TryParse(text) as JsonObject;
        if (!resp.IsSuccessStatusCode || json is null)
        {
            var reason = json?["error_description"]?.ToString() ?? json?["error"]?.ToString() ?? Clip(text);
            throw new McpException($"换取令牌失败（HTTP {(int)resp.StatusCode}）：{reason}");
        }
        var access = json["access_token"]?.ToString();
        if (string.IsNullOrEmpty(access))
        {
            throw new McpException("对方没有给 access_token");
        }
        DateTimeOffset? expires = json["expires_in"] is JsonValue v && v.TryGetValue<double>(out var seconds) ? now.AddSeconds(seconds)
            : json["expires_in"]?.ToString() is { } str && double.TryParse(str, out var s2) ? now.AddSeconds(s2)
            : null;
        return new McpTokens
        {
            AccessToken = access,
            RefreshToken = json["refresh_token"]?.ToString() ?? "",
            ExpiresAt = expires,
        };
    }

    private static async Task<JsonObject?> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            return StreamableHttpTransport.TryParse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static Uri AppendQuery(string url, IEnumerable<KeyValuePair<string, string>> query)
    {
        var qs = string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new Uri(url + (url.Contains('?') ? "&" : "?") + qs);
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>
/// 在 127.0.0.1 的随机端口上等浏览器把授权码送回来。
/// 用 TcpListener 而不是 HttpListener：后者在 Windows 上要管理员登记 URL 才能监听。
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener;

    public LoopbackReceiver()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => $"http://127.0.0.1:{Port}/callback";

    /// <summary>等回调，返回查询参数。state 对不上的请求不算（防止别的网页伪造回调）。</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(string expectedState, string successHtml, string failureHtml, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            // 读掉请求头
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct)))
            {
            }
            var parts = requestLine.Split(' ');
            var target = parts.Length > 1 ? parts[1] : "/";
            var query = ParseQuery(target);
            var ok = target.StartsWith("/callback", StringComparison.Ordinal)
                     && query.TryGetValue("state", out var state) && state == expectedState;
            var html = ok && query.ContainsKey("code") ? successHtml : failureHtml;
            var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><title>FlyknitBuddy</title><body style=\"font:16px system-ui;padding:48px;text-align:center\">{WebUtility.HtmlEncode(html)}</body>");
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, ct);
            await stream.WriteAsync(body, ct);
            if (ok)
            {
                return query;
            }
        }
    }

    public static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = target.IndexOf('?');
        if (q < 0)
        {
            return result;
        }
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    public void Dispose() => _listener.Stop();
}
