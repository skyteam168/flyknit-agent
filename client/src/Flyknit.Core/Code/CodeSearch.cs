using System.Text.RegularExpressions;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Code;

/// <summary>搜索里的一处匹配：行号、列（都从 1 开始）、长度，和这一行（太长时截一段）。</summary>
public sealed record SearchHit(int Line, int Column, int Length, string Text, int TextOffset);

/// <summary>一个文件里的匹配。</summary>
public sealed record SearchFileHits(string Path, IReadOnlyList<SearchHit> Hits);

public sealed record SearchResult(IReadOnlyList<SearchFileHits> Files, int Total, bool Truncated, string Error);

/// <summary>
/// 编码模式的全局搜索（参照 VS Code 的 Ctrl+Shift+F）：在工作区的文本文件里找，支持区分大小写、全字匹配、正则、包含 / 排除的文件（glob，逗号分隔）。
/// 跳过依赖、编译输出、隐藏目录、二进制和太大的文件；匹配太多时截断。只读，不改文件。
/// </summary>
public static class CodeSearch
{
    public const int MaxMatches = 2000;
    public const int MaxFiles = 20_000;
    public const long MaxFileBytes = 1_000_000;
    public const int MaxLineChars = 240;

    public static SearchResult Run(string root, string query, bool regex, bool matchCase, bool wholeWord, string include, string exclude, CancellationToken ct)
    {
        if (query.Length == 0)
        {
            return new SearchResult(Array.Empty<SearchFileHits>(), 0, false, "");
        }
        Regex pattern;
        try
        {
            var text = regex ? query : Regex.Escape(query);
            if (wholeWord)
            {
                text = $@"(?<![\p{{L}}\p{{N}}_]){text}(?![\p{{L}}\p{{N}}_])";
            }
            pattern = new Regex(text, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(500));
        }
        catch (ArgumentException ex)
        {
            return new SearchResult(Array.Empty<SearchFileHits>(), 0, false, "正则表达式写错了：" + ex.Message);
        }
        var includes = Globs(include);
        var excludes = Globs(exclude);
        var files = new List<SearchFileHits>();
        var total = 0;
        var scanned = 0;
        var truncated = false;
        foreach (var rel in SourceTree.Files(root, ct: ct))
        {
            ct.ThrowIfCancellationRequested();
            var unix = rel.Replace('\\', '/');
            if (includes.Count > 0 && !includes.Any(g => g.IsMatch(unix)) || excludes.Any(g => g.IsMatch(unix)))
            {
                continue;
            }
            if (++scanned > MaxFiles)
            {
                truncated = true;
                break;
            }
            var full = Path.Combine(root, rel);
            string[] lines;
            try
            {
                if (new FileInfo(full).Length > MaxFileBytes || SourceTree.LooksBinary(full))
                {
                    continue;
                }
                lines = File.ReadAllLines(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var hits = new List<SearchHit>();
            for (var i = 0; i < lines.Length && total < MaxMatches; i++)
            {
                MatchCollection matches;
                try
                {
                    matches = pattern.Matches(lines[i]);
                    if (matches.Count == 0)
                    {
                        continue;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
                foreach (Match m in matches)
                {
                    if (m.Length == 0)
                    {
                        continue;
                    }
                    var (text, offset) = Clip(lines[i], m.Index);
                    hits.Add(new SearchHit(i + 1, m.Index + 1, m.Length, text, offset));
                    if (++total >= MaxMatches)
                    {
                        break;
                    }
                }
            }
            if (hits.Count > 0)
            {
                files.Add(new SearchFileHits(rel, hits));
            }
            if (total >= MaxMatches)
            {
                truncated = true;
                break;
            }
        }
        return new SearchResult(files, total, truncated, "");
    }

    /// <summary>
    /// 「*.ts, src/**」→ 几个 glob。只写了一个词（没有 * 和 /）的像 VS Code 一样宽松：文件名或任何一级文件夹名里有它就算；
    /// 没有 / 的（如 *.ts）在任何目录下都算。
    /// </summary>
    private static List<Glob> Globs(string text)
    {
        var list = new List<Glob>();
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var g = raw.Replace('\\', '/').TrimStart('/');
            if (g.StartsWith("./", StringComparison.Ordinal))
            {
                g = g[2..];
            }
            if (!g.Contains('*') && !g.Contains('/'))
            {
                list.Add(new Glob($"**/*{g}*"));
                list.Add(new Glob($"**/*{g}*/**"));
            }
            else
            {
                list.Add(new Glob(g.Contains('/') || g.StartsWith("**", StringComparison.Ordinal) ? g : "**/" + g));
                if (g.EndsWith("/**", StringComparison.Ordinal) && !g.StartsWith("**", StringComparison.Ordinal))
                {
                    // src/** 也认 apps/web/src/** 这种在下面几层的
                    list.Add(new Glob("**/" + g));
                }
            }
        }
        return list;
    }

    /// <summary>行太长时只留匹配附近的一段；返回这一段和它在原行里的起点（界面按它算高亮位置）。</summary>
    private static (string Text, int Offset) Clip(string line, int index)
    {
        if (line.Length <= MaxLineChars)
        {
            return (line, 0);
        }
        var start = Math.Max(0, Math.Min(index - 60, line.Length - MaxLineChars));
        return (line.Substring(start, Math.Min(MaxLineChars, line.Length - start)), start);
    }
}
