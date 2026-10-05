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
    public string Description => "在目录中按文件名通配符搜索文件（如 *.xlsx、*报表*），可选按文本内容过滤。";
    public JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "搜索的起始目录", true),
        ("pattern", "string", "文件名通配符，默认 *", false),
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
        foreach (var file in Directory.EnumerateFiles(root, pattern, options))
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
}

public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "创建或覆盖文本文件，也可以追加内容。目录不存在时自动创建。需要用户确认。";
    public JsonObject Parameters => ToolArgs.Schema(
        ("path", "string", "文件路径", true),
        ("content", "string", "要写入的完整内容", true),
        ("append", "boolean", "为 true 时追加到文件末尾，默认覆盖", false));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => ctx.Policy.EvaluateWrite(args.Str("path"));

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
        if (args.Bool("append", false))
        {
            await File.AppendAllTextAsync(path, content, new UTF8Encoding(false), ct);
        }
        else
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        }
        return ToolResult.Success($"已写入 {path}");
    }
}

public sealed class DeletePathTool : ITool
{
    public string Name => "delete_path";
    public string Description => "删除文件或文件夹（移入回收站，可恢复）。需要用户确认。";
    public JsonObject Parameters => ToolArgs.Schema(("path", "string", "要删除的文件或目录路径", true));

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        var decision = ctx.Policy.EvaluateWrite(args.Str("path"));
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
                    .Take(ctx.Policy.Config.BatchConfirmThreshold + 1).Count();
                if (count > ctx.Policy.Config.BatchConfirmThreshold)
                {
                    return PolicyDecision.Confirm($"该目录包含超过 {ctx.Policy.Config.BatchConfirmThreshold} 个文件，请仔细确认");
                }
            }
        }
        catch (Exception)
        {
            // 评估失败时仍要求确认
        }
        return PolicyDecision.Confirm("删除操作需要用户确认");
    }

    public string Describe(JsonElement args) => $"删除 {args.Str("path")}（移入回收站）";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(args.Required("path"));
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Task.FromResult(ToolResult.Fail($"路径不存在：{path}"));
        }
        ctx.Deleter.Delete(path);
        return Task.FromResult(ToolResult.Success($"已删除 {path}"));
    }
}
