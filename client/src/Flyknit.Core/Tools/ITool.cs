using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Gateway;
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

    /// <summary>工具自己调用了模型（派遣专家）：用掉的 token，计入这次任务的总预算和用量。</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>计入预算的 token 数（含上游没报用量时的估算），可能比 <see cref="Usage"/> 多。</summary>
    public long BudgetTokens { get; init; }

    /// <summary>要求本次运行到此为止（专家交卷）：这一轮剩下的调用不执行，也不再调模型。</summary>
    public bool EndRun { get; init; }

    /// <summary>执行记录里这一步换个样子记（派遣专家记成 subtask 步骤）。</summary>
    public ToolTraceNote? Trace { get; init; }

    /// <summary>
    /// 工具返回的图片（外部服务的截图、渲染结果）。Agent 循环存成文件、挂到这条工具结果上：
    /// 员工在界面上能打开看；模型支持识图时，下一次请求把图片一起发给它（见 OpenAiSerializer）。
    /// 文字里用 <see cref="ToolImage.Marker"/> 标出位置，存好后换成保存路径。
    /// </summary>
    public IReadOnlyList<ToolImage> Images { get; init; } = Array.Empty<ToolImage>();

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

/// <summary>工具返回的一张图片。Marker 是它在结果文字里的那句说明（存好文件后在后面补上路径）。</summary>
public sealed record ToolImage(string Mime, byte[] Data, string Marker);

/// <summary>执行记录里这一步的类型、名字和说明。</summary>
public sealed record ToolTraceNote(string Kind, string Name, string Summary, string Detail);

/// <summary>执行时间可能超过默认工具超时的工具（派遣专家），自己声明上限。</summary>
public interface ILongRunningTool
{
    TimeSpan Timeout { get; }
}

/// <summary>
/// 可以和同一轮里相邻的同类调用同时执行的工具（目前只有派遣只读专家）。
/// 判定、确认仍然按顺序做；执行时 <see cref="ToolContext.CurrentCallId"/> 只在 ExecuteAsync 第一个 await 之前可靠，工具要当场取走。
/// </summary>
public interface IParallelTool
{
    bool CanRunInParallel(JsonElement args);
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

    /// <summary>这一轮依据的任务授权记录（远程 / 定时 / 事件任务）；员工自己发起的为空。写进审计。</summary>
    public string GrantId { get; init; } = "";

    /// <summary>这次运行（员工的一句话引起的一轮）的 ID。派遣专家按它计次。</summary>
    public string RunId { get; init; } = "";

    /// <summary>这次运行的预算，由 AgentLoop 开始运行时填上。派遣专家前据此预留额度。</summary>
    public Agent.RunBudget? Budget { get; set; }

    /// <summary>正在执行的工具调用的 ID（工具按顺序一个一个执行）。派遣专家时用它把界面上的卡片和子任务对上。</summary>
    public string CurrentCallId { get; set; } = "";

    /// <summary>
    /// 这个对话里已经用 load_tools 加载过的外部服务工具（见 ToolExposure）。宿主按对话保存，同一个对话下一轮还算加载过。
    /// </summary>
    public ISet<string> LoadedTools { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>IT 写的工具规则（拦截 / 必须确认 / 提醒 / 结果打码）。</summary>
    public Security.ToolRules? Rules { get; init; }

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

    /// <summary>
    /// 记下这一轮对文件做的改动（新建、修改、删除），任务结束后员工能看到改了哪些、一键还原。为空表示不记（单测、专家子任务）。
    /// </summary>
    public Action<FileChange>? RecordChange { get; init; }

    /// <summary>记一条改动；记录失败不影响工具本身。</summary>
    public void NoteChange(FileChange change)
    {
        try
        {
            RecordChange?.Invoke(change);
        }
        catch (Exception)
        {
        }
    }

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

    /// <summary>AI 浏览器（看得见的网页窗口）。为空表示没开启浏览器操作。</summary>
    public Browser.IBrowserDriver? Browser { get; init; }

    /// <summary>终端：命令的输出实时推给界面，后台命令也记在这里。为空（单测、专家子任务）时每条命令单独跑。</summary>
    public Terminal.TerminalHub? Terminal { get; init; }

    /// <summary>能不能在后台启动一直运行的命令（IT 在安全中心打开「后台命令」）。默认不能。</summary>
    public bool BackgroundCommands =>
        Security?.On(Flyknit.Core.Security.SecuritySettings.BackgroundCommands, fallback: false) ?? false;

    /// <summary>这一轮已经建议了几件新任务（suggest_task 限制次数用）。</summary>
    public int SuggestionsMade { get; set; }

    /// <summary>
    /// 到这一步为止的对话（不含正在执行的这条带工具调用的回答），由 Agent 循环在执行工具前填上。
    /// fork 子代理靠它继承主助手看到的全部上下文——前缀和主助手一样，上游的提示词缓存也能命中。
    /// </summary>
    public Func<IReadOnlyList<Chat.ChatMessage>>? ForkHistory { get; set; }

    /// <summary>
    /// 给子代理用的上下文：权限取更严的、运行 ID 换成子任务的；不带浏览器（同一个窗口，几个子代理一起点会互相干扰）；
    /// 改动照样记进这一轮（员工能看到、能一键还原）。
    /// </summary>
    public ToolContext ForChild(string runId, PermissionMode permission) => new()
    {
        Policy = Policy,
        ConversationId = ConversationId,
        Workspace = Workspace,
        Permission = (PermissionMode)Math.Min((int)Permission, (int)permission),
        GrantId = GrantId,
        RunId = runId,
        LoadedTools = new HashSet<string>(LoadedTools, StringComparer.Ordinal),
        Rules = Rules,
        WorkingDirectory = WorkingDirectory,
        Memory = Memory,
        UserRequest = UserRequest,
        Episodes = Episodes,
        Skills = Skills,
        Deleter = Deleter,
        Backup = Backup,
        RecordChange = RecordChange,
        Security = Security,
        Terminal = Terminal,
    };

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
