using System.Security.Cryptography;
using System.Text.Json;

namespace Flyknit.Agent.Tasks;

/// <summary>
/// 安装软件（install）：从服务端下载安装包，校验 SHA-256，再静默安装。
/// 参数在下发时已由后台定好：package_id、kind（msi/exe）、sha256、filename、silent_args。
/// </summary>
public static class Installer
{
    public static async Task<RunResult> RunAsync(JsonElement p, ServerApi api, CancellationToken ct)
    {
        if (!p.TryGetProperty("package_id", out var idEl) || !idEl.TryGetInt32(out var packageId))
        {
            return RunResult.Fail("任务缺少安装包信息。");
        }
        var kind = Str(p, "kind");
        var sha256 = Str(p, "sha256").ToLowerInvariant();
        var filename = Str(p, "filename");
        var silentArgs = Str(p, "silent_args");
        var name = Str(p, "name");

        Directory.CreateDirectory(AgentPaths.Work);
        var path = Path.Combine(AgentPaths.Work, $"{sha256}.{kind}");

        try
        {
            if (!File.Exists(path) || !await VerifyAsync(path, sha256, ct))
            {
                AgentLog.Info($"下载安装包 {name} (package {packageId})");
                await api.DownloadPackageAsync(packageId, path, ct);
            }
            if (!await VerifyAsync(path, sha256, ct))
            {
                return RunResult.Fail("安装包校验失败（SHA-256 不匹配），已中止，未执行安装。");
            }

            var (file, args) = kind switch
            {
                "msi" => ("msiexec.exe", $"/i \"{path}\" /qn /norestart"),
                _ => (path, silentArgs),
            };
            var r = await ProcessRunner.RunAsync(file, args, ct, TimeSpan.FromMinutes(30));

            // 0 成功；3010/1641 是“成功，但需要重启”；exe 各家约定不一，0 以外一律按失败报回
            if (r.ExitCode is 0 or 3010 or 1641)
            {
                var note = r.ExitCode is 3010 or 1641 ? "（安装完成，建议重启生效）" : "";
                return RunResult.Ok($"{name} 安装成功{note}。\n{r.Combined}", r.ExitCode);
            }
            return RunResult.Fail($"{name} 安装失败，退出码 {r.ExitCode}。\n{r.Combined}", r.ExitCode);
        }
        catch (Exception ex)
        {
            return RunResult.Fail($"安装 {name} 出错：{ex.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }

    private static async Task<bool> VerifyAsync(string path, string expected, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }
        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant() == expected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
