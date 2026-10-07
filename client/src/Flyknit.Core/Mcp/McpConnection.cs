using System.Text.Json.Nodes;

namespace Flyknit.Core.Mcp;

/// <summary>
/// 按配置选传输方式、握手、拿到工具列表。连上之后用它调工具。
///
/// http 先按 Streamable HTTP 连；对方回 404 / 405（还停在旧版 HTTP+SSE）就自动改用 SSE 再试一次，
/// 和 Claude Code 的做法一样，管理员不用去分清厂商用的是哪一版。
/// </summary>
public sealed class McpConnection : IAsyncDisposable
{
    private McpClient? _client;

    private McpConnection(McpServerConfig config)
    {
        Config = config;
    }

    public McpServerConfig Config { get; }
    public McpClient Client => _client ?? throw new InvalidOperationException("还没连上");
    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = Array.Empty<McpToolInfo>();

    /// <summary>实际用的传输方式：http / sse / stdio。</summary>
    public string TransportUsed { get; private set; } = "";

    /// <summary>连接断了（进程退出、对方关了长连接）。</summary>
    public event Action<string>? Closed;

    /// <summary>对方说工具列表变了，已经重新拉好。</summary>
    public event Action? ToolsChanged;

    /// <param name="bearer">OAuth 连接器：每次发请求前取一下当前的 access_token（过期会先刷新）。</param>
    public static async Task<McpConnection> ConnectAsync(
        McpServerConfig config, HttpClient http, string clientVersion,
        Func<CancellationToken, Task<string?>>? bearer, string? workingDirectory, CancellationToken ct)
    {
        var connection = new McpConnection(config);
        async Task<IReadOnlyDictionary<string, string>> Headers(CancellationToken token)
        {
            var headers = new Dictionary<string, string>(config.Headers, StringComparer.OrdinalIgnoreCase);
            if (bearer is not null && await bearer(token) is { Length: > 0 } accessToken)
            {
                headers["Authorization"] = "Bearer " + accessToken;
            }
            return headers;
        }

        switch (config.Transport)
        {
            case "stdio":
                await connection.StartAsync(new StdioTransport(config, workingDirectory), "stdio", clientVersion, ct);
                break;
            case "sse":
                await connection.StartAsync(new LegacySseTransport(http, ParseUrl(config.Url), Headers), "sse", clientVersion, ct);
                break;
            default:
                var url = ParseUrl(config.Url);
                try
                {
                    await connection.StartAsync(new StreamableHttpTransport(http, url, Headers), "http", clientVersion, ct);
                }
                catch (McpHttpStatusException ex) when (ex.StatusCode is 404 or 405)
                {
                    await connection.StartAsync(new LegacySseTransport(http, url, Headers), "sse", clientVersion, ct);
                }
                break;
        }
        return connection;
    }

    private async Task StartAsync(McpTransport transport, string kind, string clientVersion, CancellationToken ct)
    {
        var client = new McpClient(transport, Config.Timeout);
        try
        {
            await client.ConnectAsync(clientVersion, ct);
            Tools = await client.ListToolsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var detail = transport is StdioTransport stdio && stdio.StderrTail.Length > 0 && ex is not McpAuthRequiredException
                ? $"{ex.Message}\n{Tail(stdio.StderrTail)}"
                : ex.Message;
            await client.DisposeAsync();
            throw ex switch
            {
                McpAuthRequiredException or McpHttpStatusException => ex,
                McpException when detail == ex.Message => ex,
                HttpRequestException http => new McpException($"连不上 {Config.Name}：{http.Message}", http),
                _ => new McpException(detail, ex),
            };
        }
        _client = client;
        TransportUsed = kind;
        client.Closed += reason => Closed?.Invoke(reason);
        client.ToolsChanged += () => _ = RefreshToolsAsync();
    }

    private async Task RefreshToolsAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Tools = await Client.ListToolsAsync(cts.Token);
            ToolsChanged?.Invoke();
        }
        catch (Exception)
        {
            // 拉不到就先用旧的
        }
    }

    public Task<McpCallResult> CallAsync(string tool, JsonNode? arguments, CancellationToken ct) =>
        Client.CallToolAsync(tool, arguments, ct);

    private static Uri ParseUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u
            : throw new McpException($"服务地址不对：{url}");

    private static string Tail(string s) => s.Length <= 600 ? s : "…" + s[^600..];

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
