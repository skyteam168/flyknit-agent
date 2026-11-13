using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Mcp;

/// <summary>
/// 把 MCP 服务的一个工具包成 Agent 能调的工具。
///
/// 名字是 mcp__&lt;连接器&gt;__&lt;工具&gt;，所以不会和内置工具、技能工具重名。
///
/// 要不要问用户，按 Claude Code 的思路：外部服务的工具默认要确认。
/// 对方声明了只读（readOnlyHint）的直接执行；声明了不会破坏数据的（destructiveHint=false）
/// 可以选「以后自动执行」；其余每次都问——我们不知道它会对对方系统做什么。
/// </summary>
public sealed class McpTool : ITool, IParallelTool
{
    private readonly Func<string, JsonNode?, CancellationToken, Task<McpCallResult>> _call;
    private readonly Func<bool>? _markedReadOnly;

    /// <param name="markedReadOnly">管理员在后台把这个工具标成只读了没有（docs/approval-p0.md 第 6 节）。
    /// 每次判定时现查：管理员改了标注，员工端下次拉到连接器配置就生效，不用重连。</param>
    public McpTool(string serverId, string serverName, McpToolInfo info, Func<string, JsonNode?, CancellationToken, Task<McpCallResult>> call,
        Func<bool>? markedReadOnly = null)
    {
        ServerId = serverId;
        ServerName = serverName;
        Info = info;
        _call = call;
        _markedReadOnly = markedReadOnly;
        Name = McpNames.ToolName(serverId, info.Name);
        var text = info.Description.Length > 1000 ? info.Description[..1000] + "…" : info.Description;
        Description = $"[{serverName}] {(text.Length > 0 ? text : info.Title.Length > 0 ? info.Title : info.Name)}";
    }

    public string ServerId { get; }
    public string ServerName { get; }
    public McpToolInfo Info { get; }

    public string Name { get; }
    public string Description { get; }
    public JsonObject Parameters => Info.InputSchema;

    /// <summary>
    /// 只读：对方声明了 readOnlyHint，或者管理员在后台标成了只读。
    /// 管理员的标注只补对方没写的：对方声明了 destructiveHint 的，标了也不算——以对方说的「会删改数据」为准。
    /// </summary>
    public bool IsReadOnly => Info.ReadOnly || (!Info.Destructive && _markedReadOnly?.Invoke() == true);

    /// <summary>只读的可以和相邻的只读调用同时跑（查几个页面、读几个文件）；会改东西的照旧一个一个来。</summary>
    public bool CanRunInParallel(JsonElement args) => IsReadOnly;

    /// <summary>一次结果里最多收几张图片（再多的只在文字里说明）。</summary>
    public const int MaxImages = 4;

    /// <summary>单张图片超过这么大就不收（模型接口一般也不收）。</summary>
    public const int MaxImageBytes = 8 * 1024 * 1024;

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        if (Info.ReadOnly)
        {
            return PolicyDecision.Auto($"{ServerName} 声明为只读操作");
        }
        if (IsReadOnly)
        {
            return PolicyDecision.Auto($"管理员把「{ServerName} · {(Info.Title.Length > 0 ? Info.Title : Info.Name)}」标为只读");
        }
        var reason = $"{ServerName} 的操作会在对方系统里生效";
        if (Info.Destructive)
        {
            // 可能删改对方数据：每次都问，不给「以后自动执行」
            return PolicyDecision.Confirm(reason) with { Effect = CommandEffect.Destructive };
        }
        var label = Info.Title.Length > 0 ? Info.Title : Info.Name;
        return PolicyDecision.Confirm(reason) with
        {
            Effect = CommandEffect.Write,
            // 规则按「这个工具」记：允许过一次「腾讯文档 · 新建表格」，以后同一个工具不再问，别的工具照问
            Rule = new ApprovalCandidate(Name, "*", Name.ToLowerInvariant(), Name.ToLowerInvariant(), "*", $"{ServerName} · {label}"),
        };
    }

    /// <summary>
    /// 卡片上那一行：挑前几个简单参数拼成「键：值」。界面已经单独显示「厂商 · 工具」，这里不再重复；
    /// 完整参数展开卡片能看到。
    /// </summary>
    public string Describe(JsonElement args)
    {
        var parts = new List<string>();
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in args.EnumerateObject())
            {
                var value = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                    JsonValueKind.Array => $"[{p.Value.GetArrayLength()} 项]",
                    JsonValueKind.Object => "{…}",
                    _ => "",
                };
                if (value.Length == 0)
                {
                    continue;
                }
                parts.Add($"{p.Name}：{(value.Length > 60 ? value[..60] + "…" : value)}");
                if (parts.Count == 3)
                {
                    break;
                }
            }
        }
        return parts.Count > 0 ? string.Join("，", parts) : (Info.Title.Length > 0 ? Info.Title : Info.Name);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var arguments = args.ValueKind == JsonValueKind.Object ? JsonNode.Parse(args.GetRawText()) : new JsonObject();
        try
        {
            var result = await _call(Info.Name, arguments, ct);
            var tool = result.IsError ? ToolResult.Fail(result.Text) : ToolResult.Success(result.Text);
            return result.Images is { Count: > 0 } images ? tool with { Images = Decode(images) } : tool;
        }
        catch (McpException ex)
        {
            return ToolResult.Fail($"{ServerName} 调用失败：{ex.Message}");
        }
    }

    private static List<ToolImage> Decode(IReadOnlyList<McpImage> images)
    {
        var list = new List<ToolImage>();
        for (var i = 0; i < images.Count && list.Count < MaxImages; i++)
        {
            var img = images[i];
            if (!img.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || img.ApproxBytes > MaxImageBytes)
            {
                continue;
            }
            try
            {
                list.Add(new ToolImage(img.MimeType, Convert.FromBase64String(img.Base64), McpClient.ImagePlaceholder(i, img)));
            }
            catch (FormatException)
            {
                // base64 坏了：文字里那句说明还在，图片丢掉
            }
        }
        return list;
    }
}
