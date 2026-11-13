using Flyknit.Core.Mcp;
namespace Flyknit.Core.Tools;

/// <summary>
/// 工具类别：给「按类别关掉一批工具」用（团队设置、派遣约束等，见 <see cref="Experts.Delegation.DispatchConstraints"/>）。
/// 只用于收紧：关掉的类别从工具表里去掉，原来没有的不会因此多出来。
/// </summary>
public static class ToolKinds
{
    public const string Shell = "shell";
    public const string Write = "write";
    public const string Browser = "browser";
    public const string Mcp = "mcp";
    public const string Developer = "developer";

    /// <summary>认识的全部类别（外部传进来的只收这些，和服务端一致）。</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { Shell, Write, Browser, Mcp, Developer };

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
