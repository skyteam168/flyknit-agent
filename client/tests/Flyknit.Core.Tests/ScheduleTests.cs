using Flyknit.Core.Scheduling;
using Xunit;

namespace Flyknit.Core.Tests;

public class ScheduleTests
{
    // 2026-10-06 是星期二
    private static readonly DateTimeOffset Tuesday0800 = new(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(8));

    private static string Fmt(DateTimeOffset? d) => d?.ToString("yyyy-MM-dd HH:mm ddd", System.Globalization.CultureInfo.InvariantCulture) ?? "(无)";

    [Fact]
    public void ManualTasksNeverRunOnTheirOwn()
    {
        Assert.Null(ScheduleSpec.Manual.NextRun(Tuesday0800));
    }

    [Fact]
    public void DailyRunsTodayIfTheTimeHasNotPassed()
    {
        var daily = new ScheduleSpec(ScheduleKind.Daily, Hour: 17, Minute: 30);
        Assert.Equal("2026-10-06 17:30 Tue", Fmt(daily.NextRun(Tuesday0800)));

        // 已经过了今天的时间点就排到明天
        var evening = Tuesday0800.AddHours(12);
        Assert.Equal("2026-10-07 17:30 Wed", Fmt(daily.NextRun(evening)));
    }

    [Fact]
    public void WeekdaysSkipsSaturdayAndSunday()
    {
        var spec = new ScheduleSpec(ScheduleKind.Weekdays, Hour: 8, Minute: 0);
        var fridayAfternoon = new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("2026-10-12 08:00 Mon", Fmt(spec.NextRun(fridayAfternoon)));

        var saturday = new DateTimeOffset(2026, 10, 10, 6, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("2026-10-12 08:00 Mon", Fmt(spec.NextRun(saturday)));
    }

    [Fact]
    public void WeeklyPicksTheRequestedWeekday()
    {
        var friday = new ScheduleSpec(ScheduleKind.Weekly, Hour: 16, Minute: 0, Weekday: DayOfWeek.Friday);
        Assert.Equal("2026-10-09 16:00 Fri", Fmt(friday.NextRun(Tuesday0800)));

        // 当天但时间已过 → 下一周
        var fridayEvening = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("2026-10-16 16:00 Fri", Fmt(friday.NextRun(fridayEvening)));
    }

    [Fact]
    public void MonthlyClampsToTheLastDayOfShortMonths()
    {
        var spec = new ScheduleSpec(ScheduleKind.Monthly, Hour: 9, Minute: 0, DayOfMonth: 31);
        var january = new DateTimeOffset(2026, 1, 31, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("2026-02-28 09:00 Sat", Fmt(spec.NextRun(january)));

        var february = new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal("2026-03-31 09:00 Tue", Fmt(spec.NextRun(february)));
    }

    [Fact]
    public void HourlyUsesTheMinuteOfEachHour()
    {
        var spec = new ScheduleSpec(ScheduleKind.Hourly, Minute: 15);
        Assert.Equal("2026-10-06 08:15 Tue", Fmt(spec.NextRun(Tuesday0800)));
        Assert.Equal("2026-10-06 09:15 Tue", Fmt(spec.NextRun(Tuesday0800.AddMinutes(20))));
    }

    [Fact]
    public void OnceOnlyRunsBeforeItsTime()
    {
        var at = Tuesday0800.AddHours(3);
        var spec = new ScheduleSpec(ScheduleKind.Once, At: at);
        Assert.Equal(at, spec.NextRun(Tuesday0800));
        Assert.Null(spec.NextRun(at));              // 到点之后不再排期
        Assert.Null(spec.NextRun(at.AddMinutes(1)));
    }

    [Fact]
    public void NextRunIsAlwaysInTheFuture()
    {
        var specs = new[]
        {
            new ScheduleSpec(ScheduleKind.Hourly, Minute: 0),
            new ScheduleSpec(ScheduleKind.Daily, Hour: 8),
            new ScheduleSpec(ScheduleKind.Weekdays, Hour: 8),
            new ScheduleSpec(ScheduleKind.Weekly, Hour: 8, Weekday: DayOfWeek.Sunday),
            new ScheduleSpec(ScheduleKind.Monthly, Hour: 8, DayOfMonth: 1),
        };
        // 一天里每 7 分钟取一个时刻，确保任何时候算出来的下次都严格在之后
        for (var minutes = 0; minutes < 60 * 24; minutes += 7)
        {
            var now = Tuesday0800.AddMinutes(minutes);
            foreach (var spec in specs)
            {
                var next = spec.NextRun(now);
                Assert.NotNull(next);
                Assert.True(next > now, $"{spec.Kind} 在 {now:O} 算出的下次是 {next:O}");
            }
        }
    }

    [Fact]
    public void SpecSurvivesSaveAndLoad()
    {
        var spec = new ScheduleSpec(ScheduleKind.Weekly, 16, 30, DayOfWeek.Friday, 1, null);
        Assert.Equal(spec, ScheduleSpec.Parse(spec.Serialize()));

        var once = new ScheduleSpec(ScheduleKind.Once, At: Tuesday0800);
        Assert.Equal(once.At, ScheduleSpec.Parse(once.Serialize()).At);

        // 损坏的内容退回手动，不会抛异常
        Assert.Equal(ScheduleKind.Manual, ScheduleSpec.Parse("{坏掉的").Kind);
        Assert.Equal(ScheduleKind.Manual, ScheduleSpec.Parse(null).Kind);
    }

    [Fact]
    public void RehearsalFlagIsAddedToOldDatabasesAndSurvivesSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flyknit-sched-{Guid.NewGuid():N}.db");
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection Open()
            {
                var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
                c.Open();
                return c;
            }
            // 旧版本的表没有 rehearsal 列
            using (var c = Open())
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE scheduled_tasks (id TEXT PRIMARY KEY, name TEXT NOT NULL DEFAULT '', instructions TEXT NOT NULL DEFAULT '',
                        schedule TEXT NOT NULL DEFAULT '', enabled INTEGER NOT NULL DEFAULT 1, workspace TEXT NULL,
                        permission TEXT NOT NULL DEFAULT 'workspace', model_id INTEGER NULL, catch_up INTEGER NOT NULL DEFAULT 1,
                        next_run_at TEXT NULL, last_run_at TEXT NULL, last_status TEXT NOT NULL DEFAULT '', last_summary TEXT NOT NULL DEFAULT '',
                        last_conversation_id TEXT NULL, run_count INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
                    INSERT INTO scheduled_tasks (id, name, created_at) VALUES ('old', '老任务', '2026-01-01T00:00:00+08:00');
                    """;
                cmd.ExecuteNonQuery();
                ScheduledTaskStore.Migrate(c);
                ScheduledTaskStore.Migrate(c); // 重复迁移不报错
            }

            var store = new ScheduledTaskStore(Open);
            Assert.False(store.Get("old")!.Rehearsal); // 老任务默认真的执行，行为不变

            var task = new ScheduledTask { Name = "预演任务", Instructions = "整理文件夹", Rehearsal = true };
            store.Save(task);
            Assert.True(store.Get(task.Id)!.Rehearsal);

            task.Rehearsal = false;
            store.Save(task);
            Assert.False(store.Get(task.Id)!.Rehearsal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
