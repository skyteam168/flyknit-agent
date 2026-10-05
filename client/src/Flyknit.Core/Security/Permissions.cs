using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>
/// 用户为任务选择的权限模式。无论哪种模式，管理员下发的禁止规则（rm -rf、format、修改系统目录等）始终生效。
/// </summary>
public enum PermissionMode
{
    /// <summary>仅可查看：只能读取文件、执行查询类命令，任何修改都会被阻止。</summary>
    ReadOnly,

    /// <summary>工作区内修改（默认）：只能在工作区内创建、修改文件；执行命令需要确认，同样的命令确认一次后自动通过。</summary>
    Workspace,

    /// <summary>完全权限：可以修改工作区外的文件，普通命令无需确认（危险命令仍被阻止，删除工作区外的文件仍需确认）。</summary>
    Full,
}

public static class PermissionModes
{
    public static string ToText(PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "readonly",
        PermissionMode.Full => "full",
        _ => "workspace",
    };

    public static PermissionMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "readonly" or "read_only" or "read-only" => PermissionMode.ReadOnly,
        "full" => PermissionMode.Full,
        _ => PermissionMode.Workspace,
    };

    /// <summary>给模型看的权限说明。</summary>
    public static string Describe(PermissionMode mode, string? workspace) => mode switch
    {
        PermissionMode.ReadOnly =>
            "仅可查看：只能读取文件、查看目录和执行查询类命令，不能创建、修改、删除文件，也不能执行会修改电脑的命令。需要修改时请告诉用户切换权限。",
        PermissionMode.Full =>
            $"完全权限：可以修改工作区以外的文件，普通命令会直接执行。仍然优先把产出文件放在工作区 {workspace} 内。",
        _ =>
            $"工作区内修改：只能在工作区 {workspace} 内创建和修改文件；工作区外的文件只能读取。执行命令前会请用户确认。",
    };
}

/// <summary>
/// 把权限模式、工作区和命令策略结合起来，给出每个操作的最终判定。
/// 判定顺序：管理员禁止规则 → 权限模式 → 工作区范围。
/// </summary>
public static class PermissionRules
{
    // 命令中出现的绝对路径：C:\xxx、D:/xxx、\\server\share\xxx
    private static readonly Regex AbsolutePath = new(
        @"(?<![\w])(?:[A-Za-z]:[\\/][^\s""'|;&<>,()]*|\\\\[^\s""'|;&<>,()\\]+\\[^\s""'|;&<>,()]*)",
        RegexOptions.Compiled);

    /// <summary>写入或删除路径。</summary>
    public static PolicyDecision ForWrite(CommandPolicy policy, PermissionMode mode, string? workspace, string fullPath, bool delete)
    {
        var baseline = policy.EvaluateWrite(fullPath);
        if (baseline.Level == RiskLevel.Blocked)
        {
            return baseline;
        }

        var inWorkspace = IsInWorkspace(fullPath, workspace);
        switch (mode)
        {
            case PermissionMode.ReadOnly:
                return PolicyDecision.Blocked("当前是“仅可查看”权限，不能创建、修改或删除文件。需要修改时，请在输入框下方把权限切换为“工作区内修改”");

            case PermissionMode.Workspace:
                if (!inWorkspace)
                {
                    return PolicyDecision.Blocked(
                        $"当前是“工作区内修改”权限，只能修改工作区 {workspace} 内的文件。请把文件放在工作区内，或让用户切换到“完全权限”");
                }
                return delete
                    ? PolicyDecision.Confirm("删除文件需要确认（会移入回收站）") with { Rememberable = false }
                    : PolicyDecision.Auto("工作区内的文件");

            default: // Full
                if (delete && !inWorkspace)
                {
                    return PolicyDecision.Confirm("删除工作区外的文件需要确认（会移入回收站）") with { Rememberable = false };
                }
                return PolicyDecision.Auto(inWorkspace ? "工作区内的文件" : "完全权限");
        }
    }

    /// <summary>执行 Shell 命令。</summary>
    public static PolicyDecision ForCommand(CommandPolicy policy, PermissionMode mode, string? workspace, string command, string workingDirectory)
    {
        var baseline = policy.EvaluateCommand(command, workingDirectory);
        if (baseline.Level == RiskLevel.Blocked)
        {
            return baseline;
        }
        if (baseline.Level == RiskLevel.Auto)
        {
            return baseline; // 只读查询命令在任何模式下都自动执行
        }

        switch (mode)
        {
            case PermissionMode.ReadOnly:
                return PolicyDecision.Blocked("当前是“仅可查看”权限，只能执行查询类命令。需要执行这条命令时，请让用户切换权限");

            case PermissionMode.Workspace:
            {
                if (!IsInWorkspace(workingDirectory, workspace))
                {
                    return PolicyDecision.Blocked($"当前是“工作区内修改”权限，命令只能在工作区 {workspace} 内执行");
                }
                var outside = OutsidePaths(command, workspace);
                return outside.Count > 0
                    ? PolicyDecision.Confirm($"命令涉及工作区外的路径：{string.Join("、", outside.Take(3))}，请确认")
                    : PolicyDecision.Confirm("执行命令需要确认，确认后同样的命令不再询问");
            }

            default: // Full
                return PolicyDecision.Auto("完全权限");
        }
    }

    /// <summary>运行脚本或可执行文件（open_app 打开脚本时）。</summary>
    public static PolicyDecision ForScript(CommandPolicy policy, PermissionMode mode, string fullPath)
    {
        var script = policy.EvaluateScript(fullPath);
        if (script.Level == RiskLevel.Blocked)
        {
            return script;
        }
        return mode switch
        {
            PermissionMode.ReadOnly => PolicyDecision.Blocked("当前是“仅可查看”权限，不能运行脚本"),
            PermissionMode.Full => PolicyDecision.Auto("完全权限"),
            _ => PolicyDecision.Confirm("运行脚本需要确认"),
        };
    }

    public static bool IsInWorkspace(string fullPath, string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return false;
        }
        try
        {
            return CommandPolicy.IsUnder(Path.GetFullPath(fullPath), Path.GetFullPath(workspace));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>命令里出现的、不在工作区内的绝对路径。</summary>
    public static List<string> OutsidePaths(string command, string? workspace)
    {
        var list = new List<string>();
        foreach (Match m in AbsolutePath.Matches(Environment.ExpandEnvironmentVariables(command)))
        {
            var p = m.Value.TrimEnd('.', '\\', '/');
            if (p.Length < 3)
            {
                continue;
            }
            if (!IsInWorkspace(p, workspace) && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(p);
            }
        }
        return list;
    }
}
