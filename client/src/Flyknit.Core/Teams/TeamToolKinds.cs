using Flyknit.Core.Mcp;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Teams;

/// <summary>
/// 团队能关掉的工具类别（docs/team-mode.md 第 5 节）。只能收紧：关掉的类别从成员专家的工具里去掉，
/// 专家原来就没有的不会因此多出来。
/// </summary>
public static class TeamToolKinds
{
    public const string Shell = "shell";
    public const string Write = "write";
    public const string Browser = "browser";
    public const string Mcp = "mcp";
    public const string Developer = "developer";

    private static readonly HashSet<string> ShellTools = new(StringComparer.Ordinal) { "run_shell", "shell_output", "shell_stop", "open_app" };
    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal) { "write_file", "edit_file", "multi_edit", "delete_path" };
    private static readonly HashSet<string> DeveloperTools = new(StringComparer.Ordinal)
    {
        "grep", "multi_edit", "create_pull_request", "pr_status", "watch_pull_request",
    };

    /// <summary>这个工具属不属于这个类别。</summary>
    public static bool Matches(string kind, ITool tool) => kind switch
    {
        Shell => ShellTools.Contains(tool.Name),
        Write => WriteTools.Contains(tool.Name),
        Browser => tool.Name.StartsWith("browser_", StringComparison.Ordinal),
        Mcp => tool is McpTool || tool.Name.StartsWith(McpNames.Prefix, StringComparison.Ordinal),
        Developer => DeveloperTools.Contains(tool.Name) || tool.Name.StartsWith("git_", StringComparison.Ordinal),
        _ => false,
    };
}
