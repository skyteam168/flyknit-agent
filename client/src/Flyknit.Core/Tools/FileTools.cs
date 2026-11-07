using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Security;

namespace Flyknit.Core.Tools;

public sealed class ReadFileTool : ITool
{
    private const int MaxBytes = 2 * 1024 * 1024;

    public string Name => "read_file";
    public string Description => "读取文本文件内容（txt、csv、md、json、log、代码等）。可指定起始行和行数读取大文件的一部分。Excel、Word 等二进制文件请勿用此工具。";
    public JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "文件路径，支持 %USERPROFILE% 等环境变量", true),
        ("start_line", "integer", "起始行号，从 1 开始，默认 1", false),
        ("max_lines", "integer", "最多读取的行数，默认 400", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"读取文件 {args.Str("path")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(args.Required("path"));
        if (!File.Exists(path))
        {
            return ToolResult.Fail($"文件不存在：{path}");
        }
        var info = new FileInfo(path);
        var start = Math.Max(1, args.Int("start_line", 1));
        var max = Math.Clamp(args.Int("max_lines", 400), 1, 5000);

        var sb = new StringBuilder();
        var lineNo = 0;
        var read = 0;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                lineNo++;
                if (lineNo < start)
                {
                    continue;
                }
                if (read >= max || sb.Length > MaxBytes)
                {
                    sb.AppendLine($"…（文件还有更多内容，可用 start_line={lineNo} 继续读取）");
                    break;
                }
                sb.Append(lineNo).Append('\t').AppendLine(line);
                read++;
            }
        }
        return ToolResult.Success($"文件：{path}（{OpenAiSerializerSize(info.Length)}）\n{sb}");
    }

    private static string OpenAiSerializerSize(long n) => Chat.OpenAiSerializer.FormatSize(n);
}

public sealed class ListDirTool : ITool
{
    public string Name => "list_dir";
    public string Description => "列出目录中的文件和子目录，包括大小和修改时间。";
    public JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "目录路径", true),
        ("max_items", "integer", "最多返回的条目数，默认 200", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"查看目录 {args.Str("path")}";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(args.Required("path"));
        if (!Directory.Exists(path))
        {
            return Task.FromResult(ToolResult.Fail($"目录不存在：{path}"));
        }
        var max = Math.Clamp(args.Int("max_items", 200), 1, 2000);
        var dir = new DirectoryInfo(path);
        var sb = new StringBuilder($"目录：{dir.FullName}\n");
        var count = 0;
        foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name))
        {
            if (count++ >= max) break;
            sb.AppendLine($"[目录] {d.Name}/\t{d.LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name))
        {
            if (count++ >= max) break;
            sb.AppendLine($"{f.Name}\t{Chat.OpenAiSerializer.FormatSize(f.Length)}\t{f.LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        if (count > max)
        {
            sb.AppendLine($"…（仅显示前 {max} 项）");
        }
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}

public sealed class SearchFilesTool : ITool
{
    public string Name => "search_files";
    public string Description => "在目录中按文件名通配符搜索文件（如 *.xlsx、*报表*），可选按文本内容过滤。" +
        "支持 ** 跨目录（如 src/**/*.cs、**/{README,readme}.md），带 ** 或目录时会跳过 .git、node_modules。";
    public JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "搜索的起始目录", true),
        ("pattern", "string", "文件名通配符，默认 *；可写 **/*.ts 这类跨目录的写法", false),
        ("contains", "string", "只返回内容包含此文本的文件（仅限文本文件）", false),
        ("max_results", "integer", "最多返回数量，默认 100", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => $"在 {args.Str("path")} 中搜索 {args.Str("pattern", "*")}";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var root = ctx.ResolvePath(args.Required("path"));
        if (!Directory.Exists(root))
        {
            return ToolResult.Fail($"目录不存在：{root}");
        }
        var pattern = args.Str("pattern", "*");
        var contains = args.Str("contains");
        var max = Math.Clamp(args.Int("max_results", 100), 1, 1000);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 12 };

        var results = new List<string>();
        foreach (var file in Candidates(root, pattern, options, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (contains.Length > 0)
            {
                var info = new FileInfo(file);
                if (info.Length > 5 * 1024 * 1024)
                {
                    continue;
                }
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(file, ct);
                }
                catch (IOException)
                {
                    continue;
                }
                if (!text.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            results.Add(file);
            if (results.Count >= max)
            {
                break;
            }
        }
        return results.Count == 0
            ? ToolResult.Success("没有找到匹配的文件")
            : ToolResult.Success($"找到 {results.Count} 个文件：\n" + string.Join("\n", results));
    }

    // 只按文件名找的（*.xlsx、*报表*）照旧用系统自带的通配符；带 ** 或目录的自己逐层遍历
    private static readonly IReadOnlySet<string> WalkSkips = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".svn", ".hg", "node_modules" };

    private static IEnumerable<string> Candidates(string root, string pattern, EnumerationOptions options, CancellationToken ct)
    {
        if (!Glob.NeedsWalk(pattern))
        {
            return Directory.EnumerateFiles(root, pattern, options);
        }
        var glob = new Glob(pattern);
        return SourceTree.Files(root, WalkSkips, ct: ct).Where(glob.IsMatch).Select(rel => Path.Combine(root, rel));
    }
}

public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "创建或覆盖文本文件，也可以追加内容。目录不存在时自动创建。相对路径以工作区为准，产出文件优先放在工作区内。";
    public JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "文件路径", true),
        ("content", "string", "要写入的完整内容", true),
        ("append", "boolean", "为 true 时追加到文件末尾，默认覆盖", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => ctx.AssessWrite(args.Str("path"));

    public string Describe(JsonElement args)
    {
        var action = args.Bool("append", false) ? "追加内容到" : File.Exists(args.Str("path")) ? "覆盖文件" : "新建文件";
        return $"{action} {args.Str("path")}（{args.Str("content").Length} 个字符）";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(args.Required("path"));
        var content = args.Str("content");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var append = args.Bool("append", false);
        var existed = File.Exists(path);
        var emptyBefore = existed && new FileInfo(path).Length == 0;
        // 覆盖写是唯一不可逆的操作（删除有回收站），先留一份原文件。
        // 追加不会毁掉已有内容，但要能「还原这次任务的改动」，同样留一份（有改动记录时）。
        var saved = append && ctx.RecordChange is null ? null : ctx.Backup?.Capture(path);
        ctx.NoteChange(existed
            ? new FileChange(path, FileChange.Modified, saved, Name, emptyBefore)
            : new FileChange(path, FileChange.Created, null, Name));

        if (append)
        {
            await File.AppendAllTextAsync(path, content, new UTF8Encoding(false), ct);
        }
        else
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        }
        var note = saved is null ? "" : "（原文件已备份）";
        return ToolResult.Success($"已写入 {path}{note}").WithOutputs(path);
    }
}

/// <summary>
/// 改文本文件里的一段：把 old_text 换成 new_text。改文档（报告、通知、发言稿）时只动要改的那一段，
/// 不用把整篇重写一遍——省 token，也不会顺手把别的段落改掉。权限、备份、改动记录和 write_file 一样。
/// </summary>
public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";
    public string Description => "修改文本文件（md、txt、csv、代码等）中的一段：把 old_text 原样替换成 new_text，其余内容不动。" +
        "old_text 必须和文件里的文字完全一致（含标点、空格、换行），并且要足够长、在文件中唯一；要改多处时分多次调用，或 replace_all=true。" +
        "改文档优先用它，不要用 write_file 整篇重写。";
    public JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "文件路径", true),
        ("old_text", "string", "要替换的原文（与文件内容完全一致）", true),
        ("new_text", "string", "替换后的内容；为空表示删掉这段", true),
        ("replace_all", "boolean", "为 true 时替换所有出现的地方，默认只允许出现一次", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => ctx.AssessWrite(args.Str("path"));

    public string Describe(JsonElement args)
    {
        var old = args.Str("old_text");
        var preview = old.Length > 30 ? old[..30] + "…" : old;
        return $"修改文件 {args.Str("path")}：「{preview.ReplaceLineEndings(" ")}」";
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(args.Required("path"));
        if (!File.Exists(path))
        {
            return ToolResult.Fail($"文件不存在：{path}。新建文件请用 write_file");
        }
        var oldText = args.Str("old_text");
        var newText = args.Str("new_text");
        if (oldText.Length == 0)
        {
            return ToolResult.Fail("old_text 不能为空。要在开头或末尾加内容，就把相邻的一段原文连同新内容一起替换");
        }
        if (oldText == newText)
        {
            return ToolResult.Fail("old_text 和 new_text 一样，没有要改的");
        }

        string content;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            content = await reader.ReadToEndAsync(ct);
        }
        var (find, replace) = Match(content, oldText, newText);
        var count = Count(content, find);
        if (count == 0)
        {
            return ToolResult.Fail("文件里找不到 old_text。先用 read_file 看一下当前内容（文件可能刚被员工改过），再照原文复制要改的那一段");
        }
        var all = args.Bool("replace_all", false);
        if (count > 1 && !all)
        {
            return ToolResult.Fail($"old_text 在文件里出现了 {count} 次，不知道改哪一处。多带一些前后文让它唯一，或者 replace_all=true 全部替换");
        }

        var saved = ctx.Backup?.Capture(path);
        ctx.NoteChange(new FileChange(path, FileChange.Modified, saved, Name));
        var updated = all ? content.Replace(find, replace, StringComparison.Ordinal) : ReplaceOnce(content, find, replace);
        await File.WriteAllTextAsync(path, updated, new UTF8Encoding(false), ct);
        var note = saved is null ? "" : "（原文件已备份）";
        return ToolResult.Success($"已修改 {path}：替换了 {(all ? count : 1)} 处{note}").WithOutputs(path);
    }

    /// <summary>文件是 Windows 换行（\r\n）而模型给的是 \n 时，按文件的换行来找、来写。</summary>
    internal static (string Find, string Replace) Match(string content, string oldText, string newText)
    {
        if (content.Contains(oldText, StringComparison.Ordinal) || !content.Contains("\r\n", StringComparison.Ordinal))
        {
            return (oldText, newText);
        }
        var crlf = oldText.Replace("\r\n", "\n").Replace("\n", "\r\n");
        return content.Contains(crlf, StringComparison.Ordinal)
            ? (crlf, newText.Replace("\r\n", "\n").Replace("\n", "\r\n"))
            : (oldText, newText);
    }

    private static int Count(string content, string find)
    {
        var n = 0;
        for (var i = content.IndexOf(find, StringComparison.Ordinal); i >= 0; i = content.IndexOf(find, i + find.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    private static string ReplaceOnce(string content, string find, string replace)
    {
        var i = content.IndexOf(find, StringComparison.Ordinal);
        return content[..i] + replace + content[(i + find.Length)..];
    }
}

public sealed class DeletePathTool : ITool
{
    public string Name => "delete_path";
    public string Description => "删除文件或文件夹（移入回收站，可恢复）。删除前会请用户确认。";
    public JsonObject Parameters => ToolArgs.Schema(("path", "string", "要删除的文件或目录路径", true));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        var decision = ctx.AssessWrite(args.Str("path"), delete: true);
        if (decision.Level == RiskLevel.Blocked)
        {
            return decision;
        }
        try
        {
            var path = ctx.ResolvePath(args.Str("path"));
            if (Directory.Exists(path))
            {
                var count = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Take(ctx.BatchDeleteThreshold + 1).Count();
                if (count > ctx.BatchDeleteThreshold)
                {
                    // 大批量删除在任何权限下都要逐次确认
                    return PolicyDecision.Confirm($"该目录包含超过 {ctx.BatchDeleteThreshold} 个文件，请仔细确认");
                }
            }
        }
        catch (Exception)
        {
            // 评估失败时按权限判定结果处理
        }
        return decision;
    }

    public string Describe(JsonElement args) => $"删除 {args.Str("path")}（移入回收站）";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(args.Required("path"));
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Task.FromResult(ToolResult.Fail($"路径不存在：{path}"));
        }
        if (File.Exists(path))
        {
            // 回收站之外再留一份：「还原这次任务的改动」时直接拷回来
            var saved = ctx.RecordChange is null ? null : ctx.Backup?.Capture(path);
            var emptyBefore = new FileInfo(path).Length == 0;
            ctx.Deleter.Delete(path);
            ctx.NoteChange(new FileChange(path, FileChange.Deleted, saved, Name, emptyBefore));
        }
        else
        {
            ctx.Deleter.Delete(path);
            ctx.NoteChange(new FileChange(path, FileChange.Deleted, null, Name));
        }
        return Task.FromResult(ToolResult.Success($"已删除 {path}"));
    }
}
