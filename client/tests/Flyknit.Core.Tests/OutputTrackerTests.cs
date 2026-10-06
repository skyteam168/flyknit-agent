using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

public class OutputTrackerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-outputs").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void NewFilesAreReportedAndUntouchedOnesAreNot()
    {
        Write("旧文件.txt", "原样不动");
        var tracker = OutputTracker.Snapshot(_dir)!;

        Write("报表.xlsx", "新生成的");
        var changed = tracker.Changed();

        Assert.Single(changed);
        Assert.Equal("报表.xlsx", changed[0].Name);
        Assert.Equal("xlsx", changed[0].Extension);
    }

    [Fact]
    public void RewritingAFileCountsAsAnOutput()
    {
        var path = Write("汇总.csv", "第一版");
        var tracker = OutputTracker.Snapshot(_dir)!;

        File.WriteAllText(path, "第二版，内容明显变长了");
        Assert.Single(tracker.Changed());
    }

    [Fact]
    public void BuildNoiseIsIgnored()
    {
        var tracker = OutputTracker.Snapshot(_dir)!;

        Write("node_modules/left-pad/index.js");
        Write("bin/Debug/app.dll");
        Write("obj/project.assets.json");
        Write(".git/HEAD");
        Write("__pycache__/x.pyc");
        Write("半截下载.tmp");
        Write("~$报表.xlsx");          // Office 打开时留下的锁文件
        Write("产出.pdf", "这个才是用户要的");

        var changed = tracker.Changed();
        Assert.Single(changed);
        Assert.Equal("产出.pdf", changed[0].Name);
    }

    [Fact]
    public void NestedOutputsAreFoundUpToTheDepthLimit()
    {
        var tracker = OutputTracker.Snapshot(_dir)!;
        Write(Path.Combine("一月", "第一周", "日报.docx"));

        Assert.Contains(tracker.Changed(), f => f.Name == "日报.docx");
    }

    [Fact]
    public void MissingDirectoryGivesNoTrackerInsteadOfThrowing()
    {
        Assert.Null(OutputTracker.Snapshot(Path.Combine(_dir, "不存在")));
        Assert.Null(OutputTracker.Snapshot(null));
        Assert.Null(OutputTracker.Snapshot("   "));
    }

    [Fact]
    public void DescribeDropsDuplicatesMissingFilesAndTempFiles()
    {
        var a = Write("a.txt");
        var tmp = Write("b.tmp");

        var files = OutputTracker.Describe(new[]
        {
            a, a.ToUpperInvariant(),            // 同一个文件，大小写不同
            tmp,                                 // 临时文件
            Path.Combine(_dir, "没有.txt"),      // 不存在
            "",                                  // 空字符串
        });

        Assert.Single(files);
        Assert.Equal("a.txt", files[0].Name);
    }

    [Fact]
    public void ReportIsCappedAndNewestFirst()
    {
        var tracker = OutputTracker.Snapshot(_dir)!;
        for (var i = 0; i < OutputTracker.MaxReported + 10; i++)
        {
            var path = Write($"文件{i:00}.txt");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
        }

        var changed = tracker.Changed();
        Assert.Equal(OutputTracker.MaxReported, changed.Count);
        Assert.True(changed[0].ModifiedAt >= changed[^1].ModifiedAt, "应按修改时间从新到旧");
    }
}
