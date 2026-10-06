using System.Text;
using System.Text.Json;

namespace Flyknit.Agent.Tasks;

/// <summary>系统提速（optimize）：清缓存、刷 DNS、优化磁盘（SSD 做 TRIM，机械盘碎片整理）。</summary>
public static class Optimizer
{
    public static async Task<RunResult> RunAsync(JsonElement parameters, CancellationToken ct)
    {
        var log = new StringBuilder();
        var ok = true;

        if (Bool(parameters, "clean", true))
        {
            // 提速默认把常见缓存一起清掉（不含更新缓存，那个重下载代价大）
            foreach (var target in new[] { "windows_temp", "user_temp", "browser_cache", "thumbnails" })
            {
                ct.ThrowIfCancellationRequested();
                Cleaner.CleanOne(target, log);
            }
        }

        if (Bool(parameters, "flush_dns", true))
        {
            var r = await ProcessRunner.RunAsync("ipconfig", "/flushdns", ct, TimeSpan.FromMinutes(1));
            log.AppendLine(r.ExitCode == 0 ? "已刷新 DNS 缓存。" : $"刷新 DNS 失败：{r.Combined}");
            ok &= r.ExitCode == 0;
        }

        if (Bool(parameters, "optimize_disks", true))
        {
            // defrag /C /O：对所有卷按介质类型做合适的优化（SSD -> Retrim，HDD -> 碎片整理）
            var r = await ProcessRunner.RunAsync("defrag.exe", "/C /O /H", ct, TimeSpan.FromHours(2));
            log.AppendLine(r.ExitCode == 0 ? "磁盘优化完成。" : $"磁盘优化返回码 {r.ExitCode}：{Tail(r.Combined)}");
            ok &= r.ExitCode == 0;
        }

        return ok ? RunResult.Ok(log.ToString()) : RunResult.Fail(log.ToString());
    }

    private static bool Bool(JsonElement p, string name, bool fallback)
    {
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            };
        }
        return fallback;
    }

    private static string Tail(string s) => s.Length <= 500 ? s : s[^500..];
}
