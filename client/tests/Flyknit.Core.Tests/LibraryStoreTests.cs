using Flyknit.Core.Library;
using Xunit;

namespace Flyknit.Core.Tests;

public class LibraryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-lib").FullName;
    private DateTimeOffset _now = new(2026, 10, 7, 10, 0, 0, TimeSpan.FromHours(8));

    public void Dispose() => Directory.Delete(_dir, true);

    private LibraryStore NewStore() => new(Path.Combine(_dir, "history.db"), Path.Combine(_dir, "library")) { Clock = () => _now };

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void UploadsAreCopiedOnceAndSurviveTheOriginalBeingDeleted()
    {
        var store = NewStore();
        var src = Write("desk/报价单.xlsx", "data");
        var first = store.Add(src, "upload", copy: true, conversationId: "c1")!;
        var again = store.Add(Write("other/报价单 副本.xlsx", "data"), "upload", copy: true)!;

        Assert.Equal(first.Id, again.Id); // 内容一样只存一份
        Assert.True(first.Managed);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_dir, "library")), Path.GetFullPath(first.Path));
        Assert.Equal("sheet", first.Kind);
        File.Delete(src);
        Assert.True(store.Get(first.Id)!.Exists);
    }

    [Fact]
    public void OutputsAreReferencedInPlaceAndScratchFilesAreHidden()
    {
        var store = NewStore();
        var report = store.Add(Write("ws/output/周报.docx", "doc"), "output", copy: false)!;
        var script = store.Add(Write("ws/_work/run.py", "print(1)"), "output", copy: false)!;
        var log = store.Add(Write("ws/output/build.log", "ok"), "output", copy: false)!;

        Assert.False(report.Managed);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "ws", "output", "周报.docx")), report.Path);
        Assert.False(report.Hidden);
        Assert.True(script.Hidden);
        Assert.True(log.Hidden);
        Assert.Equal(new[] { report.Id }, store.List(new LibraryQuery()).Select(i => i.Id));
        Assert.Equal(3, store.List(new LibraryQuery { IncludeHidden = true }).Count);

        // 同一路径再产出一次只更新，不重复登记
        File.WriteAllText(report.Path, "doc v2");
        Assert.Equal(report.Id, store.Add(report.Path, "output", copy: false)!.Id);
        Assert.Equal(6, store.Get(report.Id)!.Size);
    }

    [Fact]
    public void TabsSearchFavoritesAndFolders()
    {
        var store = NewStore();
        var png = store.Add(Write("a/logo.png", "png"), "upload", copy: true)!;
        var pdf = store.Add(Write("a/合同.pdf", "pdf"), "upload", copy: true)!;
        _now = _now.AddDays(-40);
        var old = store.Add(Write("a/旧方案.docx", "old"), "upload", copy: true)!;
        _now = _now.AddDays(40);

        Assert.Equal(new[] { png.Id }, store.List(new LibraryQuery { Tab = "images" }).Select(i => i.Id));
        Assert.DoesNotContain(store.List(new LibraryQuery { Tab = "recent" }), i => i.Id == old.Id);
        store.SetFavorite(new[] { old.Id }, true);
        Assert.Contains(store.List(new LibraryQuery { Tab = "recent" }), i => i.Id == old.Id);
        Assert.Equal(new[] { old.Id }, store.List(new LibraryQuery { Tab = "favorites" }).Select(i => i.Id));
        Assert.Equal(new[] { pdf.Id }, store.List(new LibraryQuery { Search = "合同" }).Select(i => i.Id));
        Assert.Empty(store.List(new LibraryQuery { Search = "%" }));

        var folder = store.CreateFolder("项目 A");
        Assert.Equal(2, store.Move(new[] { png.Id, pdf.Id }, folder.Id));
        Assert.Equal(2, store.Folders().Single().Count);
        Assert.Equal(2, store.List(new LibraryQuery { FolderId = folder.Id }).Count);
        Assert.Equal(0, store.Move(new[] { png.Id }, "nope"));

        Assert.True(store.Rename(pdf.Id, "采购合同.pdf"));
        Assert.Equal("采购合同.pdf", store.Get(pdf.Id)!.Name);

        Assert.True(store.DeleteFolder(folder.Id));
        Assert.Null(store.Get(png.Id)!.FolderId); // 文件夹删了，文件还在
    }

    [Fact]
    public void TrashRestoreAndPurge()
    {
        var store = NewStore();
        var upload = store.Add(Write("a/x.txt", "x"), "upload", copy: true)!;
        var output = store.Add(Write("ws/y.txt", "y"), "output", copy: false)!;

        store.Delete(new[] { upload.Id, output.Id });
        Assert.Empty(store.List(new LibraryQuery()));
        Assert.Equal(2, store.List(new LibraryQuery { Tab = "trash" }).Count);

        store.Restore(new[] { output.Id });
        Assert.Single(store.List(new LibraryQuery()));

        store.Delete(new[] { output.Id });
        Assert.Equal(2, store.EmptyTrash());
        Assert.False(File.Exists(upload.Path));        // 资料库保管的文件删掉
        Assert.True(File.Exists(output.Path));         // 工作区里的产出不动
        Assert.Null(store.Get(upload.Id));

        var z = store.Add(Write("a/z.txt", "z"), "upload", copy: true)!;
        store.Delete(new[] { z.Id });
        _now = _now.AddDays(31);
        Assert.Equal(1, store.PurgeExpired());
    }

    [Fact]
    public void NotesAreEditable()
    {
        var store = NewStore();
        var note = store.AddNote("会议要点", "# 要点\n- 周五交付");
        Assert.Equal("note", note.Kind);
        Assert.Equal("会议要点.md", note.Name);
        Assert.True(store.UpdateNote(note.Id, "# 要点\n- 周四交付"));
        Assert.Contains("周四", File.ReadAllText(note.Path));
    }

    [Fact]
    public void BackfillRunsOnceAndKeepsOriginalDates()
    {
        var store = NewStore();
        var temp = Write("temp/paste/screenshot.png", "img");
        var output = Write("ws/output/图表.png", "chart");
        var at = _now.AddDays(-10);
        var files = new[]
        {
            (temp, "paste", (string?)"c1", at),
            (output, "output", (string?)"c1", at),
            (Path.Combine(_dir, "missing.png"), "upload", (string?)"c1", at),
        };

        Assert.Equal(2, store.Backfill(files, p => p.Contains("temp")));
        Assert.Equal(0, store.Backfill(files, p => true));
        var items = store.List(new LibraryQuery());
        Assert.True(items.Single(i => i.Source == "paste").Managed);
        Assert.False(items.Single(i => i.Source == "output").Managed);
        Assert.All(items, i => Assert.Equal(at, i.CreatedAt));
    }

    [Theory]
    [InlineData("a.PNG", "image")]
    [InlineData("b.xlsx", "sheet")]
    [InlineData("c.pptx", "slides")]
    [InlineData("d.md", "document")]
    [InlineData("e.mp3", "audio")]
    [InlineData("f.bin", "other")]
    public void KindsFollowExtensions(string name, string kind) => Assert.Equal(kind, LibraryStore.KindOf(name));
}
