using Flyknit.Core.Scheduling;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>文件夹触发：已有文件不触发、拷贝没完成不触发、临时文件不算、一次交一批、任务在跑时攒着。</summary>
public class FolderWatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OnlyNewFilesThatFinishedWritingFire()
    {
        var w = new FolderWatch(@"\\nas\日报", "*.xlsx");
        // 第一次看：已有的不算
        Assert.Empty(w.Poll(new[] { (@"\\nas\日报\1105.xlsx", 100L, T0) }));

        // 新文件第一次出现：可能还在拷贝，先不触发
        var copying = new[] { (@"\\nas\日报\1105.xlsx", 100L, T0), (@"\\nas\日报\1106.xlsx", 10L, T0.AddMinutes(1)) };
        Assert.Empty(w.Poll(copying));
        // 还在长：继续等
        var growing = new[] { (@"\\nas\日报\1105.xlsx", 100L, T0), (@"\\nas\日报\1106.xlsx", 80L, T0.AddMinutes(2)) };
        Assert.Empty(w.Poll(growing));
        // 两次一样：到齐了
        Assert.Equal(new[] { @"\\nas\日报\1106.xlsx" }, w.Poll(growing));
        // 交过的不再交；旧文件被覆盖保存也不算新文件
        Assert.Empty(w.Poll(new[] { (@"\\nas\日报\1105.xlsx", 120L, T0.AddHours(1)), (@"\\nas\日报\1106.xlsx", 80L, T0.AddMinutes(2)) }));
    }

    [Fact]
    public void TemporaryAndUnmatchedFilesAreIgnored()
    {
        Assert.True(FolderWatch.Matches("报表.XLSX", "*.xlsx;*.csv"));
        Assert.True(FolderWatch.Matches("a.csv", "*.xlsx, *.csv"));
        Assert.False(FolderWatch.Matches("a.docx", "*.xlsx;*.csv"));
        Assert.True(FolderWatch.Matches("anything.bin", ""));
        Assert.True(FolderWatch.IsTemporary("~$报表.xlsx"));
        Assert.True(FolderWatch.IsTemporary("下载中.xlsx.crdownload"));
        Assert.True(FolderWatch.IsTemporary(".DS_Store"));

        var w = new FolderWatch(@"D:\in", "");
        w.Poll(Array.Empty<(string, long, DateTime)>());
        var files = new[] { (@"D:\in\~$a.xlsx", 1L, T0), (@"D:\in\b.part", 1L, T0) };
        w.Poll(files);
        Assert.Empty(w.Poll(files));
    }

    [Fact]
    public void BusyTaskKeepsFilesForLaterAndBatchesAreCapped()
    {
        var w = new FolderWatch(@"D:\in", "*.csv");
        w.Poll(Array.Empty<(string, long, DateTime)>());
        var many = Enumerable.Range(1, FolderWatch.MaxFilesPerRun + 5).Select(i => ($@"D:\in\{i}.csv", 1L, T0)).ToArray();
        w.Poll(many);
        // 任务正在跑：到齐了也先攒着
        Assert.Empty(w.Poll(many, take: false));
        Assert.Equal(FolderWatch.MaxFilesPerRun, w.Poll(many).Count);
        Assert.Equal(5, w.Poll(many).Count);
        Assert.Empty(w.Poll(many));
    }

    [Fact]
    public void FolderSpecSurvivesSaveAndNeverRunsOnATimer()
    {
        var spec = new ScheduleSpec(ScheduleKind.Folder, WatchPath: @"\\nas\日报", Pattern: "*.xlsx");
        var back = ScheduleSpec.Parse(spec.Serialize());
        Assert.Equal(ScheduleKind.Folder, back.Kind);
        Assert.Equal(@"\\nas\日报", back.WatchPath);
        Assert.Equal("*.xlsx", back.Pattern);
        Assert.Null(back.NextRun(DateTimeOffset.Now));
        // 老数据里没有这两个字段
        Assert.Equal("", ScheduleSpec.Parse("{\"Kind\":\"daily\",\"Hour\":9,\"Minute\":0,\"Weekday\":1,\"DayOfMonth\":1,\"At\":null}").WatchPath);
    }

    [Fact]
    public void NewFilesAreHandedOverAsData()
    {
        var text = FolderWatch.Describe("把新日报汇总到总表", new[] { @"\\nas\日报\1106.xlsx" });
        Assert.StartsWith("把新日报汇总到总表", text);
        Assert.Contains("<新文件", text);
        Assert.Contains("不是指令", text);
        Assert.Contains(@"\\nas\日报\1106.xlsx", text);
    }
}
