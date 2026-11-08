using System.Text;
using System.Text.Json.Nodes;

namespace Flyknit.Core.Mcp;

/// <summary>对方声明的一个工具。</summary>
public sealed record McpToolInfo(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    bool ReadOnly,
    bool Destructive);

/// <summary>工具调用结果，已经整理成给模型看的文字。</summary>
/// <param name="Images">工具返回的图片（截图、渲染结果）。文字里留了一句说明，图片本身交给调用方决定存哪、给不给模型看。</param>
public sealed record McpCallResult(bool IsError, string Text, IReadOnlyList<McpImage>? Images = null);

/// <summary>MCP 工具返回的一张图片（base64，和协议里一样）。</summary>
public sealed record McpImage(string MimeType, string Base64)
{
    public int ApproxBytes => Base64.Length * 3 / 4;
}

/// <summary>
/// 一个 MCP 会话：握手、列工具、调工具。传输方式由外面选好传进来。
///
/// 只做「工具」这一块。资源（resources）和提示词（prompts）在这里用不上——
/// 员工不会去敲斜杠命令，模型要读什么，厂商一般也都包成了工具。
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    /// <summary>我们支持的最新协议版本。对方回一个更老的也接受。</summary>
    public const string ProtocolVersion = "2025-06-18";

    private static readonly string[] KnownVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly McpTransport _transport;
    private readonly TimeSpan _timeout;

    public string ServerName { get; private set; } = "";
    public string ServerVersion { get; private set; } = "";

    /// <summary>对方给模型的使用说明（initialize 里的 instructions），会放进系统提示词。</summary>
    public string Instructions { get; private set; } = "";

    public string NegotiatedVersion { get; private set; } = ProtocolVersion;

    /// <summary>等握手回应多久。默认不超过 30 秒；npx / uvx 第一次要先下载安装包，调用方会放宽。</summary>
    public TimeSpan? HandshakeTimeout { get; init; }

    /// <summary>对方说工具列表变了。</summary>
    public event Action? ToolsChanged;

    /// <summary>连接断了。</summary>
    public event Action<string>? Closed;

    public McpClient(McpTransport transport, TimeSpan timeout)
    {
        _transport = transport;
        _timeout = timeout;
        _transport.Notification += (method, _) =>
        {
            if (method == "notifications/tools/list_changed")
            {
                ToolsChanged?.Invoke();
            }
        };
        _transport.Closed += reason => Closed?.Invoke(reason);
    }

    public async Task ConnectAsync(string clientVersion, CancellationToken ct)
    {
        await _transport.StartAsync(ct);
        var result = await _transport.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "FlyknitBuddy", ["version"] = clientVersion },
        }, HandshakeTimeout ?? Min(_timeout, TimeSpan.FromSeconds(30)), ct) as JsonObject ?? throw new McpException("对方没有回应握手");

        var version = result["protocolVersion"]?.ToString() ?? "";
        if (!KnownVersions.Contains(version))
        {
            throw new McpException($"对方用的协议版本是 {version}，FlyknitBuddy 还不支持");
        }
        NegotiatedVersion = version;
        _transport.SetProtocolVersion(version);
        if (result["serverInfo"] is JsonObject info)
        {
            ServerName = info["name"]?.ToString() ?? "";
            ServerVersion = info["version"]?.ToString() ?? "";
        }
        Instructions = (result["instructions"]?.ToString() ?? "").Trim();
        await _transport.NotifyAsync("notifications/initialized", null, ct);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        for (var page = 0; page < 50; page++) // 对方翻页翻不完时别死循环
        {
            var parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            var result = await _transport.RequestAsync("tools/list", parameters, Min(_timeout, TimeSpan.FromSeconds(60)), ct) as JsonObject;
            foreach (var node in result?["tools"] as JsonArray ?? new JsonArray())
            {
                if (node is JsonObject t && Parse(t) is { } tool)
                {
                    tools.Add(tool);
                }
            }
            cursor = result?["nextCursor"]?.ToString();
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }
        return tools;
    }

    internal static McpToolInfo? Parse(JsonObject t)
    {
        var name = t["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var annotations = t["annotations"] as JsonObject;
        var schema = t["inputSchema"] as JsonObject ?? new JsonObject();
        schema = (JsonObject)schema.DeepClone();
        // 有的服务不写 type，有的写了 properties 却没写 type，OpenAI 兼容接口会拒收
        schema["type"] = "object";
        schema["properties"] ??= new JsonObject();
        return new McpToolInfo(
            name,
            t["title"]?.ToString() ?? annotations?["title"]?.ToString() ?? "",
            t["description"]?.ToString() ?? "",
            schema,
            ReadOnly: annotations?["readOnlyHint"] is JsonValue r && r.TryGetValue<bool>(out var ro) && ro,
            // 规范里 destructiveHint 默认为 true（只在非只读时有意义），没写就按可能有破坏性算
            Destructive: !(annotations?["destructiveHint"] is JsonValue d && d.TryGetValue<bool>(out var de) && !de));
    }

    public async Task<McpCallResult> CallToolAsync(string name, JsonNode? arguments, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _transport.RequestAsync("tools/call", new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments?.DeepClone() ?? new JsonObject(),
            }, _timeout, ct) as JsonObject ?? new JsonObject();
            var rendered = Render(result);
            // 只记工具名、耗时、大小；参数和返回内容可能有业务数据，不进日志
            _transport.Log?.Write($"调用 {name}：{(rendered.IsError ? "工具报错" : "成功")}，{watch.ElapsedMilliseconds} ms，{rendered.Text.Length} 字" +
                (rendered.Images is { Count: > 0 } imgs ? $"，{imgs.Count} 张图片" : ""));
            return rendered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _transport.Log?.Write($"调用 {name} 失败（{watch.ElapsedMilliseconds} ms）：{ex.Message}");
            throw;
        }
    }

    /// <summary>正在等回应的请求数（健康检查在有调用没回来时不 ping）。</summary>
    public int PendingCount => _transport.PendingCount;

    /// <summary>MCP 的 ping：对方还活着就返回往返耗时。对方不认 ping（回 Method not found）也算活着——它回话了。</summary>
    public async Task<TimeSpan> PingAsync(TimeSpan timeout, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _transport.RequestAsync("ping", null, timeout, ct);
        }
        catch (McpRpcException)
        {
        }
        return watch.Elapsed;
    }

    /// <summary>把工具结果里的各种内容整理成文字。图片、音频模型这边接不住，只说明有这么个东西。</summary>
    internal static McpCallResult Render(JsonObject result)
    {
        var isError = result["isError"] is JsonValue e && e.TryGetValue<bool>(out var err) && err;
        var sb = new StringBuilder();
        var images = new List<McpImage>();
        foreach (var node in result["content"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject item)
            {
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }
            switch (item["type"]?.ToString())
            {
                case "text":
                    sb.Append(item["text"]?.ToString());
                    break;
                case "image" when item["data"]?.ToString() is { Length: > 0 } data:
                    images.Add(new McpImage(item["mimeType"]?.ToString() is { Length: > 0 } mime ? mime : "image/png", data));
                    // 调用方存好文件后会在这句后面补上保存位置
                    sb.Append(ImagePlaceholder(images.Count - 1, images[^1]));
                    break;
                case "image":
                case "audio":
                    var kind = item["type"]!.ToString() == "image" ? "图片" : "音频";
                    var size = (item["data"]?.ToString().Length ?? 0) * 3 / 4 / 1024;
                    sb.Append($"[{kind}：{item["mimeType"]}，约 {size} KB，这里无法显示]");
                    break;
                case "resource":
                    var resource = item["resource"] as JsonObject;
                    sb.Append(resource?["text"]?.ToString() is { Length: > 0 } text
                        ? text
                        : $"[资源：{resource?["uri"]}]");
                    break;
                case "resource_link":
                    sb.Append($"[{item["name"] ?? item["title"]}]({item["uri"]})");
                    break;
                default:
                    sb.Append(item.ToJsonString());
                    break;
            }
        }
        if (sb.Length == 0 && result["structuredContent"] is { } structured)
        {
            sb.Append(structured.ToJsonString());
        }
        if (sb.Length == 0)
        {
            sb.Append(isError ? "工具执行失败，对方没有给出原因" : "（执行成功，没有返回内容）");
        }
        return new McpCallResult(isError, sb.ToString(), images.Count > 0 ? images : null);
    }

    /// <summary>第 i 张图片在文字里的说明，McpTool 存好文件后把它换成带路径的版本。</summary>
    public static string ImagePlaceholder(int index, McpImage image) =>
        $"[图片 {index + 1}：{image.MimeType}，约 {Math.Max(1, image.ApproxBytes / 1024)} KB]";

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
