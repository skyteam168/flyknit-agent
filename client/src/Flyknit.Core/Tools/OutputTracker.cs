using System.Security.Cryptography;

namespace Flyknit.Core.Tools;

/// <summary>任务产出的一个文件。</summary>
public sealed record OutputFile(string Path, string Name, string Extension, long Size, DateTimeOffset ModifiedAt);

/// <summary>
/// 找出一条命令产出了哪些文件。
///
/// 写文件的工具自己就知道写了什么（<see cref="ToolResult.Outputs"/>），不需要猜；
/// 但 run_shell 跑的可能是任意程序（Python 生成 Excel、构建脚本产出安装包…），
/// 事先无从知道，所以在命令前后各扫一次工作目录，比对出新增和被改动的文件。
///
/// 扫描有硬上限（深度、文件数、耗时），并跳过 node_modules、.git、bin、obj 这类噪声目录，
/// 免得在大仓库上把任务拖慢。超出上限时宁可少报，不会阻塞命令执行。
/// </summary>
public sealed class OutputTracker
{
    public const int MaxDepth = 4;
    public const int MaxEntries = 20000;
    public const int MaxReported = 20;

    /// <summary>这些目录里的变化是构建噪声，不是用户要的产出。</summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "bin", "obj", "__pycache__", ".venv", "venv",
        ".next", ".nuxt", "dist-cache", ".gradle", ".idea", ".vs", "target", ".pytest_cache", ".mypy_cache",
    };

    /// <summary>临时文件和锁文件不算产出。</summary>
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".swp", ".swo", ".lock", ".part", ".crdownload", ".partial",
    };

    private readonly Dictionary<string, (long Size, DateTime Modified)> _before;
    private readonly string _root;

    private OutputTracker(string root, Dictionary<string, (long Size, DateTime Modified)> before)
    {
        _root = root;
        _before = before;
    }

    /// <summary>命令执行前拍一张快照。目录不存在或扫描失败时返回 null，调用方直接跳过比对。</summary>
    public static OutputTracker? Snapshot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }
        var map = Scan(directory);
        return map is null ? null : new OutputTracker(directory, map);
    }

    /// <summary>命令执行后比对，返回新增或被改动的文件（按修改时间倒序，最多 MaxReported 个）。</summary>
    public IReadOnlyList<OutputFile> Changed()
    {
        var after = Scan(_root);
        if (after is null)
        {
            return Array.Empty<OutputFile>();
        }
        var changed = new List<OutputFile>();
        foreach (var (path, now) in after)
        {
            if (_before.TryGetValue(path, out var then) && then.Size == now.Size && then.Modified == now.Modified)
            {
                continue; // 没动过
            }
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }
                changed.Add(Describe(info));
            }
            catch (Exception)
            {
                // 文件刚好被删掉或占用：跳过
            }
        }
        return changed.OrderByDescending(f => f.ModifiedAt).Take(MaxReported).ToList();
    }

    public static OutputFile Describe(FileInfo info) => new(
        info.FullName,
        info.Name,
        info.Extension.TrimStart('.').ToLowerInvariant(),
        info.Length,
        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime());

    /// <summary>把一批路径整理成产出列表：去重、丢掉不存在的和临时文件。</summary>
    public static IReadOnlyList<OutputFile> Describe(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<OutputFile>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || IsNoise(info.Name) || !seen.Add(info.FullName))
                {
                    continue;
                }
                list.Add(Describe(info));
            }
            catch (Exception)
            {
                // 路径非法或没权限：跳过
            }
        }
        return list;
    }

    private static bool IsNoise(string fileName) =>
        SkipExtensions.Contains(Path.GetExtension(fileName)) || fileName.StartsWith('~') || fileName.StartsWith(".~");

    private static Dictionary<string, (long Size, DateTime Modified)>? Scan(string root)
    {
        var map = new Dictionary<string, (long Size, DateTime Modified)>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        try
        {
            while (queue.Count > 0 && map.Count < MaxEntries)
            {
                var (dir, depth) = queue.Dequeue();
                IEnumerable<string> files, dirs;
                try
                {
                    files = Directory.EnumerateFiles(dir);
                    dirs = depth < MaxDepth ? Directory.EnumerateDirectories(dir) : Array.Empty<string>();
                }
                catch (Exception)
                {
                    continue; // 没权限的目录跳过
                }
                foreach (var f in files)
                {
                    if (map.Count >= MaxEntries)
                    {
                        break;
                    }
                    if (IsNoise(Path.GetFileName(f)))
                    {
                        continue;
                    }
                    try
                    {
                        var info = new FileInfo(f);
                        map[info.FullName] = (info.Length, info.LastWriteTimeUtc);
                    }
                    catch (Exception)
                    {
                        // 文件被占用：跳过
                    }
                }
                foreach (var d in dirs)
                {
                    if (!SkipDirectories.Contains(Path.GetFileName(d)))
                    {
                        queue.Enqueue((d, depth + 1));
                    }
                }
            }
            return map;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
