namespace Flyknit.Setup;

/// <summary>
/// 安装程序的命令行参数。单独拿出来是为了能测（不碰 Windows）。
///
///   （无参数）           双击安装
///   /S（/quiet）         静默安装
///   /uninstall [/S]      卸载
///   /S /deferred         给旧运维代理「搭桥」：被代理以「安装软件」任务启动时用（docs/agent-self-update.md 第 3 节）。
///                        复制一份自己、启动副本后立即退出，副本等代理把任务回报完再安装。
///                        直接 /S 不行：安装程序一停代理，代理就把它连同子进程一起结束了
///   /S /deferred-run <进程 ID>   上面那份副本：等那个进程退出后静默安装（内部用）
/// </summary>
public sealed record SetupArgs(bool Silent, bool Uninstall, bool Deferred, int? DeferredRunAfter, string? Error)
{
    public const string DeferredFlag = "/deferred";
    public const string DeferredRunFlag = "/deferred-run";

    public static SetupArgs Parse(IReadOnlyList<string> args)
    {
        bool Has(params string[] names) => args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));

        var silent = Has("/S", "/quiet");
        var uninstall = Has("/uninstall");
        var deferred = Has(DeferredFlag);
        int? runAfter = null;
        var i = args.ToList().FindIndex(a => a.Equals(DeferredRunFlag, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            if (i + 1 >= args.Count || !int.TryParse(args[i + 1], out var pid) || pid <= 0)
            {
                return new SetupArgs(silent, uninstall, deferred, null, $"{DeferredRunFlag} 后面要跟进程 ID");
            }
            runAfter = pid;
        }
        if ((deferred || runAfter is not null) && !silent)
        {
            // 搭桥只给运维代理用，代理那边没有人能点对话框
            return new SetupArgs(silent, uninstall, deferred, runAfter, $"{DeferredFlag} 只能和 /S 一起用");
        }
        if ((deferred || runAfter is not null) && uninstall)
        {
            return new SetupArgs(silent, uninstall, deferred, runAfter, $"{DeferredFlag} 不能和 /uninstall 一起用");
        }
        return new SetupArgs(silent, uninstall, deferred, runAfter, null);
    }
}
