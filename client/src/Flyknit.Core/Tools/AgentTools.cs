using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>把值得长期记住的信息写入本地 memory.md。</summary>
public sealed class MemoryWriteTool : ITool
{
    public string Name => "memory_write";
    public string Description => "把关于用户的长期有用信息记下来（如常用路径、工作习惯、偏好），以后的对话都能用到。不要记录密码等敏感信息。";
    public JsonObject Parameters => ToolArgs.Schema(("fact", "string", "要记住的一条信息，一句话", true));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"记住：{args.Str("fact")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Memory is null)
        {
            return Task.FromResult(ToolResult.Fail("记忆功能不可用"));
        }
        ctx.Memory.Remember(args.Required("fact"));
        return Task.FromResult(ToolResult.Success("已记住"));
    }
}

/// <summary>按需加载某个 Skill 的完整说明。</summary>
public sealed class LoadSkillTool : ITool
{
    public string Name => "load_skill";
    public string Description => "加载某个技能（Skill）的完整说明。当系统提示中列出的某个技能与当前任务相关时，先调用此工具再按说明操作。";
    public JsonObject Parameters => ToolArgs.Schema(("name", "string", "技能名称", true));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"加载技能 {args.Str("name")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var skill = ctx.Skills?.Find(args.Required("name"));
        if (skill is null)
        {
            return Task.FromResult(ToolResult.Fail($"未找到技能：{args.Str("name")}"));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"# 技能：{skill.Name}");
        sb.AppendLine($"技能目录：{skill.Directory}（说明中的相对路径都相对于此目录）");
        sb.AppendLine();
        sb.AppendLine(skill.LoadBody());
        var files = skill.ListFiles();
        if (files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("技能包含的文件：");
            foreach (var f in files)
            {
                sb.AppendLine("- " + f);
            }
        }
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}
