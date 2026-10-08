using System.IO.Compression;
using System.Text;
using Flyknit.Core.Preview;
using Xunit;

namespace Flyknit.Core.Tests;

public class PreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-preview").FullName;
    private readonly PreviewRegistry _registry = PreviewRegistry.CreateDefault();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private string Write(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task TextFilesComeBackWithALanguageHint()
    {
        var doc = await _registry.LoadAsync(Write("a.py", "print('hi')\n"));
        Assert.Equal(PreviewKind.Text, doc.Kind);
        Assert.Equal("python", doc.Language);
        Assert.Contains("print", doc.Text);
    }

    [Fact]
    public async Task MarkdownAndDiagramsAreTaggedForTheUiToRender()
    {
        Assert.Equal(PreviewKind.Markdown, (await _registry.LoadAsync(Write("r.md", "# 标题"))).Kind);

        var diagram = await _registry.LoadAsync(Write("f.mmd", "graph TD; A-->B;"));
        Assert.Equal(PreviewKind.Diagram, diagram.Kind);
        Assert.Equal("mermaid", diagram.Language);
        Assert.Equal("dot", (await _registry.LoadAsync(Write("g.dot", "digraph{}"))).Language);
    }

    [Fact]
    public async Task HtmlPagesAreTaggedForTheUiToRender()
    {
        var page = await _registry.LoadAsync(Write("产品介绍页.html", "<!doctype html><h1>FlyknitBuddy</h1>"));
        Assert.Equal(PreviewKind.Html, page.Kind);
        Assert.Equal("html", page.Language);               // 切回源码时照样高亮
        Assert.Contains("<h1>FlyknitBuddy</h1>", page.Text);
        Assert.Equal(PreviewKind.Html, (await _registry.LoadAsync(Write("old.HTM", "<p>x</p>"))).Kind);

        // 太大只读了一部分：半张网页渲染出来是坏的，按源码显示
        var big = await _registry.LoadAsync(Write("big.html", "<p>" + new string('x', TextPreviewProvider.MaxChars + 10) + "</p>"));
        Assert.Equal(PreviewKind.Text, big.Kind);
        Assert.NotNull(big.Notice);
    }

    [Fact]
    public async Task CsvBecomesRowsAndQuotedCommasStayTogether()
    {
        var doc = await _registry.LoadAsync(Write("t.csv", "姓名,备注\n张三,\"北京, 海淀\"\n李四,\"他说\"\"好\"\"\"\n"));
        Assert.Equal(PreviewKind.Table, doc.Kind);
        var rows = doc.Sections[0].Rows!;
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "张三", "北京, 海淀" }, rows[1]);
        Assert.Equal("他说\"好\"", rows[2][1]);
    }

    [Fact]
    public async Task GbkTextIsDecodedInsteadOfTurningIntoGarbage()
    {
        // 工厂里不少老文件是 GBK 存的，按 UTF-8 硬读会变成乱码
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(_dir, "gbk.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding("GBK").GetBytes("车间排班表"));

        var doc = await _registry.LoadAsync(path);
        Assert.Equal(PreviewKind.Text, doc.Kind);
        Assert.DoesNotContain("�", doc.Text);  // 没有替换字符就说明没解错
    }

    [Fact]
    public async Task ImagesComeBackAsADataUrl()
    {
        var path = Path.Combine(_dir, "p.png");
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        var doc = await _registry.LoadAsync(path);
        Assert.Equal(PreviewKind.Image, doc.Kind);
        Assert.StartsWith("data:image/png;base64,", doc.DataUrl);
    }

    [Fact]
    public async Task ExcelSheetsKeepTheirNameAndEmptyCellsDoNotShiftColumns()
    {
        var path = Path.Combine(_dir, "book.xlsx");
        BuildXlsx(path);

        var doc = await _registry.LoadAsync(path);
        Assert.Equal(PreviewKind.Table, doc.Kind);
        Assert.Equal("九月产量", doc.Sections[0].Title);
        var rows = doc.Sections[0].Rows!;
        Assert.Equal(new[] { "车间", "产量" }, rows[0]);
        // B2 被省略了，补齐后 C2 必须还在第 3 列
        Assert.Equal(new[] { "一车间", "", "备注" }, rows[1]);
    }

    [Fact]
    public async Task PowerPointComesBackAsOneSectionPerSlideInOrder()
    {
        var path = Path.Combine(_dir, "deck.pptx");
        BuildPptx(path);

        var doc = await _registry.LoadAsync(path);
        Assert.Equal(PreviewKind.Sections, doc.Kind);
        Assert.Equal(2, doc.Sections.Count);
        // slide10 不能排在 slide2 前面
        Assert.Contains("第 2 页", doc.Sections[0].Title);
        Assert.Contains("第 10 页", doc.Sections[1].Title);
        Assert.Contains("营业部", doc.Sections[0].Title);
        Assert.Contains("第二行", doc.Sections[0].Text);
    }

    [Fact]
    public async Task WordHeadingsBecomeMarkdownHeadings()
    {
        var path = Path.Combine(_dir, "doc.docx");
        BuildDocx(path);

        var doc = await _registry.LoadAsync(path);
        Assert.Equal(PreviewKind.Markdown, doc.Kind);
        Assert.Contains("## 第一节", doc.Text);
        Assert.Contains("正文内容", doc.Text);
    }

    [Fact]
    public async Task WordTablesStayTablesInsteadOfCollapsingIntoOneLine()
    {
        var path = Path.Combine(_dir, "table.docx");
        BuildDocxWithTable(path);

        var doc = await _registry.LoadAsync(path);
        // Markdown 表格要有分隔行，界面才会渲染成表
        Assert.Contains("| 车间 | 产量 |", doc.Text);
        Assert.Contains("| --- | --- |", doc.Text);
        Assert.Contains("| 一车间 | 12480 |", doc.Text);
        // 表格里的文字不能又被当成正文重复输出一遍
        Assert.Equal(1, doc.Text!.Split("一车间").Length - 1);
    }

    [Fact]
    public async Task WordListsBecomeMarkdownBullets()
    {
        var path = Path.Combine(_dir, "list.docx");
        BuildDocxWithList(path);

        var doc = await _registry.LoadAsync(path);
        Assert.Contains("- 第一条", doc.Text);
        Assert.Contains("  - 子项", doc.Text);  // 第二级缩进
    }

    [Fact]
    public async Task SpeakerNotesComeAlongWithTheSlide()
    {
        var path = Path.Combine(_dir, "notes.pptx");
        BuildPptxWithNotes(path);

        var doc = await _registry.LoadAsync(path);
        Assert.Contains("【备注】记得强调交期", doc.Sections[0].Text);
    }

    [Fact]
    public async Task ZipShowsItsEntriesWithoutExtracting()
    {
        var path = Path.Combine(_dir, "a.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("里面/1.txt");
            zip.CreateEntry("里面/2.txt");
        }
        var doc = await _registry.LoadAsync(path);
        Assert.Equal(PreviewKind.Listing, doc.Kind);
        Assert.Equal(3, doc.Sections[0].Rows!.Count); // 表头 + 2 个条目
    }

    [Fact]
    public async Task UnknownFormatsFallBackToFileInfoInsteadOfFailing()
    {
        var path = Path.Combine(_dir, "x.dwg");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        var doc = await _registry.LoadAsync(path);
        Assert.Equal(PreviewKind.None, doc.Kind);
        Assert.NotNull(doc.Error);
        Assert.False(_registry.CanPreview(path));
        Assert.True(_registry.CanPreview(Path.Combine(_dir, "a.xlsx")));
    }

    [Fact]
    public async Task MissingFileSaysSoInsteadOfThrowing()
    {
        var doc = await _registry.LoadAsync(Path.Combine(_dir, "没有这个文件.txt"));
        Assert.Equal(PreviewKind.None, doc.Kind);
        Assert.Contains("不存在", doc.Error);
    }

    [Fact]
    public void ExcelColumnLettersMapToNumbers()
    {
        Assert.Equal(1, OfficePreviewProvider.ColumnIndex("A1"));
        Assert.Equal(2, OfficePreviewProvider.ColumnIndex("B7"));
        Assert.Equal(27, OfficePreviewProvider.ColumnIndex("AA3"));
        Assert.Equal(1, OfficePreviewProvider.ColumnIndex(""));
    }

    // ---------- 构造最小的 OOXML 文件 ----------

    private static void Add(ZipArchive zip, string name, string xml)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(xml);
    }

    private static void BuildXlsx(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "xl/workbook.xml",
            """<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheets><sheet name="九月产量" sheetId="1"/></sheets></workbook>""");
        Add(zip, "xl/sharedStrings.xml",
            """<?xml version="1.0"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>车间</t></si><si><t>产量</t></si><si><t>一车间</t></si><si><t>备注</t></si></sst>""");
        Add(zip, "xl/worksheets/sheet1.xml",
            """<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""" +
            """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>""" +
            """<row r="2"><c r="A2" t="s"><v>2</v></c><c r="C2" t="s"><v>3</v></c></row>""" +
            """</sheetData></worksheet>""");
    }

    private static void BuildPptx(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        const string ns = "http://schemas.openxmlformats.org/drawingml/2006/main";
        Add(zip, "ppt/slides/slide10.xml",
            $"""<?xml version="1.0"?><sld xmlns:a="{ns}"><a:p><a:r><a:t>最后一页</a:t></a:r></a:p></sld>""");
        Add(zip, "ppt/slides/slide2.xml",
            $"""<?xml version="1.0"?><sld xmlns:a="{ns}"><a:p><a:r><a:t>营业部</a:t></a:r><a:r><a:t> Q3</a:t></a:r></a:p><a:p><a:r><a:t>第二行</a:t></a:r></a:p></sld>""");
    }

    private static void BuildDocxWithTable(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Add(zip, "word/document.xml",
            $"""<?xml version="1.0"?><document xmlns:w="{ns}"><w:body>""" +
            """<w:p><w:r><w:t>下面是产量表：</w:t></w:r></w:p>""" +
            """<w:tbl>""" +
            """<w:tr><w:tc><w:p><w:r><w:t>车间</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>产量</w:t></w:r></w:p></w:tc></w:tr>""" +
            """<w:tr><w:tc><w:p><w:r><w:t>一车间</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>12480</w:t></w:r></w:p></w:tc></w:tr>""" +
            """</w:tbl>""" +
            """</w:body></document>""");
    }

    private static void BuildDocxWithList(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Add(zip, "word/document.xml",
            $"""<?xml version="1.0"?><document xmlns:w="{ns}"><w:body>""" +
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>第一条</w:t></w:r></w:p>""" +
            """<w:p><w:pPr><w:numPr><w:ilvl w:val="1"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>子项</w:t></w:r></w:p>""" +
            """</w:body></document>""");
    }

    private static void BuildPptxWithNotes(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        const string ns = "http://schemas.openxmlformats.org/drawingml/2006/main";
        Add(zip, "ppt/slides/slide1.xml",
            $"""<?xml version="1.0"?><sld xmlns:a="{ns}"><a:p><a:r><a:t>报价方案</a:t></a:r></a:p></sld>""");
        Add(zip, "ppt/notesSlides/notesSlide1.xml",
            $"""<?xml version="1.0"?><notes xmlns:a="{ns}"><a:p><a:r><a:t>记得强调交期</a:t></a:r></a:p><a:p><a:r><a:t>1</a:t></a:r></a:p></notes>""");
    }

    private static void BuildDocx(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Add(zip, "word/document.xml",
            $"""<?xml version="1.0"?><document xmlns:w="{ns}"><w:body>""" +
            """<w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>第一节</w:t></w:r></w:p>""" +
            """<w:p><w:r><w:t>正文内容</w:t></w:r></w:p>""" +
            """</w:body></document>""");
    }
}
