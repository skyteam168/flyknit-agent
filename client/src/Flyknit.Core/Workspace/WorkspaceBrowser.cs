namespace Flyknit.Core.Workspace;

/// <summary>
/// 右侧「文件」标签：浏览、搜索当前对话的工作区。只读，而且只在工作区里：
/// 传进来的相对路径解析后必须还在工作区目录下（防 ..\ 和绝对路径跳出去），点开文件还是走现有的预览、打开。
/// </summary>
public static class WorkspaceBrowser
{
    /// <summary>一个文件夹最多列这么多项（再多就是 node_modules 之类，列出来也没法看）。</summary>
    public const int MaxEntries = 500;

    /// <summary>搜索最多返回这么多个、最多看这么多项、最深几层：工作区可能是整个 D 盘。</summary>
    public const int MaxResults = 200;
    public const int MaxScanned = 20_000;
    public const int MaxDepth = 8;

    /// <summary>版本库、依赖、编译输出：列表里照样显示（标成灰色），搜索时不进去。</summary>
    public static readonly IReadOnlySet<string> NoiseDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".vs", ".idea", "node_modules", "bin", "obj", "__pycache__", ".venv", "venv", "dist", "build",
    };

    /// <summary>
    /// 工作区里的相对路径 → 完整路径。跳出工作区（..\、别的盘、UNC）返回 null。空字符串是工作区本身。
    /// </summary>
    public static string? Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }
        try
        {
            var full = Path.GetFullPath(root);
            var rel = (relative ?? "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(rel))
            {
                return null;
            }
            var target = Path.GetFullPath(Path.Combine(full, rel));
            var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
            return string.Equals(target.TrimEnd(Path.DirectorySeparatorChar), full.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? target
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>列一个文件夹：文件夹在前，再按名字排。</summary>
    public static WorkspaceListing List(string root, string relative)
    {
        var dir = Resolve(root, relative);
        if (dir is null || !Directory.Exists(dir))
        {
            return new WorkspaceListing(Array.Empty<WorkspaceEntry>(), false, Exists: false);
        }
        var rootFull = Path.GetFullPath(root);
        var entries = new List<WorkspaceEntry>();
        var truncated = false;
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
            {
                if (info.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }
                entries.Add(Entry(rootFull, info));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkspaceListing(Array.Empty<WorkspaceEntry>(), false, Exists: true, Error: ex.Message);
        }
        var sorted = entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new WorkspaceListing(sorted, truncated, Exists: true);
    }

    /// <summary>按文件名找（包含关键字，不分大小写），从浅到深；不进依赖、版本库这类目录。</summary>
    public static WorkspaceSearch Search(string root, string query)
    {
        var rootFull = Resolve(root, "");
        query = (query ?? "").Trim();
        if (rootFull is null || !Directory.Exists(rootFull) || query.Length == 0)
        {
            return new WorkspaceSearch(Array.Empty<WorkspaceEntry>(), false);
        }
        var results = new List<WorkspaceEntry>();
        var scanned = 0;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((rootFull, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> items;
            try
            {
                items = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var info in items)
            {
                if (++scanned > MaxScanned || results.Count >= MaxResults)
                {
                    return new WorkspaceSearch(results, true);
                }
                var isDir = info is DirectoryInfo;
                if (info.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    results.Add(Entry(rootFull, info));
                }
                if (isDir && depth + 1 < MaxDepth && !NoiseDirectories.Contains(info.Name) && !info.Name.StartsWith('.'))
                {
                    queue.Enqueue((info.FullName, depth + 1));
                }
            }
        }
        return new WorkspaceSearch(results, false);
    }

    private static WorkspaceEntry Entry(string rootFull, FileSystemInfo info)
    {
        var isDir = info is DirectoryInfo;
        return new WorkspaceEntry(
            info.Name,
            Path.GetRelativePath(rootFull, info.FullName),
            info.FullName,
            isDir,
            isDir ? 0 : ((FileInfo)info).Length,
            info.LastWriteTime,
            Noise: isDir && NoiseDirectories.Contains(info.Name));
    }
}

/// <param name="Path">工作区里的相对路径（展开文件夹时传回来）。</param>
/// <param name="FullPath">完整路径（预览、打开用）。</param>
/// <param name="Noise">依赖、编译输出这类文件夹：界面上标灰。</param>
public sealed record WorkspaceEntry(string Name, string Path, string FullPath, bool IsDirectory, long Size, DateTime ModifiedAt, bool Noise = false);

public sealed record WorkspaceListing(IReadOnlyList<WorkspaceEntry> Entries, bool Truncated, bool Exists, string Error = "");

public sealed record WorkspaceSearch(IReadOnlyList<WorkspaceEntry> Results, bool Truncated);
