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
public sealed class McpTool : ITool
{
    private readonly Func<string, JsonNode?, CancellationToken, Task<McpCallResult>> _call;

    public McpTool(string serverId, string serverName, McpToolInfo info, Func<string, JsonNode?, CancellationToken, Task<McpCallResult>> call)
    {
        ServerId = serverId;
        ServerName = serverName;
        Info = info;
        _call = call;
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

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        if (Info.ReadOnly)
        {
            return PolicyDecision.Auto($"{ServerName} 声明为只读操作");
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
            return result.IsError ? ToolResult.Fail(result.Text) : ToolResult.Success(result.Text);
        }
        catch (McpException ex)
        {
            return ToolResult.Fail($"{ServerName} 调用失败：{ex.Message}");
        }
    }
}
