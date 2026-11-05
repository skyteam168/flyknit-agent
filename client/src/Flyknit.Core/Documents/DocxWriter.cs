using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json.Serialization;

namespace Flyknit.Core.Documents;

/// <summary>
/// 把文档画布里的文档导出成 Word（.docx）。
/// Markdown 由界面那边用 markdown-it 解析好，拆成这里的几种块（标题、段落、列表、引用、代码、表格、分隔线）传过来：
/// 解析只做一遍、和界面显示的一致，这边只管按 OpenXML 写出来，不引入第三方库（只用 System.IO.Compression）。
/// </summary>
public static class DocxWriter
{
    public static void Write(Stream output, DocModel doc)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var lists = new ListNumbers();
        var body = new StringBuilder();
        foreach (var block in doc.Blocks)
        {
            WriteBlock(body, block, lists);
        }

        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", RootRels);
        Add(zip, "docProps/core.xml", Core(doc.Title));
        Add(zip, "word/_rels/document.xml.rels", DocumentRels);
        Add(zip, "word/styles.xml", Styles);
        Add(zip, "word/numbering.xml", lists.Xml());
        Add(zip, "word/document.xml",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {Ns}><w:body>{body}" +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>");
    }

    public static byte[] ToBytes(DocModel doc)
    {
        using var ms = new MemoryStream();
        Write(ms, doc);
        return ms.ToArray();
    }

    private static void WriteBlock(StringBuilder w, DocBlock b, ListNumbers lists)
    {
        switch (b.Type)
        {
            case "heading":
                Paragraph(w, $"Heading{Math.Clamp(b.Level, 1, 6)}", b.Runs);
                break;
            case "list":
                var num = lists.For(b.ListId, b.Ordered);
                var level = Math.Clamp(b.Level, 0, 8);
                w.Append("<w:p><w:pPr><w:pStyle w:val=\"ListParagraph\"/><w:numPr>")
                    .Append($"<w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{num}\"/></w:numPr></w:pPr>");
                Runs(w, b.Runs);
                w.Append("</w:p>");
                break;
            case "quote":
                Paragraph(w, "Quote", b.Runs);
                break;
            case "code":
                foreach (var line in (b.Text ?? "").Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                {
                    w.Append("<w:p><w:pPr><w:pStyle w:val=\"Code\"/></w:pPr>");
                    Text(w, line, "");
                    w.Append("</w:p>");
                }
                break;
            case "table":
                Table(w, b);
                break;
            case "hr":
                w.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"BFBFBF\"/></w:pBdr></w:pPr></w:p>");
                break;
            default:
                Paragraph(w, "", b.Runs);
                break;
        }
    }

    private static void Paragraph(StringBuilder w, string style, List<DocRun>? runs)
    {
        w.Append("<w:p>");
        if (style.Length > 0)
        {
            w.Append($"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>");
        }
        Runs(w, runs);
        w.Append("</w:p>");
    }

    private static void Table(StringBuilder w, DocBlock b)
    {
        var rows = b.Rows ?? new();
        if (rows.Count == 0)
        {
            return;
        }
        var cols = rows.Max(r => r.Count);
        const string border = "w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"";
        w.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>")
            .Append($"<w:top {border}/><w:left {border}/><w:bottom {border}/><w:right {border}/><w:insideH {border}/><w:insideV {border}/>")
            .Append("</w:tblBorders><w:tblCellMar><w:left w:w=\"100\" w:type=\"dxa\"/><w:right w:w=\"100\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
        for (var c = 0; c < cols; c++)
        {
            w.Append("<w:gridCol/>");
        }
        w.Append("</w:tblGrid>");
        for (var r = 0; r < rows.Count; r++)
        {
            var header = r == 0 && b.Header;
            w.Append("<w:tr>");
            if (header)
            {
                w.Append("<w:trPr><w:tblHeader/></w:trPr>");
            }
            for (var c = 0; c < cols; c++)
            {
                var cell = c < rows[r].Count ? rows[r][c] : new List<DocRun>();
                w.Append("<w:tc><w:tcPr>");
                if (header)
                {
                    w.Append("<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/>");
                }
                w.Append("</w:tcPr><w:p>");
                Runs(w, header ? cell.Select(x => x with { Bold = true }).ToList() : cell);
                w.Append("</w:p></w:tc>");
            }
            w.Append("</w:tr>");
        }
        w.Append("</w:tbl><w:p/>");
    }

    private static void Runs(StringBuilder w, List<DocRun>? runs)
    {
        foreach (var r in runs ?? new())
        {
            var props = new StringBuilder();
            if (r.Code)
            {
                props.Append("<w:rStyle w:val=\"CodeChar\"/>");
            }
            if (r.Link is { Length: > 0 })
            {
                props.Append("<w:color w:val=\"2F5496\"/><w:u w:val=\"single\"/>");
            }
            if (r.Bold)
            {
                props.Append("<w:b/>");
            }
            if (r.Italic)
            {
                props.Append("<w:i/>");
            }
            if (r.Strike)
            {
                props.Append("<w:strike/>");
            }
            Text(w, r.Text ?? "", props.ToString());
        }
    }

    /// <summary>一段文字：换行写成 w:br，前后空格要保留。</summary>
    private static void Text(StringBuilder w, string text, string props)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        w.Append("<w:r>");
        if (props.Length > 0)
        {
            w.Append("<w:rPr>").Append(props).Append("</w:rPr>");
        }
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                w.Append("<w:br/>");
            }
            w.Append("<w:t xml:space=\"preserve\">").Append(Escape(lines[i])).Append("</w:t>");
        }
        w.Append("</w:r>");
    }

    /// <summary>XML 转义，并去掉 XML 1.0 不允许的控制字符（复制粘贴进来的文字里偶尔会有）。</summary>
    internal static string Escape(string s)
    {
        var clean = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\t' || ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF)
            {
                clean.Append(ch);
            }
        }
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>每个列表一个编号实例：有序列表各自从 1 开始，不会接着上一个列表往下数。</summary>
    private sealed class ListNumbers
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly List<(int Id, bool Ordered)> _nums = new();

        public int For(string? listId, bool ordered)
        {
            var key = (listId ?? "") + (ordered ? ":o" : ":u");
            if (!_ids.TryGetValue(key, out var id))
            {
                id = _nums.Count + 1;
                _ids[key] = id;
                _nums.Add((id, ordered));
            }
            return id;
        }

        public string Xml()
        {
            var sb = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering {Ns}>");
            sb.Append(Abstract(1, ordered: false)).Append(Abstract(2, ordered: true));
            foreach (var (id, ordered) in _nums)
            {
                sb.Append($"<w:num w:numId=\"{id}\"><w:abstractNumId w:val=\"{(ordered ? 2 : 1)}\"/>");
                if (ordered)
                {
                    sb.Append("<w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride>");
                }
                sb.Append("</w:num>");
            }
            return sb.Append("</w:numbering>").ToString();
        }

        private static string Abstract(int id, bool ordered)
        {
            var sb = new StringBuilder($"<w:abstractNum w:abstractNumId=\"{id}\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
            string[] bullets = { "•", "◦", "▪" };
            string[] formats = { "decimal", "lowerLetter", "lowerRoman" };
            for (var lvl = 0; lvl < 9; lvl++)
            {
                var indent = 420 * (lvl + 1);
                var fmt = ordered ? formats[lvl % 3] : "bullet";
                var text = ordered ? $"%{lvl + 1}." : bullets[lvl % 3];
                sb.Append($"<w:lvl w:ilvl=\"{lvl}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{fmt}\"/><w:lvlText w:val=\"{text}\"/>")
                    .Append($"<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{indent}\" w:hanging=\"420\"/></w:pPr></w:lvl>");
            }
            return sb.Append("</w:abstractNum>").ToString();
        }
    }

    private const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
        "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
        "</Types>";

    private const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
        "</Relationships>";

    private const string DocumentRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>" +
        "</Relationships>";

    private static string Core(string title) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        $"<dc:title>{Escape(title)}</dc:title><dc:creator>FlyknitBuddy</dc:creator>" +
        $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created>" +
        "</cp:coreProperties>";

    // 正文用等线 / Calibri（中文、越南语都能显示）；标题、引用、代码各一种样式，Word 里能直接改样式统一调整
    private const string Fonts = "<w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"等线\" w:cs=\"Calibri\"/>";

    private static readonly string Styles =
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles {Ns}>" +
        $"<w:docDefaults><w:rPrDefault><w:rPr>{Fonts}<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"zh-CN\" w:eastAsia=\"zh-CN\"/></w:rPr></w:rPrDefault>" +
        "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"300\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        Heading(1, 36, "1F3864") + Heading(2, 30, "1F3864") + Heading(3, 26, "2F5496") +
        Heading(4, 24, "2F5496") + Heading(5, 22, "2F5496") + Heading(6, 22, "595959") +
        "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"60\"/></w:pPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/>" +
        "<w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"BFBFBF\"/></w:pBdr><w:ind w:left=\"360\"/></w:pPr><w:rPr><w:color w:val=\"595959\"/></w:rPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/>" +
        "<w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F5F5F5\"/><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>" +
        "<w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:eastAsia=\"等线\" w:cs=\"Consolas\"/><w:sz w:val=\"19\"/></w:rPr></w:style>" +
        "<w:style w:type=\"character\" w:styleId=\"CodeChar\"><w:name w:val=\"Code Char\"/>" +
        "<w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/></w:rPr></w:style>" +
        "</w:styles>";

    private static string Heading(int level, int size, string color) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
        $"<w:pPr><w:keepNext/><w:spacing w:before=\"{(level <= 2 ? 360 : 240)}\" w:after=\"120\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
        $"<w:rPr><w:b/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";
}

/// <summary>界面传过来的文档：标题 + 一串块。</summary>
public sealed class DocModel
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("blocks")] public List<DocBlock> Blocks { get; set; } = new();
}

/// <summary>
/// 一个块。type：heading / paragraph / list / quote / code / table / hr。
/// 列表每一项一个块，同一个列表的项 listId 相同（编号接着数），level 是缩进层级（0 起）。
/// </summary>
public sealed class DocBlock
{
    [JsonPropertyName("type")] public string Type { get; set; } = "paragraph";
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("ordered")] public bool Ordered { get; set; }
    [JsonPropertyName("listId")] public string? ListId { get; set; }
    [JsonPropertyName("runs")] public List<DocRun>? Runs { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("rows")] public List<List<List<DocRun>>>? Rows { get; set; }
    [JsonPropertyName("header")] public bool Header { get; set; }
}

/// <summary>一段同样格式的文字。</summary>
public sealed record DocRun
{
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("b")] public bool Bold { get; init; }
    [JsonPropertyName("i")] public bool Italic { get; init; }
    [JsonPropertyName("s")] public bool Strike { get; init; }
    [JsonPropertyName("code")] public bool Code { get; init; }
    [JsonPropertyName("link")] public string? Link { get; init; }
}
