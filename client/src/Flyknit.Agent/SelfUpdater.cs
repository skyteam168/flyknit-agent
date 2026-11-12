using System.Diagnostics;

namespace Flyknit.Agent;

/// <summary>
/// 运维代理自升级（docs/agent-self-update.md）。
///
/// 新版本从哪来：员工端目录里的 agent\FlyknitAgent.exe。这个目录在 Program Files 下、只有 SYSTEM 和管理员能写，
/// 里面的文件只来自安装程序或代理自己下载并校验过 SHA-256 的员工端安装包（ClientUpdater）——和代理替员工端升级是同一条信任链。
///
/// 正在运行的 exe 不能覆盖，所以分两段：
///   1. 代理（当前版本）把新版本复制到 update\，再把自己复制成 update\FlyknitAgentUpdate.exe，
///      以 --complete-update 启动这个更新助手，然后正常停止服务；
///   2. 更新助手等代理进程退出 → 旧 exe 改名为 .old、新 exe 放上去 → 启动服务 → 等新代理连上服务器写健康标记；
///      3 分钟内没等到就换回 .old 并把这个版本记进 skip，以后不再试。
/// 更新助手用的是旧版本（已知能跑）：新版本要是坏的，拿它来回滚也会失败。
///
/// 所有中转文件都在代理自己的程序目录（Program Files\FlyknitAgent）里，员工账号建不了、改不了，不用 ProgramData 或 Temp。
/// </summary>
public sealed class SelfUpdater(AgentConfig config)
{
    public const string ServiceName = "FlyknitAgent";
    public const string HelperFlag = "--complete-update";

    /// <summary>代理的程序目录。更新助手本身在 update\ 子目录里跑，要往上一层。</summary>
    private static string AgentDir
    {
        get
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Path.GetFileName(dir), "update", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(dir)!
                : dir;
        }
    }
    private static string UpdateDir => Path.Combine(AgentDir, "update");
    private static string AgentExe => Path.Combine(AgentDir, "FlyknitAgent.exe");
    private static string OldExe => AgentExe + ".old";
    private static string StagedExe => Path.Combine(UpdateDir, "FlyknitAgent.exe");
    private static string HelperExe => Path.Combine(UpdateDir, "FlyknitAgentUpdate.exe");
    private static string HealthyMarker => Path.Combine(UpdateDir, "healthy");
    /// <summary>回滚记录，每次一行版本号。</summary>
    private static string SkipFile => Path.Combine(UpdateDir, "skip.txt");
    /// <summary>最近一次失败（已回滚）的版本，领任务时报给服务端，后台设备详情里显示。</summary>
    private static string FailedFile => Path.Combine(UpdateDir, "failed.txt");

    private string ClientDir => config.EffectiveClientDir;

    /// <summary>员工端目录归代理管（安装程序写了标记）才从那里取新版本；免安装版、手工拷的目录不认。</summary>
    private string? CandidateExe
    {
        get
        {
            if (!File.Exists(Path.Combine(ClientDir, ClientUpdater.ManagedMarker)))
            {
                return null;
            }
            var exe = Path.Combine(ClientDir, "agent", "FlyknitAgent.exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    /// <summary>员工端目录里有没有比自己新、而且没失败过的代理。</summary>
    public string? AvailableVersion()
    {
        try
        {
            if (CandidateExe is not { } exe)
            {
                return null;
            }
            var version = SelfUpdatePolicy.CleanVersion(FileVersionInfo.GetVersionInfo(exe).ProductVersion);
            return SelfUpdatePolicy.ShouldUpdate(ServerApi.Version, version, SelfUpdatePolicy.GivenUp(Failures())) ? version : null;
        }
        catch (Exception ex)
        {
            AgentLog.Warn("读取员工端目录里的代理版本失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 开始换：复制新版本和更新助手，启动助手。返回 true 表示调用方应该马上停止服务（助手会再把它启动起来）。
    /// </summary>
    public bool Begin(string version)
    {
        try
        {
            Directory.CreateDirectory(UpdateDir);
            File.Copy(CandidateExe!, StagedExe, overwrite: true);
            // 再核对一次：复制下来的确实是这个版本
            var staged = SelfUpdatePolicy.CleanVersion(FileVersionInfo.GetVersionInfo(StagedExe).ProductVersion);
            if (staged != version)
            {
                AgentLog.Warn($"复制下来的代理版本是 {staged}，不是 {version}，这次不换");
                File.Delete(StagedExe);
                return false;
            }
            File.Copy(Environment.ProcessPath ?? AgentExe, HelperExe, overwrite: true);
            TryDelete(HealthyMarker);
            // ShellExecute 启动：不继承代理的句柄，代理停掉后助手照常跑
            Process.Start(new ProcessStartInfo(HelperExe, $"{HelperFlag} {Environment.ProcessId} {version}")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = UpdateDir,
            });
            AgentLog.Info($"运维代理开始自升级 {ServerApi.Version} → {version}，服务即将重启");
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"运维代理自升级没能开始（{version}），继续用 {ServerApi.Version}", ex);
            return false;
        }
    }

    /// <summary>新代理第一次连上服务器（注册或领任务成功）时调用：告诉更新助手「这个版本能用」。</summary>
    public static void MarkHealthy()
    {
        try
        {
            if (Directory.Exists(UpdateDir) && !File.Exists(HealthyMarker))
            {
                File.WriteAllText(HealthyMarker, ServerApi.Version);
            }
        }
        catch (Exception)
        {
            // 写不了的话助手会超时回滚：宁可停在旧版本
        }
    }

    /// <summary>最近一次失败（已回滚）的版本，没有就是空串。</summary>
    public static string FailedVersion()
    {
        try
        {
            return File.Exists(FailedFile) ? File.ReadAllText(FailedFile).Trim() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>每次回滚记的一行版本号（同一个版本失败够次数就不再试，见 SelfUpdatePolicy.MaxAttempts）。</summary>
    private static List<string> Failures()
    {
        try
        {
            return File.Exists(SkipFile)
                ? File.ReadAllLines(SkipFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                : new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    // =====================================================================
    // 更新助手（--complete-update <原进程 ID> <新版本>），以旧版本的身份运行
    // =====================================================================

    public static int CompleteUpdate(string[] args)
    {
        var i = Array.IndexOf(args, HelperFlag);
        if (i < 0 || i + 2 >= args.Length || !int.TryParse(args[i + 1], out var pid))
        {
            AgentLog.Error("更新助手参数不对：" + string.Join(' ', args));
            return 2;
        }
        var version = args[i + 2];
        AgentLog.Info($"更新助手启动：等代理（进程 {pid}）退出，换成 {version}");
        try
        {
            if (!WaitForExit(pid))
            {
                AgentLog.Error("代理进程没有按时退出，放弃这次升级");
                StartService();
                return 1;
            }
            if (!File.Exists(StagedExe))
            {
                AgentLog.Error("找不到要换上的新版本，放弃这次升级");
                StartService();
                return 1;
            }
            TryDelete(HealthyMarker);
            File.Move(AgentExe, OldExe, overwrite: true);
            try
            {
                File.Move(StagedExe, AgentExe);
            }
            catch (Exception)
            {
                File.Move(OldExe, AgentExe, overwrite: true);
                throw;
            }
            StartService();

            var watch = Stopwatch.StartNew();
            while (true)
            {
                switch (SelfUpdatePolicy.CheckHealth(File.Exists(HealthyMarker), watch.Elapsed))
                {
                    case SelfUpdatePolicy.HealthOutcome.Healthy:
                        TryDelete(OldExe);
                        TryDelete(FailedFile);
                        AgentLog.Info($"运维代理已升级到 {version}");
                        return 0;
                    case SelfUpdatePolicy.HealthOutcome.RollBack:
                        RollBack(version);
                        return 1;
                }
                Thread.Sleep(2000);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error($"运维代理升级到 {version} 出错", ex);
            if (!File.Exists(AgentExe) && File.Exists(OldExe))
            {
                TryMove(OldExe, AgentExe);
            }
            StartService();
            return 1;
        }
    }

    private static void RollBack(string version)
    {
        AgentLog.Error($"新代理 {version} 在 {SelfUpdatePolicy.HealthTimeout.TotalMinutes:0} 分钟内没连上服务器，换回原版本");
        StopService();
        WaitForExitByName("FlyknitAgent");
        if (File.Exists(OldExe))
        {
            TryMove(OldExe, AgentExe);
        }
        try
        {
            File.AppendAllText(SkipFile, version + Environment.NewLine);
            File.WriteAllText(FailedFile, version);
        }
        catch (Exception ex)
        {
            AgentLog.Warn("记录失败版本出错", ex);
        }
        StartService();
    }

    private static bool WaitForExit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit((int)SelfUpdatePolicy.ExitTimeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true; // 已经退出了
        }
    }

    private static void WaitForExitByName(string name)
    {
        var deadline = DateTime.UtcNow + SelfUpdatePolicy.ExitTimeout;
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName(name).Length > 0)
        {
            Thread.Sleep(500);
        }
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try { p.Kill(); } catch (Exception) { }
            }
        }
    }

    private static void StartService() => Sc($"start {ServiceName}");

    private static void StopService() => Sc($"stop {ServiceName}");

    private static void Sc(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit(30_000);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"sc {args} 失败", ex);
        }
    }

    private static void TryMove(string from, string to)
    {
        try { File.Move(from, to, overwrite: true); }
        catch (Exception ex) { AgentLog.Error($"把 {from} 换回 {to} 失败", ex); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { }
    }
}
