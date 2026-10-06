using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Flyknit.Core.Preview;

/// <summary>按扩展名认领的 Provider 基类。</summary>
public abstract class ExtensionPreviewProvider : IPreviewProvider
{
    public abstract string Name { get; }
    protected abstract IReadOnlySet<string> Extensions { get; }

    public virtual bool CanHandle(string path) =>
        Extensions.Contains(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    public abstract Task<PreviewDocument> LoadAsync(string path, CancellationToken ct);

    protected static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
}

/// <summary>图片：读成 data URL 交给界面显示。</summary>
public sealed class ImagePreviewProvider : ExtensionPreviewProvider
{
    public const long MaxBytes = 12 * 1024 * 1024;

    public override string Name => "图片";

    protected override IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "svg", "ico", "avif",
    };

    public override async Task<PreviewDocument> LoadAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            return PreviewDocument.Unavailable($"图片有 {PreviewRegistry.Format(info.Length)}，太大了，请用看图软件打开");
        }
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var mime = Ext(path) switch
        {
            "png" => "image/png",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            "ico" => "image/x-icon",
            "avif" => "image/avif",
            _ => "image/jpeg",
        };
        return new PreviewDocument(PreviewKind.Image)
        {
            DataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}",
        };
    }
}

/// <summary>PDF：交给 WebView2 自带的阅读器。</summary>
public sealed class PdfPreviewProvider : ExtensionPreviewProvider
{
    public const long MaxBytes = 20 * 1024 * 1024;

    public override string Name => "PDF";

    protected override IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pdf" };

    public override async Task<PreviewDocument> LoadAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            return PreviewDocument.Unavailable($"PDF 有 {PreviewRegistry.Format(info.Length)}，太大了，请用 PDF 阅读器打开");
        }
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return new PreviewDocument(PreviewKind.Pdf)
        {
            DataUrl = $"data:application/pdf;base64,{Convert.ToBase64String(bytes)}",
        };
    }
}

/// <summary>CSV / TSV：切成表格。</summary>
public sealed class TablePreviewProvider : ExtensionPreviewProvider
{
    public const int MaxRows = 500;

    public override string Name => "表格";

    protected override IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "csv", "tsv" };

    public override async Task<PreviewDocument> LoadAsync(string path, CancellationToken ct)
    {
        var (text, truncated) = await PreviewRegistry.ReadTextAsync(path, 2_000_000, ct);
        var separator = Ext(path) == "tsv" ? '\t' : ',';
        var rows = new List<IReadOnlyList<string>>();
        foreach (var line in text.Split('\n'))
        {
            if (rows.Count >= MaxRows)
            {
                truncated = true;
                break;
            }
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                continue; // 空行（含文件末尾那个换行）不算一行数据
            }
            rows.Add(SplitLine(trimmed, separator));
        }
        return new PreviewDocument(PreviewKind.Table)
        {
            Sections = new[] { new PreviewSection(Path.GetFileName(path), Rows: rows) },
            Notice = truncated ? $"只显示前 {rows.Count} 行" : null,
        };
    }

    /// <summary>按分隔符切，支持 "" 转义的引号字段。</summary>
    public static List<string> SplitLine(string line, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == separator)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }
        cells.Add(cell.ToString());
        return cells;
    }
}

/// <summary>
/// 文本与代码。Markdown 和流程图单独标出来，界面才知道该渲染还是该高亮。
/// </summary>
public sealed class TextPreviewProvider : ExtensionPreviewProvider
{
    public const int MaxChars = 400_000;

    public override string Name => "文本";

    /// <summary>扩展名 → 代码高亮用的语言名。加一种语言只要往这里加一行。</summary>
    public static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["txt"] = "", ["log"] = "", ["ini"] = "ini", ["cfg"] = "ini", ["conf"] = "ini", ["env"] = "ini",
        ["json"] = "json", ["jsonl"] = "json", ["xml"] = "xml", ["html"] = "html", ["htm"] = "html",
        ["yml"] = "yaml", ["yaml"] = "yaml", ["toml"] = "toml", ["css"] = "css", ["scss"] = "scss",
        ["js"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript", ["ts"] = "typescript",
        ["tsx"] = "tsx", ["jsx"] = "jsx", ["vue"] = "html", ["py"] = "python", ["cs"] = "csharp",
        ["java"] = "java", ["go"] = "go", ["rs"] = "rust", ["php"] = "php", ["rb"] = "ruby",
        ["sql"] = "sql", ["sh"] = "bash", ["bash"] = "bash", ["ps1"] = "powershell", ["psm1"] = "powershell",
        ["bat"] = "bat", ["cmd"] = "bat", ["c"] = "c", ["h"] = "c", ["cpp"] = "cpp", ["hpp"] = "cpp",
        ["srt"] = "", ["vtt"] = "", ["csproj"] = "xml", ["sln"] = "", ["gitignore"] = "", ["editorconfig"] = "ini",
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase) { "md", "markdown", "mdx" };
    private static readonly HashSet<string> DiagramExtensions = new(StringComparer.OrdinalIgnoreCase) { "mmd", "mermaid", "dot", "gv", "puml", "plantuml" };

    protected override IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(Languages.Keys.Concat(MarkdownExtensions).Concat(DiagramExtensions), StringComparer.OrdinalIgnoreCase);

    public override async Task<PreviewDocument> LoadAsync(string path, CancellationToken ct)
    {
        var (text, truncated) = await PreviewRegistry.ReadTextAsync(path, MaxChars, ct);
        var ext = Ext(path);
        var kind = MarkdownExtensions.Contains(ext) ? PreviewKind.Markdown
            : DiagramExtensions.Contains(ext) ? PreviewKind.Diagram
            : PreviewKind.Text;
        return new PreviewDocument(kind)
        {
            Text = text,
            Language = kind == PreviewKind.Diagram
                ? (ext is "dot" or "gv" ? "dot" : ext is "puml" or "plantuml" ? "plantuml" : "mermaid")
                : Languages.GetValueOrDefault(ext, ""),
            Notice = truncated ? $"文件较大，只显示前 {MaxChars / 1000} 千字" : null,
        };
    }
}

/// <summary>
/// Word / Excel / PowerPoint。
///
/// 这三种格式本质上是 zip 里的 XML，所以直接用 System.IO.Compression + XDocument 解，
/// 不引第三方库——工厂电脑上多半没装 Office 以外的东西，也不该为了预览去装。
/// 只取得出文字和表格，样式一律不管，目的是让用户快速确认内容对不对，要排版还是用 Office 打开。
/// </summary>
public sealed class OfficePreviewProvider : ExtensionPreviewProvider
{
    public const int MaxSections = 200;
    public const int MaxRowsPerSheet = 300;

    public override string Name => "Office 文档";

    protected override IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "docx", "xlsx", "pptx", "docm", "xlsm", "pptm",
    };

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public override Task<PreviewDocument> LoadAsync(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var ext = Ext(path);
        var doc = ext.StartsWith("doc") ? Word(zip)
            : ext.StartsWith("xls") ? Excel(zip, ct)
            : Slides(zip);
        return Task.FromResult(doc);
    }

    // ---------- Word ----------
    private static PreviewDocument Word(ZipArchive zip)
    {
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null)
        {
            return PreviewDocument.Unavailable("这个 Word 文件里没有正文（可能是加密的）");
        }
        var xml = XDocument.Load(entry.Open());
        var body = xml.Descendants(W + "body").FirstOrDefault();
        if (body is null)
        {
            return PreviewDocument.Unavailable("这个 Word 文件里没有正文（可能是加密的）");
        }
        var sb = new StringBuilder();
        // 只遍历 body 的直接子节点：段落和表格是并列的，表格里的段落不能再当正文走一遍
        foreach (var node in body.Elements())
        {
            if (node.Name == W + "p")
            {
                sb.AppendLine(Paragraph(node));
            }
            else if (node.Name == W + "tbl")
            {
                sb.AppendLine().Append(Table(node)).AppendLine();
            }
        }
        return new PreviewDocument(PreviewKind.Markdown) { Text = sb.ToString().Trim() };
    }

    /// <summary>一个段落。标题样式转成 Markdown 的 #，列表转成 - ，这样界面按 Markdown 渲染就有层级。</summary>
    private static string Paragraph(XElement p)
    {
        var text = string.Concat(p.Descendants(W + "t").Select(t => t.Value));
        if (text.Length == 0)
        {
            return "";
        }
        var style = p.Descendants(W + "pStyle").FirstOrDefault()?.Attribute(W + "val")?.Value ?? "";
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            var level = int.TryParse(style.AsSpan(7), out var n) ? Math.Clamp(n, 1, 6) : 1;
            return Environment.NewLine + new string('#', level) + " " + text + Environment.NewLine;
        }
        // 项目符号 / 编号列表：按缩进级别加前缀
        var numbering = p.Descendants(W + "numPr").FirstOrDefault();
        if (numbering is not null)
        {
            var level = int.TryParse(numbering.Descendants(W + "ilvl").FirstOrDefault()?.Attribute(W + "val")?.Value, out var l) ? Math.Clamp(l, 0, 5) : 0;
            return new string(' ', level * 2) + "- " + text;
        }
        return text;
    }

    /// <summary>表格转成 Markdown 表格，界面渲染出来就还是一张表，不会糊成一行字。</summary>
    private static string Table(XElement tbl)
    {
        var rows = tbl.Elements(W + "tr")
            .Select(tr => tr.Elements(W + "tc")
                .Select(tc => string.Concat(tc.Descendants(W + "t").Select(t => t.Value)).Replace("|", "\\|").Trim())
                .ToList())
            .Where(cells => cells.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return "";
        }
        var columns = rows.Max(r => r.Count);
        var sb = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            sb.Append("| ");
            for (var c = 0; c < columns; c++)
            {
                sb.Append(c < cells.Count ? cells[c] : "").Append(" | ");
            }
            sb.AppendLine();
            if (i == 0)
            {
                // Markdown 表格必须有分隔行，否则不会被当成表格
                sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", columns))).AppendLine();
            }
        }
        return sb.ToString();
    }

    // ---------- PowerPoint ----------
    private static PreviewDocument Slides(ZipArchive zip)
    {
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SlideNumber(e.Name))
            .Take(MaxSections)
            .ToList();
        if (slides.Count == 0)
        {
            return PreviewDocument.Unavailable("这个演示文稿里没有幻灯片");
        }

        var sections = new List<PreviewSection>();
        foreach (var entry in slides)
        {
            var xml = XDocument.Load(entry.Open());
            // 每个文本框是一个 a:p，框内的 a:t 拼起来才是一行完整的话
            var lines = xml.Descendants(A + "p")
                .Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)).Trim())
                .Where(l => l.Length > 0)
                .ToList();
            var number = SlideNumber(entry.Name);
            var title = lines.Count > 0 ? lines[0] : "（无标题）";
            var body = lines.Count > 1 ? string.Join("\n", lines.Skip(1)) : "";
            var notes = SpeakerNotes(zip, number);
            if (notes.Length > 0)
            {
                body = body.Length > 0 ? $"{body}\n\n【备注】{notes}" : $"【备注】{notes}";
            }
            sections.Add(new PreviewSection($"第 {number} 页 · {title}", body));
        }
        return new PreviewDocument(PreviewKind.Sections) { Sections = sections };
    }

    /// <summary>演讲者备注，和幻灯片按编号对应。</summary>
    private static string SpeakerNotes(ZipArchive zip, int slideNumber)
    {
        var entry = zip.GetEntry($"ppt/notesSlides/notesSlide{slideNumber}.xml");
        if (entry is null)
        {
            return "";
        }
        try
        {
            var xml = XDocument.Load(entry.Open());
            var lines = xml.Descendants(A + "p")
                .Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)).Trim())
                .Where(l => l.Length > 0 && !int.TryParse(l, out _)) // 页码占位符不算备注
                .ToList();
            return string.Join("\n", lines);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static int SlideNumber(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    // ---------- Excel ----------
    private static PreviewDocument Excel(ZipArchive zip, CancellationToken ct)
    {
        var shared = SharedStrings(zip);
        var names = SheetNames(zip);
        var sheets = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => SlideNumber(e.Name))
            .ToList();
        if (sheets.Count == 0)
        {
            return PreviewDocument.Unavailable("这个工作簿里没有工作表");
        }

        var sections = new List<PreviewSection>();
        var truncated = false;
        foreach (var entry in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var xml = XDocument.Load(entry.Open());
            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in xml.Descendants(S + "row"))
            {
                if (rows.Count >= MaxRowsPerSheet)
                {
                    truncated = true;
                    break;
                }
                var cells = new List<string>();
                var expected = 1;
                foreach (var c in row.Elements(S + "c"))
                {
                    // 空单元格在 XML 里直接省略，要按 A1/B1 的列号补齐，否则列会错位
                    var column = ColumnIndex(c.Attribute("r")?.Value ?? "");
                    while (column > expected)
                    {
                        cells.Add("");
                        expected++;
                    }
                    cells.Add(CellText(c, shared));
                    expected++;
                }
                rows.Add(cells);
            }
            var index = SlideNumber(entry.Name);
            var name = names.Count >= index && index >= 1 ? names[index - 1] : $"工作表 {index}";
            sections.Add(new PreviewSection(name, Rows: rows));
        }
        return new PreviewDocument(PreviewKind.Table)
        {
            Sections = sections,
            Notice = truncated ? $"每张表只显示前 {MaxRowsPerSheet} 行" : null,
        };
    }

    private static string CellText(XElement c, IReadOnlyList<string> shared)
    {
        var type = c.Attribute("t")?.Value;
        if (type == "inlineStr")
        {
            return string.Concat(c.Descendants(S + "t").Select(t => t.Value));
        }
        var v = c.Element(S + "v")?.Value ?? "";
        if (type == "s" && int.TryParse(v, out var i) && i >= 0 && i < shared.Count)
        {
            return shared[i];
        }
        return v;
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return new List<string>();
        }
        var xml = XDocument.Load(entry.Open());
        return xml.Descendants(S + "si")
            .Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value)))
            .ToList();
    }

    private static List<string> SheetNames(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/workbook.xml");
        if (entry is null)
        {
            return new List<string>();
        }
        var xml = XDocument.Load(entry.Open());
        return xml.Descendants(S + "sheet").Select(s => s.Attribute("name")?.Value ?? "").ToList();
    }

    /// <summary>"B7" → 2。</summary>
    public static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c))
            {
                break;
            }
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return Math.Max(index, 1);
    }
}

/// <summary>压缩包：列出里面有什么，不解压。</summary>
public sealed class ArchivePreviewProvider : ExtensionPreviewProvider
{
    public const int MaxEntries = 500;

    public override string Name => "压缩包";

    protected override IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zip" };

    public override Task<PreviewDocument> LoadAsync(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var rows = new List<IReadOnlyList<string>> { new[] { "文件", "大小" } };
        foreach (var e in zip.Entries.Take(MaxEntries))
        {
            rows.Add(new[] { e.FullName, e.Length == 0 ? "" : PreviewRegistry.Format(e.Length) });
        }
        return Task.FromResult(new PreviewDocument(PreviewKind.Listing)
        {
            Sections = new[] { new PreviewSection($"{zip.Entries.Count} 个条目", Rows: rows) },
            Notice = zip.Entries.Count > MaxEntries ? $"只显示前 {MaxEntries} 个" : null,
        });
    }
}

/// <summary>兜底：认领所有文件，只给基本信息，让界面至少能显示「用默认应用打开」。</summary>
public sealed class FileInfoPreviewProvider : IPreviewProvider
{
    public string Name => "文件信息";

    public bool CanHandle(string path) => true;

    public Task<PreviewDocument> LoadAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        return Task.FromResult(new PreviewDocument(PreviewKind.None)
        {
            Notice = $"{PreviewRegistry.Format(info.Length)} · {info.LastWriteTime:yyyy-MM-dd HH:mm}",
            Error = "这种格式不能在这里预览，可以用默认应用打开",
        });
    }
}
