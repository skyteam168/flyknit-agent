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
        return PermissionRules.ForWrite(Policy, Permission, Workspace, full, delete);
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
