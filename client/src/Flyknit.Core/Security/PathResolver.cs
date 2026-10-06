using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>一条路径解析出来的结果。</summary>
public sealed record ResolvedPath(string Raw, string? Full, PathCertainty Certainty)
{
    public bool Known => Certainty == PathCertainty.Resolved;
}

public enum PathCertainty
{
    /// <summary>解析出了确定的完整路径。</summary>
    Resolved,

    /// <summary>认得出是条路径，但值要到运行时才知道（变量、通配符、命令替换）。</summary>
    Unresolvable,
}

/// <summary>
/// 把命令里出现的路径解析成真实的完整路径。
///
/// 这是「按目标判断危险命令」的地基：一旦不再靠动词硬拦，路径解析准不准就成了
/// 唯一的防线。原来的实现有三个洞，每个都能绕过工作区限制：
///
///   1. 只认 C:\ 和 UNC 开头的绝对路径，<c>..\..\secret</c> 这种相对路径扫不到；
///   2. 比较靠字符串前缀，工作区里放一个指向 C:\ 的 junction 就出去了；
///   3. 环境变量只在扫描前整体展开一次，<c>%TEMP%\..\..</c> 展开后还要再规范化。
///
/// 解析不出来的（变量、通配符、反引号）一律标成 Unresolvable，调用方当作不安全处理：
/// 证不了它安全，就不能放行。
/// </summary>
public static class PathResolver
{
    /// <summary>命令里像路径的片段：绝对路径、UNC、相对路径、~ 开头。</summary>
    private static readonly Regex Candidate = new(
        @"(?<![\w:])(?:
              [A-Za-z]:[\\/][^\s""'|;&<>()]*        # C:\foo
            | \\\\[^\s""'|;&<>()\\]+\\[^\s""'|;&<>()]*  # \\server\share\foo
            | ~[\\/][^\s""'|;&<>()]*                # ~/foo
            | \.{1,2}[\\/][^\s""'|;&<>()]*          # .\foo 与 ..\foo
            | [^\s""'|;&<>()/\\]+[\\/][^\s""'|;&<>()]*  # foo\bar 这种相对路径
            | [\w.\-]+\.[A-Za-z0-9]{1,8}                  # report.txt 这种裸文件名
          )",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

    /// <summary>值要到运行时才知道的写法。命中任何一个就不该当作已知路径。</summary>
    private static readonly Regex Dynamic = new(
        @"[*?]|\$\(|\$\{|\$[A-Za-z_]|%[A-Za-z_][A-Za-z0-9_()]*%|`|\{[^}]*\}",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析一条路径。<paramref name="workingDirectory"/> 是相对路径的基准。
    /// </summary>
    public static ResolvedPath Resolve(string raw, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ResolvedPath(raw ?? "", null, PathCertainty.Unresolvable);
        }

        var text = raw.Trim().Trim('"', '\'');

        // 环境变量先展开，展开不了的（拼写错、没定义）会原样留着 %NAME%，
        // 下面的 Dynamic 检查会把它挡住，不会当成一个叫「%NAME%」的目录
        var expanded = SafeExpand(text);
        if (Dynamic.IsMatch(expanded))
        {
            return new ResolvedPath(raw, null, PathCertainty.Unresolvable);
        }

        if (expanded.StartsWith("~", StringComparison.Ordinal))
        {
            var home = SafeFolder(Environment.SpecialFolder.UserProfile);
            expanded = home.Length > 0 ? home + expanded[1..] : expanded;
        }

        // Windows 风格的绝对路径（C:\... 或 \\server\share）在非 Windows 上
        // Path.IsPathRooted 认不出来，GetFullPath 还会把它拼到当前目录后面。
        // 判定逻辑不该跟着分析器跑在哪个系统上变，所以这类单独按文本处理。
        if (IsWindowsRooted(expanded) && !OperatingSystem.IsWindows())
        {
            return new ResolvedPath(raw, FoldWindows(expanded), PathCertainty.Resolved);
        }

        try
        {
            // 相对路径按命令的工作目录解析，这正是原来漏掉的那一类
            var combined = IsRooted(expanded)
                ? expanded
                : Path.Combine(workingDirectory ?? Directory.GetCurrentDirectory(), expanded);

            // GetFullPath 会把 .. 和 . 折叠掉，所以 D:\ws\..\..\Windows 会变成真实位置
            var full = Path.GetFullPath(combined);
            return new ResolvedPath(raw, RealPath(full), PathCertainty.Resolved);
        }
        catch (Exception)
        {
            // 路径里有非法字符、太长、盘符不存在……都当作不确定
            return new ResolvedPath(raw, null, PathCertainty.Unresolvable);
        }
    }

    /// <summary>
    /// 跟到链接真正指向的地方。
    ///
    /// 工作区里放一个指向 C:\ 的 junction，不跟链接的话字符串比较会认为它在工作区内。
    /// 逐级往上找最近一个存在的父目录来解析，这样目标还不存在（比如要新建的文件）
    /// 也能把路径中间的链接跟过去。
    /// </summary>
    public static string RealPath(string fullPath) => RealPath(fullPath, 0);

    private static string RealPath(string path, int depth)
    {
        // 链接套链接有可能成环，限制层数
        if (depth > 40 || string.IsNullOrEmpty(path))
        {
            return path;
        }
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent) || parent == path)
            {
                return LinkTargetOf(path); // 到盘符根了
            }

            // 先把父目录解析干净，再拼回这一段——**每一级都要解析**。
            // 只解析最后一段是不够的：workspace\shortcut\payroll.txt 里
            // 是中间的 shortcut 指向外面，payroll.txt 自己并不是链接。
            var realParent = RealPath(parent, depth + 1);
            var combined = Path.Combine(realParent, Path.GetFileName(path));
            return LinkTargetOf(combined);
        }
        catch (Exception)
        {
            // 权限不足、路径太长：退回未解析的原值，调用方仍会做范围判断
            return path;
        }
    }

    /// <summary>这一段本身要是链接，就换成它指向的地方；不是链接就原样返回。</summary>
    private static string LinkTargetOf(string path)
    {
        try
        {
            FileSystemInfo? info = Directory.Exists(path) ? new DirectoryInfo(path)
                : File.Exists(path) ? new FileInfo(path)
                : null;
            if (info is null)
            {
                return path; // 还不存在（比如要新建的文件），没有链接可跟
            }
            // ResolveLinkTarget 在 .NET 6+ 对 junction、符号链接都有效
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// 从一条命令里找出所有像路径的片段并解析。
    /// </summary>
    public static List<ResolvedPath> FromCommand(string command, string? workingDirectory)
    {
        var found = new List<ResolvedPath>();
        if (string.IsNullOrWhiteSpace(command))
        {
            return found;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Candidate.Matches(command))
        {
            var raw = m.Value.TrimEnd('.', ',', ';');
            if (raw.Length < 2 || !seen.Add(raw))
            {
                continue;
            }
            // 命令里的开关（-rf、/s）和 URL 不是路径
            if (raw.StartsWith("-", StringComparison.Ordinal) || raw.Contains("://", StringComparison.Ordinal))
            {
                continue;
            }
            found.Add(Resolve(raw, workingDirectory));
        }
        return found;
    }

    /// <summary>
    /// 一条路径是否在某个根目录下。两边都解析过链接，所以 junction 绕不过去。
    /// </summary>
    public static bool IsUnder(string fullPath, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }
        try
        {
            // 任一边是 Windows 路径而当前不是 Windows：按文本比，别让 GetFullPath 把它拼歪
            if (!OperatingSystem.IsWindows() && (IsWindowsRooted(fullPath) || IsWindowsRooted(root)))
            {
                var x = FoldWindows(fullPath).TrimEnd('\\');
                var y = FoldWindows(root).TrimEnd('\\');
                return x.Equals(y, StringComparison.OrdinalIgnoreCase)
                       || x.StartsWith(y + "\\", StringComparison.OrdinalIgnoreCase);
            }
            var a = Normalize(RealPath(Path.GetFullPath(fullPath)));
            var b = Normalize(RealPath(Path.GetFullPath(root)));
            return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                   || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>C:\foo 或 \\server\share\foo。</summary>
    public static bool IsWindowsRooted(string path) =>
        (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
        || path.StartsWith("\\\\", StringComparison.Ordinal);

    private static bool IsRooted(string path) => Path.IsPathRooted(path) || IsWindowsRooted(path);

    /// <summary>按文本折叠 . 和 ..，给非 Windows 上出现的 Windows 路径用。</summary>
    private static string FoldWindows(string path)
    {
        var text = path.Replace('/', '\\');
        var unc = text.StartsWith("\\\\", StringComparison.Ordinal);
        var parts = text.Split('\\', StringSplitOptions.None);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == "." || part.Length == 0)
            {
                continue;
            }
            if (part == "..")
            {
                // 已经在盘符根上还往上走：钳在根，别把 .. 留在结果里
                // （GetFullPath 对 C:\.. 也是这个行为）
                if (stack.Count > 1)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                continue;
            }
            stack.Add(part);
        }
        var joined = string.Join("\\", stack);
        return unc ? "\\\\" + joined : joined;
    }

    private static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

    private static string SafeExpand(string text)
    {
        try { return Environment.ExpandEnvironmentVariables(text); }
        catch (Exception) { return text; }
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); }
        catch (Exception) { return ""; }
    }
}
