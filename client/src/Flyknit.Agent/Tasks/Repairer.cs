using System.Text;
using System.Text.Json;

namespace Flyknit.Agent.Tasks;

/// <summary>系统修复（repair）：按后台勾选跑 DISM、SFC、网络重置、重置更新组件。</summary>
public static class Repairer
{
    public static async Task<RunResult> RunAsync(JsonElement parameters, CancellationToken ct)
    {
        var actions = ReadActions(parameters);
        var log = new StringBuilder();
        var ok = true;
        var needRestart = false;

        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            switch (action)
            {
                case "dism":
                    ok &= await Step("修复系统映像（DISM）", "DISM.exe",
                        "/Online /Cleanup-Image /RestoreHealth", log, ct, TimeSpan.FromHours(1));
                    break;
                case "sfc":
                    ok &= await Step("检查系统文件（SFC）", "sfc.exe", "/scannow", log, ct, TimeSpan.FromHours(1));
                    break;
                case "network":
                    ok &= await ResetNetwork(log, ct);
                    needRestart = true;
                    break;
                case "windows_update":
                    ok &= await ResetWindowsUpdate(log, ct);
                    break;
            }
        }

        if (needRestart)
        {
            log.AppendLine("网络组件已重置，建议重启电脑使其完全生效。");
        }
        return ok ? RunResult.Ok(log.ToString()) : RunResult.Fail(log.ToString());
    }

    public static List<string> ReadActions(JsonElement parameters)
    {
        var list = new List<string>();
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("actions", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.GetString() is { } s) list.Add(s);
            }
        }
        return list;
    }

    private static async Task<bool> Step(string label, string file, string args, StringBuilder log, CancellationToken ct, TimeSpan timeout)
    {
        var r = await ProcessRunner.RunAsync(file, args, ct, timeout);
        var ok = r.ExitCode == 0;
        log.AppendLine($"{label}：{(ok ? "完成" : $"返回码 {r.ExitCode}")}");
        if (!ok)
        {
            log.AppendLine(Tail(r.Combined));
        }
        return ok;
    }

    private static async Task<bool> ResetNetwork(StringBuilder log, CancellationToken ct)
    {
        var ok = true;
        foreach (var (file, args) in new[]
        {
            ("netsh", "winsock reset"),
            ("netsh", "int ip reset"),
            ("ipconfig", "/flushdns"),
            ("ipconfig", "/release"),
            ("ipconfig", "/renew"),
        })
        {
            var r = await ProcessRunner.RunAsync(file, args, ct, TimeSpan.FromMinutes(2));
            // release/renew 在无 DHCP 时可能非 0，不算致命，记录即可
            ok &= r.ExitCode == 0 || args.StartsWith("/re");
        }
        log.AppendLine("重置网络：已重置 Winsock、TCP/IP 并刷新 DNS。");
        return ok;
    }

    private static async Task<bool> ResetWindowsUpdate(StringBuilder log, CancellationToken ct)
    {
        foreach (var svc in new[] { "wuauserv", "bits", "cryptsvc" })
        {
            await ProcessRunner.RunAsync("net", $"stop {svc}", ct, TimeSpan.FromMinutes(2));
        }
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        TryRename(Path.Combine(win, "SoftwareDistribution"), log);
        TryRename(Path.Combine(win, "System32", "catroot2"), log);
        foreach (var svc in new[] { "cryptsvc", "bits", "wuauserv" })
        {
            await ProcessRunner.RunAsync("net", $"start {svc}", ct, TimeSpan.FromMinutes(2));
        }
        log.AppendLine("重置 Windows 更新：已重建更新缓存目录并重启相关服务。");
        return true;
    }

    private static void TryRename(string dir, StringBuilder log)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                var backup = dir + ".old";
                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                Directory.Move(dir, backup);
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"重命名 {Path.GetFileName(dir)} 失败：{ex.Message}");
        }
    }

    private static string Tail(string s) => s.Length <= 800 ? s : s[^800..];
}
