using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Memory;
using Flyknit.Core.Security;
using Flyknit.Core.Skills;

namespace Flyknit.Core.Tools;

public sealed record ToolResult(bool Ok, string Output)
{
    public const int DefaultMaxChars = 12000;

    /// <summary>
    /// 这次执行产出或改动的文件（绝对路径）。工具自己最清楚写了什么，就由工具直接声明；
    /// run_shell 这种跑任意程序的，由 OutputTracker 比对工作目录得出。
    /// 界面据此给出「打开 / 在资源管理器中显示 / 预览」的卡片。
    /// </summary>
    public IReadOnlyList<string> Outputs { get; init; } = Array.Empty<string>();

    public ToolResult WithOutputs(params string[] paths) => this with { Outputs = paths };

    public static ToolResult Success(string output) => new(true, output);
    public static ToolResult Fail(string error) => new(false, error);

    /// <summary>截断过长的输出，避免撑爆模型上下文。</summary>
    public ToolResult Truncate(int maxChars = DefaultMaxChars)
    {
        if (Output.Length <= maxChars)
        {
            return this;
        }
        var head = Output[..(maxChars * 2 / 3)];
        var tail = Output[^(maxChars / 3)..];
        return this with { Output = $"{head}\n\n…（已省略 {Output.Length - maxChars} 个字符）…\n\n{tail}" };
    }
}

public sealed record PlanItem(string Step, string Status);

/// <summary>任务计划的保存格式与原样呈现。</summary>
public static class TaskPlan
{
    public static bool HasOpenSteps(IReadOnlyList<PlanItem> plan) => plan.Any(p => p.Status != "completed");

    public static string? Serialize(IReadOnlyList<PlanItem> plan) =>
        plan.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(plan.Select(p => new { step = p.Step, status = p.Status }));

    public static List<PlanItem> Parse(string? json)
    {
        var list = new List<PlanItem>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.TryGetProperty("step", out var step) && step.GetString() is { Length: > 0 } text)
                {
                    var status = e.TryGetProperty("status", out var st) ? st.GetString() ?? "pending" : "pending";
                    list.Add(new PlanItem(text, status));
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        return list;
    }

    /// <summary>原样列出计划（不经模型改写），压缩上下文后放在摘要旁边。</summary>
    public static string Render(IReadOnlyList<PlanItem> plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<当前任务计划>");
        sb.AppendLine("这是你之前用 update_plan 列的计划（原样保留），继续按它推进，完成一步更新一次：");
        for (var i = 0; i < plan.Count; i++)
        {
            var mark = plan[i].Status switch { "completed" => "[x]", "in_progress" => "[>]", _ => "[ ]" };
            sb.AppendLine($"{i + 1}. {mark} {plan[i].Step}");
        }
        sb.Append("</当前任务计划>");
        return sb.ToString();
    }
}

/// <summary>删除文件的方式。Windows 客户端实现为移入回收站。</summary>
public interface IFileDeleter
{
    void Delete(string path);
}

public sealed class PermanentDeleter : IFileDeleter
{
    public void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }
}

/// <summary>工具执行时可用的上下文。</summary>
public sealed class ToolContext
{
    public required CommandPolicy Policy { get; init; }
    public required string ConversationId { get; init; }
    /// <summary>任务的工作区：相对路径、命令默认工作目录、产出文件都在这里。</summary>
    public string? Workspace { get; init; }

    /// <summary>用户为任务选择的权限模式。</summary>
    public PermissionMode Permission { get; init; } = PermissionMode.Workspace;

    private readonly string? _workingDirectory;

    public string WorkingDirectory
    {
        get => _workingDirectory ?? Workspace ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        init => _workingDirectory = value;
    }

    public MemoryStore? Memory { get; init; }

    /// <summary>用户这一轮说的话。memory_write 用它核对“用户原话”，防止把文件、网页里的话当成用户的要求记下来。</summary>
    public string UserRequest { get; init; } = "";
    public EpisodeStore? Episodes { get; init; }
    public SkillCatalog? Skills { get; init; }
    public IFileDeleter Deleter { get; init; } = new PermanentDeleter();

    /// <summary>覆盖写之前留一份原文件。为空表示不备份（单测里就是这样）。</summary>
    public FileBackup? Backup { get; init; }

    /// <summary>安全中心的设置。为空时按策略里的默认值走。</summary>
    public Flyknit.Core.Security.SecuritySettings? Security { get; init; }

    /// <summary>网络白名单是否生效。服务端还没下发这一项时按生效算。</summary>
    public bool NetworkAllowlist =>
        Security?.On(Flyknit.Core.Security.SecuritySettings.NetworkAllowlist) ?? true;

    /// <summary>能不能用注册表、服务、计划任务、WMI。默认不能。</summary>
    public bool SystemTools =>
        Security?.On(Flyknit.Core.Security.SecuritySettings.SystemTools) ?? false;

    /// <summary>工作区隔离是否生效。服务端还没下发这一项时按生效算。</summary>
    public bool Sandboxed =>
        Security?.On(Flyknit.Core.Security.SecuritySettings.Sandbox) ?? true;

    /// <summary>
    /// 实际生效的权限模式。
    ///
    /// 工作区隔离开着时，「完全权限」最多按「工作区内修改」算——隔离的含义就是工作区
    /// 之外不能写。这一档归 IT：员工在输入框里选什么都越不过去，需要哪台机器用完全
    /// 权限，IT 在后台给那台单独关掉隔离。
    /// </summary>
    public PermissionMode EffectivePermission =>
        Sandboxed && Permission == PermissionMode.Full ? PermissionMode.Workspace : Permission;

    /// <summary>一次删除多少个文件就要强制确认。安全中心改了这里要跟着变。</summary>
    public int BatchDeleteThreshold =>
        Security?.Number(Flyknit.Core.Security.SecuritySettings.BatchDeleteThreshold,
            Policy?.Config.BatchConfirmThreshold ?? 20)
        ?? Policy?.Config.BatchConfirmThreshold ?? 20;

    /// <summary>update_plan 工具写入的计划，Agent 循环会把变化通知界面。</summary>
    public List<PlanItem> Plan { get; } = new();

    public event Action<IReadOnlyList<PlanItem>>? PlanChanged;

    internal void RaisePlanChanged() => PlanChanged?.Invoke(Plan.ToList());

    public string ResolvePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(WorkingDirectory, expanded));
    }

    /// <summary>写入或删除路径的判定（结合权限模式与工作区）。</summary>
    public PolicyDecision AssessWrite(string rawPath, bool delete = false)
    {
        string full;
        try
        {
            full = ResolvePath(rawPath);
        }
        catch (Exception)
        {
            return PolicyDecision.Blocked("路径无效");
        }
        return PermissionRules.ForWrite(Policy, EffectivePermission, Workspace, full, delete);
    }

    public bool InWorkspace(string fullPath) => PermissionRules.IsInWorkspace(fullPath, Workspace);
}

public interface ITool
{
    /// <summary>工具名，模型通过它调用。</summary>
    string Name { get; }

    /// <summary>给模型看的说明。</summary>
    string Description { get; }

    /// <summary>参数的 JSON Schema。</summary>
    JsonObject Parameters { get; }

    /// <summary>根据参数评估风险等级。</summary>
    PolicyDecision Assess(JsonElement args, ToolContext ctx);

    /// <summary>给用户看的一句话描述，用于确认卡片。</summary>
    string Describe(JsonElement args);

    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct);
}

public static class ToolArgs
{
    public static string Str(this JsonElement args, string name, string fallback = "")
    {
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()
            : fallback;
    }

    public static string Required(this JsonElement args, string name)
    {
        var value = args.Str(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"缺少参数 {name}");
        }
        return value;
    }

    public static int Int(this JsonElement args, string name, int fallback)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            {
                return n;
            }
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n))
            {
                return n;
            }
        }
        return fallback;
    }

    public static bool Bool(this JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
                _ => fallback,
            };
        }
        return fallback;
    }

    /// <summary>构造 JSON Schema 的简便方法。</summary>
    public static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Required)
            {
                required.Add(p.Name);
            }
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }
}
