using System.Text;
using System.Text.Json;

namespace Flyknit.Agent.Tasks;

/// <summary>清理缓存（clean）：按后台勾选的目标删临时文件、回收站、缓存等。</summary>
public static class Cleaner
{
    public static Task<RunResult> RunAsync(JsonElement parameters, CancellationToken ct)
    {
        var targets = ReadTargets(parameters);
        var log = new StringBuilder();
        long freed = 0;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            freed += CleanOne(target, log);
        }
        log.AppendLine($"共清理约 {Mb(freed)}。");
        return Task.FromResult(RunResult.Ok(log.ToString()));
    }

    public static List<string> ReadTargets(JsonElement parameters)
    {
        var list = new List<string>();
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("targets", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.GetString() is { } s) list.Add(s);
            }
        }
        return list;
    }

    /// <summary>返回这一项释放的字节数（粗略统计）。</summary>
    public static long CleanOne(string target, StringBuilder log)
    {
        try
        {
            return target switch
            {
                "windows_temp" => PurgeDir(Path.Combine(WinDir, "Temp"), log, "系统临时文件"),
                "user_temp" => PurgeAllUsersTemp(log),
                "browser_cache" => PurgeBrowserCaches(log),
                "thumbnails" => PurgeThumbnails(log),
                "update_cache" => PurgeUpdateCache(log),
                "recycle_bin" => EmptyRecycleBin(log),
                _ => 0,
            };
        }
        catch (Exception ex)
        {
            log.AppendLine($"清理 {target} 出错：{ex.Message}");
            return 0;
        }
    }

    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>删掉目录里的内容，但保留目录本身；正被占用的文件跳过。</summary>
    private static long PurgeDir(string dir, StringBuilder log, string label)
    {
        if (!Directory.Exists(dir))
        {
            return 0;
        }
        long freed = 0;
        int skipped = 0;
        foreach (var file in SafeEnumerate(dir))
        {
            try
            {
                var size = new FileInfo(file).Length;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                freed += size;
            }
            catch (Exception) { skipped++; }
        }
        foreach (var sub in SafeEnumerateDirs(dir))
        {
            try { Directory.Delete(sub, recursive: true); }
            catch (Exception) { skipped++; }
        }
        log.AppendLine($"{label}：释放约 {Mb(freed)}" + (skipped > 0 ? $"（{skipped} 项占用中已跳过）" : ""));
        return freed;
    }

    private static long PurgeAllUsersTemp(StringBuilder log)
    {
        long freed = 0;
        var usersRoot = Path.Combine(Path.GetPathRoot(WinDir) ?? "C:\\", "Users");
        if (!Directory.Exists(usersRoot))
        {
            return 0;
        }
        foreach (var profile in SafeEnumerateDirs(usersRoot))
        {
            var temp = Path.Combine(profile, "AppData", "Local", "Temp");
            if (Directory.Exists(temp))
            {
                freed += PurgeDir(temp, log, $"{Path.GetFileName(profile)} 的临时文件");
            }
        }
        return freed;
    }

    private static long PurgeBrowserCaches(StringBuilder log)
    {
        long freed = 0;
        var usersRoot = Path.Combine(Path.GetPathRoot(WinDir) ?? "C:\\", "Users");
        var caches = new[]
        {
            @"AppData\Local\Google\Chrome\User Data\Default\Cache",
            @"AppData\Local\Microsoft\Edge\User Data\Default\Cache",
            @"AppData\Local\Mozilla\Firefox\Profiles",
        };
        foreach (var profile in SafeEnumerateDirs(usersRoot))
        {
            foreach (var rel in caches)
            {
                var dir = Path.Combine(profile, rel);
                if (Directory.Exists(dir))
                {
                    freed += PurgeDir(dir, log, $"{Path.GetFileName(profile)} 浏览器缓存");
                }
            }
        }
        return freed;
    }

    private static long PurgeThumbnails(StringBuilder log)
    {
        long freed = 0;
        var usersRoot = Path.Combine(Path.GetPathRoot(WinDir) ?? "C:\\", "Users");
        foreach (var profile in SafeEnumerateDirs(usersRoot))
        {
            var explorer = Path.Combine(profile, @"AppData\Local\Microsoft\Windows\Explorer");
            if (!Directory.Exists(explorer)) continue;
            foreach (var file in SafeEnumerate(explorer).Where(f => Path.GetFileName(f).StartsWith("thumbcache", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += size;
                }
                catch (Exception) { }
            }
        }
        log.AppendLine($"缩略图缓存：释放约 {Mb(freed)}");
        return freed;
    }

    private static long PurgeUpdateCache(StringBuilder log)
    {
        var dir = Path.Combine(WinDir, "SoftwareDistribution", "Download");
        return PurgeDir(dir, log, "Windows 更新缓存");
    }

    private static long EmptyRecycleBin(StringBuilder log)
    {
        var result = SHEmptyRecycleBin(IntPtr.Zero, null, RecycleFlags.NoConfirmation | RecycleFlags.NoProgressUI | RecycleFlags.NoSound);
        log.AppendLine(result == 0 ? "回收站：已清空" : $"回收站：清空返回码 {result}");
        return 0;
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateFiles(dir); }
        catch (Exception) { return Enumerable.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch (Exception) { return Enumerable.Empty<string>(); }
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024:F1} MB";

    [Flags]
    private enum RecycleFlags : uint
    {
        NoConfirmation = 0x1,
        NoProgressUI = 0x2,
        NoSound = 0x4,
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, RecycleFlags flags);
}
