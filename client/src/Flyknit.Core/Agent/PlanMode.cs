using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Agent;

/// <summary>
/// 计划模式（同 Claude Code 的 plan mode）：大任务先只读调研、给出分步方案，员工点「按方案执行」后才真正动手。
/// 计划模式下权限压到「仅可查看」，并去掉会改东西的工具：写文件、改文件、删除、浏览器里点按输入，
/// 以及没有声明只读的外部服务工具。命令仍可执行，但「仅可查看」只放行查询类命令。
/// </summary>
public static class PlanMode
{
    /// <summary>计划模式下去掉的内置工具。</summary>
    public static readonly IReadOnlySet<string> WriteTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "write_file", "edit_file", "multi_edit", "delete_path", "open_app", "git_commit",
        "browser_click", "browser_type", "browser_select",
    };

    public static bool Allows(ITool tool) => tool switch
    {
        McpTool mcp => mcp.Info.ReadOnly && !mcp.Info.Destructive,
        _ => !WriteTools.Contains(tool.Name),
    };

    public static ToolRegistry Filter(ToolRegistry all)
    {
        var filtered = new ToolRegistry();
        foreach (var tool in all.All.Where(Allows))
        {
            filtered.Add(tool);
        }
        return filtered;
    }

    public static PermissionMode Cap(PermissionMode chosen) => PermissionMode.ReadOnly < chosen ? PermissionMode.ReadOnly : chosen;

    public const string RehearsalGuide = """

        <预演>
        这是一次预演：任务照常理解和调研，但不做任何修改——会改东西的工具这一轮用不了。
        - 可以读文件、查目录、执行查询类命令、查外部服务，把真正执行时要用的数据都看一遍。
        - 用 update_plan 列出真正执行时会做的每一步，每一步写清：做什么、动哪个文件或哪条数据、改成什么样。
        - 最后用几句话总结：预计会改动哪些东西、有没有拿不准或有风险的地方。不要假装已经做了。
        </预演>
        """;

    public const string Guide = """

        <计划模式>
        员工打开了「先出方案」：这一轮只调研、出方案，不做任何修改。
        - 可以读文件、查目录、搜索、执行查询类命令、查外部服务，把情况摸清楚；会改东西的工具这一轮用不了。
        - 用 update_plan 列出分步方案（每一步写清做什么、动哪些文件或数据），回复里再用几句话说明：要改什么、为什么、有什么风险、需要员工决定什么。
        - 不要假装已经做了；员工点「按方案执行」后才会真正动手。员工提了修改意见就调整方案，再给一版。
        </计划模式>
        """;
}
