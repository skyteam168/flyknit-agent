using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Mcp;

/// <summary>
/// 服务端下发的一个 MCP 连接器（管理员在后台引入的厂商）。字段和 server/app/schemas.py 的 McpClientVendorOut 一致。
///
/// 写法沿用 Claude Code 的 .mcp.json：type / url / headers / command / args / env，值里可以写 ${KEY}
/// 或 ${KEY:-默认值}，KEY 对应 Fields 里的一项，由员工连接时填写或管理员统一预填（Preset）。
/// </summary>
public sealed class McpVendor
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("homepage")] public string Homepage { get; set; } = "";

    /// <summary>http（Streamable HTTP，连不上会退回旧版 SSE）/ sse / stdio。</summary>
    [JsonPropertyName("transport")] public string Transport { get; set; } = "http";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("args")] public List<string> Args { get; set; } = new();
    [JsonPropertyName("env")] public Dictionary<string, string> Env { get; set; } = new();
    [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>none / fields / oauth。</summary>
    [JsonPropertyName("auth")] public string Auth { get; set; } = "none";
    [JsonPropertyName("fields")] public List<McpField> Fields { get; set; } = new();
    [JsonPropertyName("preset")] public Dictionary<string, string> Preset { get; set; } = new();
    [JsonPropertyName("needs_input")] public List<string> NeedsInput { get; set; } = new();
    [JsonPropertyName("oauth")] public Dictionary<string, string> OAuth { get; set; } = new();
    [JsonPropertyName("examples")] public List<string> Examples { get; set; } = new();
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; set; } = 60000;
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 配置的指纹。管理员改了地址、命令、密钥模板之后，员工端据此知道要重连；
    /// 只改了介绍文字、图标不算。
    /// </summary>
    public string ConnectionFingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(Transport).Append('\n').Append(Url).Append('\n').Append(Command).Append('\n');
        foreach (var a in Args) sb.Append(a).Append('\u0001');
        foreach (var (k, v) in Env.OrderBy(p => p.Key, StringComparer.Ordinal)) sb.Append(k).Append('=').Append(v).Append('\u0001');
        foreach (var (k, v) in Headers.OrderBy(p => p.Key, StringComparer.Ordinal)) sb.Append(k).Append(':').Append(v).Append('\u0001');
        foreach (var (k, v) in Preset.OrderBy(p => p.Key, StringComparer.Ordinal)) sb.Append(k).Append('=').Append(v).Append('\u0001');
        sb.Append(Auth).Append(TimeoutMs);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}

public sealed class McpField
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("secret")] public bool Secret { get; set; } = true;
    [JsonPropertyName("required")] public bool Required { get; set; } = true;
    [JsonPropertyName("placeholder")] public string Placeholder { get; set; } = "";
    [JsonPropertyName("help")] public string Help { get; set; } = "";
}

/// <summary>展开好、可以直接拿去连接的配置。</summary>
public sealed record McpServerConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Transport { get; init; } = "http";
    public string Url { get; init; } = "";
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>${KEY} / ${KEY:-默认值} 占位符。</summary>
public static partial class McpTemplate
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// 展开占位符。没有值又没有默认值的展开成空字符串——和 Claude Code 一样，
    /// 而且<b>只认连接器自己的填写项</b>，不读这台电脑的环境变量：
    /// 否则管理员写一个 ${OPENAI_API_KEY} 就能把员工电脑上的密钥发给第三方。
    /// </summary>
    public static string Expand(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrEmpty(v) ? v
            : m.Groups[2].Success ? m.Groups[2].Value
            : "");

    /// <summary>模板里有占位符，而且一个都没有值（也没有默认值）。</summary>
    private static bool AllBlank(string template, IReadOnlyDictionary<string, string> values)
    {
        var matches = Placeholder().Matches(template);
        return matches.Count > 0 && matches.All(m =>
            !(values.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrEmpty(v)) && !m.Groups[2].Success);
    }

    /// <summary>还缺哪些必填项（员工没填、管理员也没预填）。</summary>
    public static IReadOnlyList<string> Missing(McpVendor vendor, IReadOnlyDictionary<string, string> values) =>
        vendor.Fields
            .Where(f => f.Required && !(values.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v)))
            .Select(f => f.Key)
            .ToList();

    /// <summary>预填值 + 员工填的值（员工填了的优先），展开成连接配置。</summary>
    public static McpServerConfig Resolve(McpVendor vendor, IReadOnlyDictionary<string, string>? userValues)
    {
        var values = new Dictionary<string, string>(vendor.Preset, StringComparer.Ordinal);
        if (userValues is not null)
        {
            foreach (var (k, v) in userValues)
            {
                if (!string.IsNullOrEmpty(v))
                {
                    values[k] = v;
                }
            }
        }
        return new McpServerConfig
        {
            Id = vendor.Id,
            Name = vendor.Name,
            Transport = vendor.Transport,
            Url = Expand(vendor.Url, values).Trim(),
            Command = Expand(vendor.Command, values).Trim(),
            Args = vendor.Args.Select(a => Expand(a, values)).ToList(),
            Env = vendor.Env.ToDictionary(p => p.Key, p => Expand(p.Value, values)),
            // 展开后是空的头不发：「Authorization: Bearer 」比不带更容易让对方报一个看不懂的错
            Headers = vendor.Headers
                .Select(p => (p.Key, Value: AllBlank(p.Value, values) ? "" : Expand(p.Value, values).Trim()))
                .Where(p => p.Value.Length > 0)
                .ToDictionary(p => p.Key, p => p.Value),
            Timeout = TimeSpan.FromMilliseconds(Math.Clamp(vendor.TimeoutMs, 5000, 600000)),
        };
    }
}

/// <summary>
/// 工具名：mcp__&lt;连接器&gt;__&lt;工具&gt;，和 Claude Code 一致。
/// 前缀把 MCP 工具和内置工具、技能工具彻底隔开：同名的 MCP 工具不会顶掉 read_file，也不会顶掉 load_skill。
/// </summary>
public static class McpNames
{
    public const string Prefix = "mcp__";

    /// <summary>OpenAI 兼容接口要求函数名不超过 64 个字符，只能有字母、数字、_ 和 -。</summary>
    public const int MaxLength = 64;

    public static string ServerPrefix(string serverId) => Prefix + Sanitize(serverId) + "__";

    public static string ToolName(string serverId, string toolName)
    {
        var full = ServerPrefix(serverId) + Sanitize(toolName);
        if (full.Length <= MaxLength)
        {
            return full;
        }
        // 太长就截断，再拼上原名的短哈希，保证两个长名字截出来不会撞在一起
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverId + "/" + toolName)))[..6].ToLowerInvariant();
        return full[..(MaxLength - 7)] + "_" + hash;
    }

    public static bool IsMcp(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }
        return sb.Length == 0 ? "_" : sb.ToString();
    }
}
