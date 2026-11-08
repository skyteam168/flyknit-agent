namespace Flyknit.Setup;

/// <summary>PATH 这种分号分隔的目录列表：加一项（已有就不加）、去掉一项。比较时忽略大小写和结尾的反斜杠。</summary>
public static class PathList
{
    public static string Add(string list, string dir)
    {
        var items = Split(list);
        if (items.Any(i => Same(i, dir)))
        {
            return list;
        }
        items.Add(dir);
        return string.Join(';', items);
    }

    public static string Remove(string list, string dir)
    {
        var items = Split(list);
        var kept = items.Where(i => !Same(i, dir)).ToList();
        return kept.Count == items.Count ? list : string.Join(';', kept);
    }

    private static List<string> Split(string list) =>
        list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
