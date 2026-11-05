using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Flyknit.Core.Documents;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 文档画布：AI 用 edit_file 只改要改的一段（权限、备份、改动记录照旧），导出 Word 时按块写成合法的 docx。
/// </summary>
public sealed class DocumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flyknit-doc").FullName;
    private readonly string _work;
    private readonly FileBackup _backup;
    private readonly List<FileChange> _changes = new();

    public DocumentTests()
    {
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_work);
        _backup = new FileBackup(Path.Combine(_root, "backups"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private ToolContext Ctx(PermissionMode mode = PermissionMode.Workspace) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _work,
        Permission = mode,
        Backup = _backup,
        RecordChange = _changes.Add,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.Sandbox] = new() { Value = false, Locked = true },
        }),
    };

    private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();

    private string P(string name) => Path.Combine(_work, name);

    // ---------- edit_file ----------

    [Fact]
    public async Task EditFileChangesOnlyTheMatchedPassageAndKeepsABackup()
    {
        File.WriteAllText(P("稿.md"), "# 发言稿\n\n第一段。\n\n第二段：产量 90%。\n");

        var r = await new EditFileTool().ExecuteAsync(Args(new { path = "稿.md", old_text = "产量 90%", new_text = "产量 95%" }), Ctx(), default);

        Assert.True(r.Ok, r.Output);
        Assert.Equal("# 发言稿\n\n第一段。\n\n第二段：产量 95%。\n", File.ReadAllText(P("稿.md")));
        var change = Assert.Single(_changes);
        Assert.Equal(FileChange.Modified, change.Kind);
        Assert.Equal("edit_file", change.Source);
        Assert.Contains("产量 90%", File.ReadAllText(change.BackupPath!));
        Assert.Contains(P("稿.md"), r.Outputs);
    }

    [Fact]
    public async Task EditFileRefusesAmbiguousOrMissingTextInsteadOfGuessing()
    {
        File.WriteAllText(P("a.md"), "达标\n达标\n");
        var tool = new EditFileTool();

        var twice = await tool.ExecuteAsync(Args(new { path = "a.md", old_text = "达标", new_text = "未达标" }), Ctx(), default);
        var missing = await tool.ExecuteAsync(Args(new { path = "a.md", old_text = "不存在", new_text = "x" }), Ctx(), default);
        var noFile = await tool.ExecuteAsync(Args(new { path = "nope.md", old_text = "a", new_text = "b" }), Ctx(), default);

        Assert.False(twice.Ok);
        Assert.Contains("2 次", twice.Output);
        Assert.False(missing.Ok);
        Assert.Contains("read_file", missing.Output);
        Assert.False(noFile.Ok);
        Assert.Equal("达标\n达标\n", File.ReadAllText(P("a.md")));
        Assert.Empty(_changes);

        var all = await tool.ExecuteAsync(Args(new { path = "a.md", old_text = "达标", new_text = "合格", replace_all = true }), Ctx(), default);
        Assert.True(all.Ok);
        Assert.Equal("合格\n合格\n", File.ReadAllText(P("a.md")));
    }

    [Fact]
    public async Task EditFileMatchesWindowsLineEndingsWhenTheModelSendsPlainNewlines()
    {
        File.WriteAllText(P("w.md"), "一、背景\r\n现状如下\r\n二、措施\r\n");

        var r = await new EditFileTool().ExecuteAsync(Args(new { path = "w.md", old_text = "一、背景\n现状如下", new_text = "一、背景\n现状与问题" }), Ctx(), default);

        Assert.True(r.Ok, r.Output);
        Assert.Equal("一、背景\r\n现状与问题\r\n二、措施\r\n", File.ReadAllText(P("w.md")));
    }

    [Fact]
    public void EditFileNeedsTheSamePermissionAsWritingAndIsDroppedForReadOnlyExperts()
    {
        var args = Args(new { path = "x.md", old_text = "a", new_text = "b" });
        Assert.NotEqual(RiskLevel.Auto, new EditFileTool().Assess(args, Ctx(PermissionMode.ReadOnly)).Level);
        Assert.Equal(new WriteFileTool().Assess(Args(new { path = "x.md", content = "" }), Ctx()).Level,
            new EditFileTool().Assess(args, Ctx()).Level);
        Assert.Contains("edit_file", Flyknit.Core.Experts.ExpertRuntime.WriteTools);
        Assert.Contains("edit_file", Flyknit.Core.Agent.LoopGuard.TargetTools);
        Assert.NotNull(ToolRegistry.CreateDefault().Get("edit_file"));
    }

    // ---------- 画布保存 ----------

    [Fact]
    public void SavingFromTheCanvasRefusesToOverwriteChangesMadeInTheMeantime()
    {
        var path = P("纪要.md");
        File.WriteAllText(path, "# 纪要\n原稿");
        var opened = DocumentCanvas.Load(path);
        Assert.True(opened.Exists);
        Assert.Equal("# 纪要\n原稿", opened.Text);

        // 员工改着的时候，AI 用 edit_file 改了文件
        File.WriteAllText(path, "# 纪要\nAI 改的");
        var conflict = DocumentCanvas.Save(path, "# 纪要\n员工改的", opened.Version);

        Assert.False(conflict.Ok);
        Assert.True(conflict.Conflict);
        Assert.Equal("# 纪要\nAI 改的", conflict.CurrentText);
        Assert.Equal("# 纪要\nAI 改的", File.ReadAllText(path));

        // 员工看过后基于最新版本再存：成功，版本跟着变
        var saved = DocumentCanvas.Save(path, "# 纪要\n两边合在一起", conflict.Version);
        Assert.True(saved.Ok);
        Assert.Equal(DocumentCanvas.Version("# 纪要\n两边合在一起"), saved.Version);
        Assert.Equal("# 纪要\n两边合在一起", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".flyknit-tmp"));
    }

    [Fact]
    public void OnlyMarkdownOpensInTheCanvasAndDeletedFilesAreNotRecreatedSilently()
    {
        Assert.True(DocumentCanvas.IsDocument(@"D:\a\报告.MD"));
        Assert.True(DocumentCanvas.IsDocument("x.markdown"));
        Assert.False(DocumentCanvas.IsDocument("x.txt"));
        Assert.False(DocumentCanvas.IsDocument("x.docx"));

        var path = P("gone.md");
        File.WriteAllText(path, "a");
        var v = DocumentCanvas.Load(path).Version;
        File.Delete(path);
        var r = DocumentCanvas.Save(path, "b", v);
        Assert.False(r.Ok);
        Assert.True(r.Conflict);
        Assert.False(File.Exists(path));
        Assert.False(DocumentCanvas.Load(path).Exists);
    }

    // ---------- Word 导出 ----------

    private static DocModel Sample() => new()
    {
        Title = "CFT 发言稿 <草稿>",
        Blocks =
        {
            new() { Type = "heading", Level = 1, Runs = new() { new() { Text = "数据治理 & 落地" } } },
            new() { Type = "paragraph", Runs = new() { new() { Text = "统一接入：" , Bold = true }, new() { Text = "第一行\n第二行" }, new() { Text = "MES", Code = true } } },
            new() { Type = "list", Ordered = true, ListId = "L1", Runs = new() { new() { Text = "第一步" } } },
            new() { Type = "list", Ordered = true, ListId = "L1", Runs = new() { new() { Text = "第二步" } } },
            new() { Type = "list", Ordered = true, ListId = "L2", Runs = new() { new() { Text = "另一组第一步" } } },
            new() { Type = "list", ListId = "L3", Level = 1, Runs = new() { new() { Text = "要点" } } },
            new() { Type = "quote", Runs = new() { new() { Text = "一句话收尾", Italic = true } } },
            new() { Type = "code", Text = "SELECT 1;\nSELECT 2;" },
            new() { Type = "table", Header = true, Rows = new() {
                new() { new() { new() { Text = "车间" } }, new() { new() { Text = "产量" } } },
                new() { new() { new() { Text = "织造" } } },
            } },
            new() { Type = "hr" },
            new() { Type = "paragraph", Runs = new() { new() { Text = "控制字符\u0001被去掉\u000b" } } },
        },
    };

    private static Dictionary<string, XDocument> Open(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            return XDocument.Load(s); // 每个部件都必须是合法 XML，否则 Word 打不开
        });
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Fact]
    public void WordExportIsAValidPackageWithAllParts()
    {
        var parts = Open(DocxWriter.ToBytes(Sample()));

        Assert.Equal(
            new[] { "[Content_Types].xml", "_rels/.rels", "docProps/core.xml", "word/_rels/document.xml.rels", "word/document.xml", "word/numbering.xml", "word/styles.xml" },
            parts.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("CFT 发言稿 <草稿>", parts["docProps/core.xml"].Root!.Value);
    }

    [Fact]
    public void WordExportKeepsStructureAndFormatting()
    {
        var doc = Open(DocxWriter.ToBytes(Sample()))["word/document.xml"];
        var paras = doc.Descendants(W + "p").ToList();
        string StyleOf(XElement p) => p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        string TextOf(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value));

        var h1 = paras.First(p => StyleOf(p) == "Heading1");
        Assert.Equal("数据治理 & 落地", TextOf(h1));

        var body = paras.First(p => TextOf(p).StartsWith("统一接入"));
        Assert.NotNull(body.Descendants(W + "b").FirstOrDefault());
        Assert.Single(body.Descendants(W + "br"));                       // 段内换行
        Assert.Contains(body.Descendants(W + "rStyle"), s => s.Attribute(W + "val")?.Value == "CodeChar");

        // 同一个列表共用编号；另一个有序列表重新从 1 开始（不同的 numId）
        int NumId(string text) => int.Parse(paras.First(p => TextOf(p) == text).Descendants(W + "numId").Single().Attribute(W + "val")!.Value);
        Assert.Equal(NumId("第一步"), NumId("第二步"));
        Assert.NotEqual(NumId("第一步"), NumId("另一组第一步"));
        Assert.Equal("1", paras.First(p => TextOf(p) == "要点").Descendants(W + "ilvl").Single().Attribute(W + "val")!.Value);

        Assert.Equal("Quote", StyleOf(paras.First(p => TextOf(p) == "一句话收尾")));
        Assert.Equal(2, paras.Count(p => StyleOf(p) == "Code"));       // 代码按行
        Assert.Contains(paras, p => TextOf(p) == "控制字符被去掉");

        var table = doc.Descendants(W + "tbl").Single();
        var rows = table.Elements(W + "tr").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[1].Elements(W + "tc").Count());            // 缺的格子补齐
        Assert.NotNull(rows[0].Descendants(W + "b").FirstOrDefault());   // 表头加粗
    }

    [Fact]
    public void TheModelFromTheUiDeserializes()
    {
        const string json = """
            {"title":"t","blocks":[{"type":"heading","level":2,"runs":[{"text":"标题","b":true}]},
            {"type":"list","ordered":true,"listId":"x","level":0,"runs":[{"text":"一","link":"https://a"}]},
            {"type":"table","header":true,"rows":[[[{"text":"a"}],[{"text":"b"}]]]}]}
            """;
        var model = JsonSerializer.Deserialize<DocModel>(json)!;

        Assert.Equal(3, model.Blocks.Count);
        Assert.True(model.Blocks[0].Runs![0].Bold);
        Assert.Equal("https://a", model.Blocks[1].Runs![0].Link);
        Assert.Equal("b", model.Blocks[2].Rows![0][1][0].Text);
        Assert.NotEmpty(DocxWriter.ToBytes(model));
    }
}
