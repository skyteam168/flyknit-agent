using System.Text.RegularExpressions;

namespace Flyknit.Core.Memory;

/// <summary>
/// 从一句话里认出时间范围：“昨天做的报表”“上周的周报”“九月那次安装”。
/// 找历史任务和记忆时，落在这个范围里的排前面。只认常见说法，认不出就当没说。
/// </summary>
public sealed record TimeRange(DateTime From, DateTime To, string Phrase)
{
    private static readonly string[] Numerals = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十", "十一", "十二" };

    private static readonly Regex Pattern = new(
        @"大前天|前天|昨天|昨日|今天|今日|上上周|上周|上星期|上个星期|本周|这周|这个星期|这星期|上个月|上月|本月|这个月|这月|今年|去年|" +
        @"(?:最近|近|过去)\s*(?<n>\d+|[一二三四五六七八九十两]+)\s*(?<u>天|日|周|个星期|星期|个月|月)|" +
        @"(?<n2>\d+|[一二三四五六七八九十两]+)\s*(?<u2>天|周|个星期|个月)前|" +
        @"(?<![\d统唯单同第每逐])(?<m>1[0-2]|0?[1-9]|十[一二]?|[一二三四五六七八九])\s*月份?(?:\s*(?<d>[0-3]?\d)\s*[日号])?",
        RegexOptions.Compiled);

    public static TimeRange? Parse(string? text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var m = Pattern.Match(text);
        if (!m.Success)
        {
            return null;
        }
        var today = now.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        TimeRange Days(DateTime from, int days) => new(from, from.AddDays(days), m.Value);

        switch (m.Value)
        {
            case "今天" or "今日": return Days(today, 1);
            case "昨天" or "昨日": return Days(today.AddDays(-1), 1);
            case "前天": return Days(today.AddDays(-2), 1);
            case "大前天": return Days(today.AddDays(-3), 1);
            case "本周" or "这周" or "这个星期" or "这星期": return Days(monday, 7);
            case "上周" or "上星期" or "上个星期": return Days(monday.AddDays(-7), 7);
            case "上上周": return Days(monday.AddDays(-14), 7);
            case "本月" or "这个月" or "这月": return new(month, month.AddMonths(1), m.Value);
            case "上个月" or "上月": return new(month.AddMonths(-1), month, m.Value);
            case "今年": return new(new DateTime(today.Year, 1, 1), new DateTime(today.Year + 1, 1, 1), m.Value);
            case "去年": return new(new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year, 1, 1), m.Value);
        }

        if (m.Groups["n"].Success || m.Groups["n2"].Success)
        {
            var n = Number(m.Groups["n"].Success ? m.Groups["n"].Value : m.Groups["n2"].Value);
            var unit = m.Groups["u"].Success ? m.Groups["u"].Value : m.Groups["u2"].Value;
            if (n is null or <= 0 or > 3650)
            {
                return null;
            }
            var from = unit.Contains('月') ? today.AddMonths(-n.Value) : today.AddDays(-n.Value * (unit.Contains('周') || unit.Contains("星期") ? 7 : 1));
            // “最近 3 天”到今天为止；“3 天前”是那一天前后
            return m.Groups["n"].Success
                ? new TimeRange(from, today.AddDays(1), m.Value)
                : new TimeRange(from.AddDays(-1), from.AddDays(2), m.Value);
        }

        var mon = Number(m.Groups["m"].Value);
        if (mon is null or < 1 or > 12)
        {
            return null;
        }
        // 说“九月”一般指最近过去的那个九月：还没到的月份算去年
        var year = mon.Value > today.Month ? today.Year - 1 : today.Year;
        if (m.Groups["d"].Success && int.TryParse(m.Groups["d"].Value, out var day) && day >= 1 && day <= DateTime.DaysInMonth(year, mon.Value))
        {
            var date = new DateTime(year, mon.Value, day);
            return new TimeRange(date, date.AddDays(1), m.Value);
        }
        var start = new DateTime(year, mon.Value, 1);
        return new TimeRange(start, start.AddMonths(1), m.Value);
    }

    public bool Contains(DateTime time) => time >= From && time < To;

    /// <summary>去掉时间说法后的部分，用来算内容相关度（不然“上周”这两个字也会参与匹配）。</summary>
    public string Strip(string text) => text.Replace(Phrase, " ").Trim();

    private static readonly Regex Filler = new(@"帮我|给我|我们|我|你|看看|看一下|一下|查一下|找一下|回忆|记得|做了|做过|干了|弄了|处理了|完成了|都|有|哪些|什么|那个|那次|那些|任务|事情|事儿|事|工作|的|了|吗|呢|吧|[\s,，。.？?！!、]", RegexOptions.Compiled);

    /// <summary>去掉时间和“做了什么”这类虚词后，话里还有没有具体内容。没有就是在问“那段时间做了什么”。</summary>
    public bool IsTimeOnly(string text) => Filler.Replace(Strip(text), "").Length < 2;

    private static int? Number(string s)
    {
        if (int.TryParse(s, out var n))
        {
            return n;
        }
        s = s.Replace("两", "二");
        var index = Array.IndexOf(Numerals, s);
        if (index >= 0)
        {
            return index;
        }
        // 二十、三十五 这类
        var ten = s.IndexOf('十');
        if (ten >= 0)
        {
            var tens = ten == 0 ? 1 : Array.IndexOf(Numerals, s[..ten]);
            var ones = ten == s.Length - 1 ? 0 : Array.IndexOf(Numerals, s[(ten + 1)..]);
            return tens > 0 && ones >= 0 ? tens * 10 + ones : null;
        }
        return null;
    }
}
