using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Tools;

public enum OutputCheckStatus
{
    /// <summary>检查通过（Detail 里可能有行数、页数这类事实，交给模型自己对照要求）。</summary>
    Ok,

    /// <summary>能打开，但内容看起来不对劲（全是空表、文档没有文字）。不算错，提醒模型确认。</summary>
    Warning,

    /// <summary>确定有问题：文件不存在、是空的、打不开、被截断。</summary>
    Problem,

    /// <summary>没法检查（被占用、加密、没权限）。不当成错误，免得把对的结果“修”坏。</summary>
    Unverifiable,
}

public sealed record OutputCheck(string Path, OutputCheckStatus Status, string Detail)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 产出文件的确定性检查：只看能确定的事实——存在不存在、是不是空的、能不能按格式打开，
/// 以及工作表行数、页数这类一眼能看出“不对劲”的数字。不做语义判断（数据对不对交给模型对照用户要求）。
///
/// 原则是宁可放过、不可误报：打不开的原因不确定（被 Excel 占着、加密、没权限）一律算“无法检查”，
/// 不算错误，否则模型会去“修”一个本来没问题的文件。检查本身出任何意外都当作无法检查，绝不让任务因此失败。
/// </summary>
public static class OutputVerifier
{
    /// <summary>超过这个大小只查存在和文件头，不解析内容（免得拖慢任务）。</summary>
    public const long MaxParseBytes = 30L * 1024 * 1024;

    /// <summary>Office 文件解压后的 XML 超过这个大小就不数行数了。</summary>
    public const long MaxXmlBytes = 60L * 1024 * 1024;

    /// <summary>一次最多查几个文件（run_shell 可能一次改了一堆文件）。</summary>
    public const int MaxFiles = 8;

    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xlsm", ".docx", ".pptx", ".xls", ".doc", ".ppt", ".pdf", ".csv", ".tsv", ".json",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp",
    };

    /// <summary>是不是值得检查的文档类产出（代码、日志这类不查）。</summary>
    public static bool IsDocument(string path) => Documents.Contains(Path.GetExtension(path));

    public static OutputCheck Check(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new(path, OutputCheckStatus.Problem, "文件不存在（可能没保存成功，或者后来被删掉、移走了）");
            }
            if (info.Length == 0)
            {
                return new(path, OutputCheckStatus.Problem, "文件是空的（0 字节）");
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[8];
            var read = stream.Read(head, 0, head.Length);
            stream.Position = 0;
            var ext = info.Extension.ToLowerInvariant();
            return ext switch
            {
                ".xlsx" or ".xlsm" or ".docx" or ".pptx" => CheckOpenXml(path, stream, head, read, ext, info.Length),
                ".xls" or ".doc" or ".ppt" => IsCfb(head, read)
                    ? new(path, OutputCheckStatus.Ok, "旧版 Office 格式，文件头正常")
                    : new(path, OutputCheckStatus.Problem, $"不是有效的 {ext} 文件（文件头不对），Office 可能打不开"),
                ".pdf" => CheckPdf(path, stream, head, read, info.Length),
                ".csv" or ".tsv" => CheckTable(path, stream, info.Length),
                ".json" => CheckJson(path, stream, info.Length),
                ".png" => Magic(path, head, read, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "PNG"),
                ".jpg" or ".jpeg" => Magic(path, head, read, new byte[] { 0xFF, 0xD8, 0xFF }, "JPEG"),
                ".gif" => Magic(path, head, read, "GIF8"u8.ToArray(), "GIF"),
                ".bmp" => Magic(path, head, read, "BM"u8.ToArray(), "BMP"),
                _ => new(path, OutputCheckStatus.Ok, ""),
            };
        }
        catch (IOException)
        {
            return new(path, OutputCheckStatus.Unverifiable, "文件正被其他程序占用，暂时无法检查");
        }
        catch (UnauthorizedAccessException)
        {
            return new(path, OutputCheckStatus.Unverifiable, "没有权限读取，无法检查");
        }
        catch (Exception)
        {
            return new(path, OutputCheckStatus.Unverifiable, "无法检查");
        }
    }

    /// <summary>附在工具结果后面给模型看的检查结果。没有值得说的就返回 null。</summary>
    public static string? Describe(IReadOnlyList<OutputCheck> checks)
    {
        var worth = checks.Where(c => c.Detail.Length > 0).ToList();
        if (worth.Count == 0)
        {
            return null;
        }
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("【系统检查】这一步产出的文件：");
        foreach (var c in worth)
        {
            var mark = c.Status switch
            {
                OutputCheckStatus.Problem => "✗ ",
                OutputCheckStatus.Warning => "⚠ ",
                _ => "",
            };
            sb.AppendLine($"- {mark}{c.Name}：{c.Detail}");
        }
        if (worth.Any(c => c.Status is OutputCheckStatus.Problem))
        {
            sb.AppendLine("标 ✗ 的文件有问题，交付前先修好；如果是有意为之（比如本来就要删掉），回答里说明。");
        }
        else if (worth.Any(c => c.Status is OutputCheckStatus.Warning))
        {
            sb.AppendLine("标 ⚠ 的请对照用户的要求确认是不是本来就该这样。");
        }
        else
        {
            sb.AppendLine("请对照用户的要求看看这些数字对不对（比如行数、页数是否合理）。");
        }
        return sb.ToString().TrimEnd();
    }

    private static OutputCheck CheckOpenXml(string path, FileStream stream, byte[] head, int read, string ext, long length)
    {
        if (IsCfb(head, read))
        {
            return new(path, OutputCheckStatus.Unverifiable, "加密的 Office 文件，无法检查内容");
        }
        if (read < 2 || head[0] != 'P' || head[1] != 'K')
        {
            return new(path, OutputCheckStatus.Problem, $"不是有效的 {ext} 文件（文件头不对），Office 打不开");
        }
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return new(path, OutputCheckStatus.Problem, "文件已损坏（可能没写完），Office 打不开");
        }
        using (zip)
        {
            if (zip.GetEntry("[Content_Types].xml") is null)
            {
                return new(path, OutputCheckStatus.Problem, "文件结构不完整，Office 打不开");
            }
            if (length > MaxParseBytes)
            {
                return new(path, OutputCheckStatus.Ok, "文件较大，只检查了结构，结构完整");
            }
            return ext switch
            {
                ".xlsx" or ".xlsm" => DescribeWorkbook(path, zip),
                ".docx" => DescribeDocument(path, zip),
                _ => DescribeSlides(path, zip),
            };
        }
    }

    private static OutputCheck DescribeWorkbook(string path, ZipArchive zip)
    {
        var names = zip.GetEntry("xl/workbook.xml") is { } wb
            ? Regex.Matches(ReadEntry(wb), "<sheet\\b[^>]*\\bname=\"([^\"]*)\"").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList()
            : new List<string>();
        var sheets = zip.Entries
            .Where(e => Regex.IsMatch(e.FullName, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(e => int.Parse(Regex.Match(e.Name, @"\d+").Value))
            .ToList();
        if (sheets.Count == 0)
        {
            return new(path, OutputCheckStatus.Problem, "工作簿里没有工作表，Excel 打不开");
        }
        if (sheets.Sum(s => s.Length) > MaxXmlBytes)
        {
            return new(path, OutputCheckStatus.Ok, $"{sheets.Count} 个工作表，数据量很大，未统计行数");
        }
        var rows = sheets.Select(s => Regex.Matches(ReadEntry(s), "<row\\b").Count).ToList();
        var parts = rows.Select((n, i) => $"{(i < names.Count ? names[i] : $"表{i + 1}")} {n} 行").Take(6);
        var detail = $"{sheets.Count} 个工作表（{string.Join("，", parts)}{(sheets.Count > 6 ? "…" : "")}）";
        return rows.All(n => n <= 1)
            ? new(path, OutputCheckStatus.Warning, detail + "，所有工作表都没有数据行（最多只有表头）")
            : new(path, OutputCheckStatus.Ok, detail);
    }

    private static OutputCheck DescribeDocument(string path, ZipArchive zip)
    {
        if (zip.GetEntry("word/document.xml") is not { } doc)
        {
            return new(path, OutputCheckStatus.Problem, "文档正文缺失，Word 打不开");
        }
        if (doc.Length > MaxXmlBytes)
        {
            return new(path, OutputCheckStatus.Ok, "文档很大，结构完整");
        }
        var xml = ReadEntry(doc);
        var paragraphs = Regex.Matches(xml, "<w:p[ >]").Count;
        var text = Regex.Matches(xml, "<w:t(?: [^>]*)?>([^<]*)</w:t>").Sum(m => m.Groups[1].Value.Length);
        var tables = Regex.Matches(xml, "<w:tbl>").Count;
        var images = zip.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase));
        var detail = $"{paragraphs} 段，约 {text} 字" + (tables > 0 ? $"，{tables} 个表格" : "") + (images > 0 ? $"，{images} 张图片" : "");
        return text == 0 && images == 0
            ? new(path, OutputCheckStatus.Warning, "文档没有文字内容")
            : new(path, OutputCheckStatus.Ok, detail);
    }

    private static OutputCheck DescribeSlides(string path, ZipArchive zip)
    {
        var slides = zip.Entries.Count(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase));
        return slides == 0
            ? new(path, OutputCheckStatus.Warning, "演示文稿里没有幻灯片")
            : new(path, OutputCheckStatus.Ok, $"{slides} 页幻灯片");
    }

    private static OutputCheck CheckPdf(string path, FileStream stream, byte[] head, int read, long length)
    {
        if (read < 5 || Encoding.ASCII.GetString(head, 0, 5) != "%PDF-")
        {
            return new(path, OutputCheckStatus.Problem, "不是有效的 PDF（文件头不对）");
        }
        var tailLength = (int)Math.Min(2048, length);
        stream.Seek(-tailLength, SeekOrigin.End);
        var tail = new byte[tailLength];
        stream.ReadExactly(tail);
        if (!Encoding.ASCII.GetString(tail).Contains("%%EOF"))
        {
            return new(path, OutputCheckStatus.Problem, "PDF 不完整（可能还没写完或被截断）");
        }
        if (length > MaxParseBytes)
        {
            return new(path, OutputCheckStatus.Ok, "文件较大，只检查了首尾，完整");
        }
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
        var pages = Regex.Matches(reader.ReadToEnd(), @"/Type\s*/Page(?!s)").Count;
        return new(path, OutputCheckStatus.Ok, pages > 0 ? $"约 {pages} 页" : "结构完整");
    }

    private static OutputCheck CheckTable(string path, FileStream stream, long length)
    {
        if (length > MaxParseBytes)
        {
            return new(path, OutputCheckStatus.Ok, "文件较大，未统计行数");
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var lines = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Length > 0)
            {
                lines++;
            }
        }
        return lines <= 1
            ? new(path, OutputCheckStatus.Warning, lines == 0 ? "没有内容" : "只有表头，没有数据行")
            : new(path, OutputCheckStatus.Ok, $"{lines} 行（含表头）");
    }

    private static OutputCheck CheckJson(string path, FileStream stream, long length)
    {
        if (length > MaxParseBytes)
        {
            return new(path, OutputCheckStatus.Ok, "");
        }
        try
        {
            using var doc = JsonDocument.Parse(stream);
            return new(path, OutputCheckStatus.Ok, "");
        }
        catch (JsonException ex)
        {
            return new(path, OutputCheckStatus.Problem, $"不是合法的 JSON：{ex.Message}");
        }
    }

    private static OutputCheck Magic(string path, byte[] head, int read, byte[] magic, string kind) =>
        read >= magic.Length && head.AsSpan(0, magic.Length).SequenceEqual(magic)
            ? new(path, OutputCheckStatus.Ok, "")
            : new(path, OutputCheckStatus.Problem, $"不是有效的 {kind} 图片（文件头不对），可能打不开");

    private static bool IsCfb(byte[] head, int read) =>
        read >= 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
