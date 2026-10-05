using System.Text.Json;

namespace Flyknit.Core.Scheduling;

public enum ScheduleKind
{
    /// <summary>只在用户点「运行」时执行。</summary>
    Manual,

    /// <summary>指定时刻执行一次，执行后自动停用。</summary>
    Once,

    Hourly,
    Daily,

    /// <summary>周一到周五。</summary>
    Weekdays,

    Weekly,
    Monthly,
}

/// <summary>
/// 定时规则。工厂都在同一个时区，按本机本地时间计算，不涉及夏令时。
/// </summary>
public sealed record ScheduleSpec(
    ScheduleKind Kind,
    int Hour = 9,
    int Minute = 0,
    /// <summary>每周：周几（周日 = 0）。</summary>
    DayOfWeek Weekday = DayOfWeek.Monday,
    /// <summary>每月：几号（1-31，超过当月天数时取当月最后一天）。</summary>
    int DayOfMonth = 1,
    /// <summary>仅一次：执行时刻。</summary>
    DateTimeOffset? At = null)
{
    public static ScheduleSpec Manual { get; } = new(ScheduleKind.Manual);

    public bool IsRecurring => Kind is not (ScheduleKind.Manual or ScheduleKind.Once);

    /// <summary>算出 after 之后的下一次执行时间；手动任务和已过期的一次性任务返回 null。</summary>
    public DateTimeOffset? NextRun(DateTimeOffset after)
    {
        var hour = Math.Clamp(Hour, 0, 23);
        var minute = Math.Clamp(Minute, 0, 59);

        switch (Kind)
        {
            case ScheduleKind.Manual:
                return null;

            case ScheduleKind.Once:
                return At > after ? At : null;

            case ScheduleKind.Hourly:
            {
                var slot = new DateTimeOffset(after.Year, after.Month, after.Day, after.Hour, minute, 0, after.Offset);
                return slot > after ? slot : slot.AddHours(1);
            }

            case ScheduleKind.Daily:
            {
                var slot = Slot(after, after.Date, hour, minute);
                return slot > after ? slot : slot.AddDays(1);
            }

            case ScheduleKind.Weekdays:
            {
                var slot = Slot(after, after.Date, hour, minute);
                if (slot <= after)
                {
                    slot = slot.AddDays(1);
                }
                while (slot.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    slot = slot.AddDays(1);
                }
                return slot;
            }

            case ScheduleKind.Weekly:
            {
                var slot = Slot(after, after.Date, hour, minute);
                var days = ((int)Weekday - (int)slot.DayOfWeek + 7) % 7;
                slot = slot.AddDays(days);
                return slot > after ? slot : slot.AddDays(7);
            }

            case ScheduleKind.Monthly:
            {
                var day = Math.Clamp(DayOfMonth, 1, 31);
                var slot = InMonth(after.Year, after.Month, day, hour, minute, after.Offset);
                if (slot > after)
                {
                    return slot;
                }
                var next = new DateTime(after.Year, after.Month, 1).AddMonths(1);
                return InMonth(next.Year, next.Month, day, hour, minute, after.Offset);
            }

            default:
                return null;
        }
    }

    private static DateTimeOffset Slot(DateTimeOffset reference, DateTime date, int hour, int minute) =>
        new(date.Year, date.Month, date.Day, hour, minute, 0, reference.Offset);

    private static DateTimeOffset InMonth(int year, int month, int day, int hour, int minute, TimeSpan offset) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), hour, minute, 0, offset);

    // ---------- 存库用的字符串形式 ----------

    public string Serialize() => JsonSerializer.Serialize(new Dto(
        Kind.ToString().ToLowerInvariant(), Hour, Minute, (int)Weekday, DayOfMonth, At?.ToString("O")));

    public static ScheduleSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Manual;
        }
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json);
            if (dto is null)
            {
                return Manual;
            }
            return new ScheduleSpec(
                Enum.TryParse<ScheduleKind>(dto.Kind, ignoreCase: true, out var k) ? k : ScheduleKind.Manual,
                dto.Hour,
                dto.Minute,
                (DayOfWeek)Math.Clamp(dto.Weekday, 0, 6),
                dto.DayOfMonth,
                DateTimeOffset.TryParse(dto.At, out var at) ? at : null);
        }
        catch (JsonException)
        {
            return Manual;
        }
    }

    private sealed record Dto(string Kind, int Hour, int Minute, int Weekday, int DayOfMonth, string? At);
}
