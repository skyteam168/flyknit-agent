using System.Text;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Tools;

/// <summary>
/// 文件通配符（同 Claude Code / ripgrep 的写法）：<c>*</c> 不跨目录，<c>**</c> 跨任意层目录，<c>?</c> 一个字符，<c>{a,b}</c> 任选其一。
/// 不带 <c>/</c> 的（<c>*.cs</c>）只比文件名，在哪一层都算；带 <c>/</c> 的（<c>src/**/*.ts</c>）比相对路径。
/// Windows 文件名不分大小写，这里也不分。
/// </summary>
public sealed class Glob
{
    private readonly Regex _regex;

    public Glob(string pattern)
    {
        Pattern = pattern.Replace('\\', '/').Trim();
        if (Pattern.StartsWith("./", StringComparison.Ordinal))
        {
            Pattern = Pattern[2..];
        }
        MatchesPath = Pattern.Contains('/');
        _regex = new Regex("^" + ToRegex(Pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    public string Pattern { get; }

    /// <summary>带目录（要比相对路径），而不是只比文件名。</summary>
    public bool MatchesPath { get; }

    /// <summary>需要逐层往下找（带 ** 或目录）：.NET 自带的通配符做不到，要自己遍历。</summary>
    public static bool NeedsWalk(string pattern) => pattern.Contains("**", StringComparison.Ordinal) || pattern.Contains('/') || pattern.Contains('\\') || pattern.Contains('{');

    /// <param name="relativePath">相对搜索起点的路径，分隔符随意。</param>
    public bool IsMatch(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        return _regex.IsMatch(MatchesPath ? path : path[(path.LastIndexOf('/') + 1)..]);
    }

    internal static string ToRegex(string glob)
    {
        var sb = new StringBuilder();
        var braces = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // **/ 匹配零层或多层目录；结尾的 ** 匹配剩下的一切
                        if (i + 2 < glob.Length && glob[i + 2] == '/')
                        {
                            sb.Append("(?:.*/)?");
                            i += 2;
                        }
                        else
                        {
                            sb.Append(".*");
                            i += 1;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    braces++;
                    sb.Append("(?:");
                    break;
                case '}' when braces > 0:
                    braces--;
                    sb.Append(')');
                    break;
                case ',' when braces > 0:
                    sb.Append('|');
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        // 没配对的 { 当普通字符太麻烦，直接补上右括号，至少不会抛异常
        sb.Append(')', braces);
        return sb.ToString();
    }
}

/// <summary>代码目录遍历：跳过版本库、依赖、编译产物这类既大又不是源码的目录。</summary>
public static class SourceTree
{
    /// <summary>默认跳过的目录名（任何一层）。</summary>
    public static readonly IReadOnlySet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", ".vs", ".idea", "__pycache__", ".venv", "venv", ".mypy_cache", ".pytest_cache",
        "bin", "obj", "dist", ".next", ".nuxt", "target", ".gradle", "coverage",
    };

    /// <summary>深度优先列出 root 下的文件（相对路径），遇到跳过的目录不进去；没权限的目录跳过不报错。</summary>
    public static IEnumerable<string> Files(string root, IReadOnlySet<string>? skip = null, int maxDepth = 24, CancellationToken ct = default)
    {
        skip ??= SkippedDirectories;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", options).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                dirs = Directory.EnumerateDirectories(dir, "*", options).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files)
            {
                yield return Path.GetRelativePath(root, f);
            }
            if (depth >= maxDepth)
            {
                continue;
            }
            foreach (var d in dirs)
            {
                if (!skip.Contains(Path.GetFileName(d)))
                {
                    stack.Push((d, depth + 1));
                }
            }
        }
    }

    /// <summary>开头 8KB 里有 0 字节就当二进制（图片、压缩包、exe），不去搜它的内容。</summary>
    public static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[8192];
            var n = fs.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, n) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
