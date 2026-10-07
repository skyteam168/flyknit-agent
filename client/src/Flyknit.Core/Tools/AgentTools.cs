using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Memory;
using Flyknit.Core.Security;

namespace Flyknit.Core.Tools;

/// <summary>Agent 用来维护任务计划清单，界面右侧实时显示。</summary>
public sealed class UpdatePlanTool : ITool
{
    public string Name => "update_plan";
    public string Description => "多步骤任务开始前列出计划，并在每步完成后更新状态。每次传入完整的步骤列表。";

    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "完整的步骤列表",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["step"] = new JsonObject { ["type"] = "string", ["description"] = "步骤描述" },
                        ["status"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("pending", "in_progress", "completed"),
                        },
                    },
                    ["required"] = new JsonArray("step", "status"),
                },
            },
        },
        ["required"] = new JsonArray("steps"),
    };

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => "更新任务计划";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult(ToolResult.Fail("缺少 steps 数组"));
        }
        ctx.Plan.Clear();
        foreach (var s in steps.EnumerateArray())
        {
            var status = s.Str("status", "pending");
            if (status is not ("pending" or "in_progress" or "completed"))
            {
                status = "pending";
            }
            ctx.Plan.Add(new PlanItem(s.Str("step"), status));
        }
        ctx.RaisePlanChanged();
        var done = ctx.Plan.Count(p => p.Status == "completed");
        return Task.FromResult(ToolResult.Success($"计划已更新（{done}/{ctx.Plan.Count} 已完成）"));
    }
}

/// <summary>把值得长期记住的信息写入本地记忆（偏好、常用信息、经验、教训）。</summary>
public sealed class MemoryWriteTool : ITool
{
    public string Name => "memory_write";
    public string Description =>
        "把对以后有用的信息记入长期记忆，以后的所有对话都会用到。用户说“记住…”“以后都…”，或纠正了你的做法时使用。" +
        "category：preference=用户偏好与习惯，fact=常用信息（路径、系统、术语），success=有效的做法，lesson=踩过的坑和避免方法。" +
        "不要记录密码等敏感信息，也不要记录一次性的临时数据。";

    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["fact"] = new JsonObject { ["type"] = "string", ["description"] = "要记住的内容，一句话" },
            ["category"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("preference", "fact", "success", "lesson"),
                ["description"] = "类别，默认 fact",
            },
            ["replaces"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "用户改了主意、旧信息过时时，填要取代的那条旧记忆的编号（memory_search 结果里 # 后面的编号）；旧的会留作历史",
            },
        },
        ["required"] = new JsonArray("fact"),
    };

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"记住：{args.Str("fact")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Memory is null)
        {
            return Task.FromResult(ToolResult.Fail("记忆功能不可用"));
        }
        var kind = args.Str("category", "fact").ToLowerInvariant() switch
        {
            "preference" => MemoryKind.Preference,
            "success" => MemoryKind.Success,
            "lesson" => MemoryKind.Lesson,
            _ => MemoryKind.Fact,
        };
        var result = ctx.Memory.Save(kind, args.Required("fact"), "tool", ctx.ConversationId, args.Str("replaces") is { Length: > 0 } old ? old.TrimStart('#') : null);
        return Task.FromResult(result.Outcome switch
        {
            MemoryWriteOutcome.Added => ToolResult.Success("已记住"),
            MemoryWriteOutcome.Updated => ToolResult.Success("已更新"),
            MemoryWriteOutcome.Reinforced => ToolResult.Success($"记忆中已有相同内容：{result.Text}"),
            _ => ToolResult.Fail($"没有记录：{result.Reason}"),
        });
    }
}

/// <summary>搜索长期记忆和历史任务。</summary>
public sealed class MemorySearchTool : ITool
{
    public string Name => "memory_search";
    public string Description =>
        "搜索长期记忆（用户偏好、常用信息、经验教训）和以前完成过的相似任务。" +
        "开始一个不熟悉的任务前、或需要用户以前提供过的信息（路径、格式、习惯）时先搜索，找到就直接用，不要再问用户。";

    public JsonObject Parameters => ToolArgs.Schema(("query", "string", "要找的内容，如“周报格式”“ERP 安装”", true));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"回忆：{args.Str("query")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var query = args.Required("query");
        var sb = new StringBuilder();
        var memories = ctx.Memory?.Search(query, max: 12) ?? new();
        if (memories.Count > 0)
        {
            sb.AppendLine("记忆：");
            foreach (var (item, _) in memories)
            {
                var label = item.Kind switch
                {
                    MemoryKind.Preference => "偏好",
                    MemoryKind.Success => "经验",
                    MemoryKind.Lesson => "教训",
                    _ => "信息",
                };
                var proof = item.ProofCount > 1 ? $"，确认 {item.ProofCount} 次" : "";
                sb.AppendLine($"- [{label} #{item.Id}{proof}] {item.Text}");
            }
        }
        var episodes = ctx.Episodes?.Search(query, max: 3, minScore: 0.15) ?? new();
        if (episodes.Count > 0)
        {
            sb.AppendLine();
            sb.Append(EpisodeStore.BuildPromptSection(episodes));
            ctx.Episodes!.MarkUsed(episodes.Select(e => e.Episode.Id));
        }
        return Task.FromResult(ToolResult.Success(sb.Length == 0 ? "没有找到相关的记忆" : sb.ToString().TrimEnd()));
    }
}

/// <summary>按需加载某个 Skill 的完整说明（渐进式披露：提示词里只有名称和描述，正文用到时才读）。</summary>
public sealed class LoadSkillTool : ITool
{
    public string Name => "load_skill";
    public string Description => "加载某个技能（Skill）的完整说明。当系统提示中列出的某个技能与当前任务相关时，先调用此工具再按说明操作。";
    public JsonObject Parameters => ToolArgs.Schema(("name", "string", "技能名称", true));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"加载技能 {args.Str("name")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var name = args.Required("name");
        var skill = ctx.Skills?.Find(name);
        if (skill is null)
        {
            // 技能存在但被停用时说明原因，避免模型反复尝试
            if (ctx.Skills?.FindAny(name) is { } disabled)
            {
                return Task.FromResult(ToolResult.Fail($"技能 {disabled.Name} 已被用户停用，不能使用。可以告诉用户在「技能」里启用它。"));
            }
            var similar = ctx.Skills?.Search(name, max: 3) ?? new();
            var hint = similar.Count > 0 ? $"相近的技能有：{string.Join("、", similar.Select(s => s.Skill.Name))}" : "可以用 search_skills 按关键词查找";
            return Task.FromResult(ToolResult.Fail($"未找到技能：{name}。{hint}"));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"# 技能：{skill.Name}");
        if (skill.Version.Length > 0)
        {
            sb.AppendLine($"版本：{skill.Version}");
        }
        sb.AppendLine($"技能目录：{skill.Directory}（说明中的相对路径都相对于此目录）");
        sb.AppendLine();
        sb.AppendLine(skill.LoadBody());
        var files = skill.ListFiles();
        if (files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("技能包含的文件（需要时用 read_file 读取，脚本用 run_shell 执行）：");
            foreach (var f in files)
            {
                sb.AppendLine("- " + f);
            }
        }
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}

/// <summary>按关键词检索已安装的技能（技能很多时提示词里只列出相关的几个）。</summary>
public sealed class SearchSkillsTool : ITool
{
    public string Name => "search_skills";
    public string Description =>
        "按关键词检索已安装的技能，返回名称和用途。系统提示里列出的技能都不合适、或者怀疑还有更合适的技能时使用；" +
        "找到后用 load_skill 读取完整说明。";

    public JsonObject Parameters => ToolArgs.Schema(("query", "string", "任务关键词，如“Excel 周报”“PDF 合并”", true));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"查找技能：{args.Str("query")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Skills is null)
        {
            return Task.FromResult(ToolResult.Fail("技能功能不可用"));
        }
        var found = ctx.Skills.Search(args.Required("query"), max: 8);
        if (found.Count == 0)
        {
            var total = ctx.Skills.Skills.Count(s => s.Enabled);
            return Task.FromResult(ToolResult.Success(
                total == 0 ? "还没有安装任何技能，按常规方式完成任务即可。" : "没有找到相关的技能，按常规方式完成任务即可。"));
        }
        var sb = new StringBuilder("找到以下技能（用 load_skill 读取完整说明）：");
        sb.AppendLine();
        foreach (var (skill, _) in found)
        {
            sb.AppendLine($"- {skill.Name}：{skill.Description}");
        }
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}
