using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
    /// <param name="runtime">stdio 连接器用的托管运行环境（见 McpRuntimeManager），没有就用这台电脑自己的。</param>
    public static async Task<McpConnection> ConnectAsync(
        McpServerConfig config, HttpClient http, string clientVersion,
        Func<CancellationToken, Task<string?>>? bearer, string? workingDirectory, CancellationToken ct,
        McpRuntimeEnvironment? runtime = null)
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
                var stdio = new StdioTransport(config, workingDirectory, runtime);
                try
                {
                    await connection.StartAsync(stdio, "stdio", clientVersion, ct);
                }
                catch (McpException) when (BrokenNpxInstall(stdio.StderrTail) is { } broken && TryDeleteDirectory(broken))
                {
                    // npx 上次装到一半被打断，留下缺文件的包，之后每次都会「找不到模块」：删掉重装一次
                    await connection.StartAsync(new StdioTransport(config, workingDirectory, runtime), "stdio", clientVersion, ct);
                }
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

    /// <summary>这些命令第一次运行要先下载安装包，经服务器转一道的话一两分钟很正常。</summary>
    private static readonly HashSet<string> PackageRunners = new(StringComparer.OrdinalIgnoreCase)
    {
        "npx", "pnpx", "bunx", "uvx", "pipx", "pnpm", "yarn", "bun", "uv",
    };

    internal static readonly TimeSpan FirstInstallHandshake = TimeSpan.FromMinutes(5);

    internal static bool InstallsPackages(McpServerConfig config) =>
        config.Transport == "stdio" && PackageRunners.Contains(Path.GetFileNameWithoutExtension(config.Command.Trim().Trim('"').Replace('\\', '/').Split('/')[^1]));

    private static readonly Regex NpxDir = new(
        @"(?:file:///(?=[A-Za-z]:)|file://)?((?:[A-Za-z]:)?[\\/][^'""\s]*?[\\/]_npx[\\/][0-9a-fA-F]{8,})[\\/]node_modules[\\/]",
        RegexOptions.Compiled);

    /// <summary>
    /// npx 装到一半被打断（超时被结束、断网），_npx 下留着缺文件的包；npx 看到目录在就不再装，之后每次启动都报找不到模块。
    /// 从错误输出里认出这种情况，返回那个 _npx\&lt;hash&gt; 目录。
    /// </summary>
    internal static string? BrokenNpxInstall(string stderr)
    {
        if (!stderr.Contains("ERR_MODULE_NOT_FOUND", StringComparison.Ordinal) && !stderr.Contains("Cannot find module", StringComparison.Ordinal)
            && !stderr.Contains("Cannot find package", StringComparison.Ordinal))
        {
            return null;
        }
        var m = NpxDir.Match(stderr);
        if (!m.Success)
        {
            return null;
        }
        var dir = Uri.UnescapeDataString(m.Groups[1].Value).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Directory.Exists(dir) ? dir : null;
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task StartAsync(McpTransport transport, string kind, string clientVersion, CancellationToken ct)
    {
        var client = new McpClient(transport, Config.Timeout)
        {
            HandshakeTimeout = InstallsPackages(Config) ? FirstInstallHandshake : null,
        };
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
