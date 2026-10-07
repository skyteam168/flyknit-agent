using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Flyknit.Core.Tools;

/// <summary>
/// 覆盖写之前留一份原文件。
///
/// 删除有回收站兜底，覆盖写没有——<c>write_file</c> 是目前唯一一个**完全不可逆**
/// 的操作。放宽删除之前先把这个补上，两件事是配套的。
///
/// 按日期分目录存，超过总容量上限时从最旧的开始删。
/// </summary>
public sealed class FileBackup
{
    /// <summary>备份总量上限。到顶之后删最旧的，不会无限占盘。</summary>
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    /// <summary>单个文件超过这个大小就不备份了——备份它的代价大于收益。</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    private readonly string _root;
    private readonly long _maxBytes;
    private readonly Action<string>? _warn;

    /// <param name="warn">记日志的回调。Core 不依赖 Client 的 Log，由宿主传进来。</param>
    public FileBackup(string root, long maxBytes = DefaultMaxBytes, Action<string>? warn = null)
    {
        _root = root;
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        _warn = warn;
    }

    /// <summary>
    /// 备份即将被覆盖的文件。返回备份路径；文件不存在、太大或备份失败时返回 null。
    /// </summary>
    public string? Capture(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                return null; // 新建文件没什么可备份的
            }
            if (info.Length > MaxFileBytes)
            {
                _warn?.Invoke($"文件超过 {MaxFileBytes / 1024 / 1024} MB，跳过备份：{path}");
                return null;
            }

            var day = Path.Combine(_root, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(day);

            var target = Path.Combine(day, UniqueName(path));
            File.Copy(path, target, overwrite: false);
            Trim();
            return target;
        }
        catch (Exception ex)
        {
            // 备份失败不能挡住用户要做的事，但要留下痕迹
            _warn?.Invoke($"备份 {path} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 备份文件名：时间 + 原名 + 原路径的短哈希。
    /// 带哈希是因为不同目录下同名文件很常见（到处都有 README.md），
    /// 只用文件名会互相覆盖，恢复时也认不出是哪个。
    /// </summary>
    private static string UniqueName(string path)
    {
        var name = Path.GetFileName(path);
        var stamp = DateTime.Now.ToString("HHmmss");
        var tag = Math.Abs(StableHash(Path.GetFullPath(path))).ToString("x8");
        return $"{stamp}-{tag}-{name}";
    }

    /// <summary>进程间稳定的哈希。string.GetHashCode 每次启动都不一样，不能用。</summary>
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in text.ToLowerInvariant())
            {
                hash = hash * 31 + c;
            }
            return hash;
        }
    }

    /// <summary>超出上限时从最旧的开始删。</summary>
    public long Trim()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return 0;
            }
            var files = new DirectoryInfo(_root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= _maxBytes)
                {
                    break;
                }
                var size = file.Length;
                try
                {
                    file.Delete();
                    total -= size;
                }
                catch (IOException)
                {
                    // 被占用就跳过，下次再说
                }
            }
            RemoveEmptyDays();
            return total;
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"清理备份失败：{ex.Message}");
            return 0;
        }
    }

    private void RemoveEmptyDays()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (IOException) { /* 下次再清 */ }
        }
    }

    /// <summary>当前占用的字节数，设置界面上要显示。</summary>
    public long UsedBytes()
    {
        try
        {
            return Directory.Exists(_root)
                ? new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>某个原始文件的历次备份，最新的在前。给「恢复」用。</summary>
    public IReadOnlyList<string> VersionsOf(string path)
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return Array.Empty<string>();
            }
            var tag = Math.Abs(StableHash(Path.GetFullPath(path))).ToString("x8");
            return new DirectoryInfo(_root)
                .EnumerateFiles($"*-{tag}-*", SearchOption.AllDirectories)
                // 按备份的时间排（日期目录 + 文件名开头的时分秒），不能按修改时间：File.Copy 保留的是原文件的修改时间，
                // 两次改动挨得很近时分不出先后
                .OrderByDescending(f => f.Directory?.Name, StringComparer.Ordinal)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
