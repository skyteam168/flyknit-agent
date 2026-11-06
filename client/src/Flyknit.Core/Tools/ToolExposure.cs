using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Chat;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;

namespace Flyknit.Core.Tools;

/// <summary>
/// 外部服务（MCP）工具按需加载：连接器多了，每次请求都把几十个工具的完整参数说明发过去，能占好几万 token。
/// 超过 <see cref="DeferAbove"/> 个时，只发内置工具和「已加载」的 MCP 工具；其余的只在 load_tools 的说明里列名字和一句话，
/// AI 要用时先 load_tools，下一步起就带上完整说明（同 Claude Code 的 tool search）。
/// 对话里已经调用过的工具自动算已加载：历史里的调用要能对上工具定义。
/// </summary>
public static class ToolExposure
{
    /// <summary>MCP 工具不超过这么多个时全部直接发（加载这一步省不了多少，反而多一轮）。</summary>
    public const int DeferAbove = 12;

    /// <summary>这一步发给模型的工具。</summary>
    public static IReadOnlyList<ITool> Visible(IEnumerable<ITool> all, ICollection<string> loaded, IEnumerable<ChatMessage>? history = null, int deferAbove = DeferAbove)
    {
        var list = all.ToList();
        if (!Deferring(list, deferAbove))
        {
            return list.Where(t => t.Name != LoadToolsTool.ToolName).ToList();
        }
        var used = UsedIn(history);
        return list.Where(t => t is not McpTool || loaded.Contains(t.Name) || used.Contains(t.Name)).ToList();
    }

    /// <summary>MCP 工具多到要按需加载了没有。</summary>
    public static bool Deferring(IEnumerable<ITool> all, int deferAbove = DeferAbove) => all.Count(t => t is McpTool) > deferAbove;

    /// <summary>对话里调用过的工具名。</summary>
    public static HashSet<string> UsedIn(IEnumerable<ChatMessage>? history) =>
        (history ?? Array.Empty<ChatMessage>())
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.ToolCalls)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// 加载外部服务工具的完整参数说明。说明里列出全部可用的 MCP 工具（名字 + 一句话），按工具名或关键字加载。
/// 本身不读写任何东西，不需要确认。
/// </summary>
public sealed class LoadToolsTool : ITool
{
    public const string ToolName = "load_tools";

    /// <summary>一次最多加载几个：一次全加载就失去按需的意义了。</summary>
    public const int MaxPerCall = 8;

    /// <summary>目录里每个工具的说明最多这么多字。</summary>
    private const int SummaryChars = 60;

    private readonly ToolRegistry _tools;

    public LoadToolsTool(ToolRegistry tools) => _tools = tools;

    public string Name => ToolName;

    public string Description
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("加载外部服务（MCP）工具的完整参数说明。下面这些工具要先用本工具加载，加载后从下一步起可以直接调用；已经加载过、用过的不用再加载。");
            sb.Append("可以写工具全名（names），也可以写关键字（query）按名字和说明找。可加载的工具：");
            foreach (var t in _tools.All.OfType<McpTool>().OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var line = t.Description.ReplaceLineEndings(" ").Trim();
                sb.Append("\n- ").Append(t.Name).Append('：').Append(line.Length > SummaryChars ? line[..SummaryChars] + "…" : line);
            }
            return sb.ToString();
        }
    }

    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["names"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "要加载的工具全名，例如 mcp__mes__query",
            },
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "不知道全名时按关键字找（匹配工具名和说明）" },
        },
        ["required"] = new JsonArray(),
    };

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();

    public string Describe(JsonElement args) => "加载外部服务工具";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var mcp = _tools.All.OfType<McpTool>().ToList();
        var picked = new List<McpTool>();
        var unknown = new List<string>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in names.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").Where(x => x.Length > 0))
            {
                if (mcp.FirstOrDefault(t => t.Name == n) is { } hit)
                {
                    picked.Add(hit);
                }
                else
                {
                    unknown.Add(n);
                }
            }
        }
        var query = args.Str("query").Trim();
        if (query.Length > 0)
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            picked.AddRange(mcp
                .Select(t => (Tool: t, Score: words.Count(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(w, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Tool));
        }
        picked = picked.DistinctBy(t => t.Name).Take(MaxPerCall).ToList();
        if (picked.Count == 0)
        {
            var hint = unknown.Count > 0 ? $"没有这些工具：{string.Join("、", unknown)}。" : "";
            return Task.FromResult(ToolResult.Fail(hint + "没有找到要加载的工具。看一下本工具说明里的工具清单，写全名，或者换个关键字"));
        }

        foreach (var t in picked)
        {
            ctx.LoadedTools.Add(t.Name);
        }
        // 结果里直接附上参数说明：模型这一步就能看到，不用等下一步的工具定义
        var sb = new StringBuilder($"已加载 {picked.Count} 个工具，下一步起可以直接调用：");
        foreach (var t in picked)
        {
            sb.Append("\n\n## ").Append(t.Name).Append('\n').Append(t.Description.Trim());
            sb.Append("\n参数：").Append(t.Parameters.ToJsonString());
        }
        if (unknown.Count > 0)
        {
            sb.Append($"\n\n没有这些工具：{string.Join("、", unknown)}");
        }
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}
