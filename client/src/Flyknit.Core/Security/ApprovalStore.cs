using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

public sealed class ApprovalEntry
{
    public string Key { get; set; } = "";
    public string Tool { get; set; } = "";

    /// <summary>给用户看的内容（命令原文或操作描述）。</summary>
    public string Display { get; set; } = "";
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;
    public int Uses { get; set; }
}

/// <summary>
/// 已授权的操作：用户允许过一次的同一条命令，以后直接执行，不再询问。
/// 只记录完全相同的命令（忽略多余空格和大小写）；危险命令在评估阶段就会被阻止，不会走到这里。
/// 保存在本机 approvals.json，可在设置中查看和撤销。
/// </summary>
public sealed class ApprovalStore
{
    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? _file;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<string, ApprovalEntry> _entries = new(StringComparer.Ordinal);

    /// <param name="file">保存位置；为 null 时只保存在内存中（测试用）。</param>
    public ApprovalStore(string? file = null)
    {
        _file = file;
        Load();
    }

    public bool IsApproved(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e))
            {
                return false;
            }
            e.Uses++;
            e.LastUsedAt = DateTimeOffset.Now;
        }
        Save();
        return true;
    }

    public void Approve(string key, string tool, string display)
    {
        lock (_gate)
        {
            _entries[key] = new ApprovalEntry { Key = key, Tool = tool, Display = display.Length > 500 ? display[..500] : display };
            if (_entries.Count > MaxEntries)
            {
                foreach (var old in _entries.Values.OrderBy(e => e.LastUsedAt).Take(_entries.Count - MaxEntries).ToList())
                {
                    _entries.Remove(old.Key);
                }
            }
        }
        Save();
    }

    public IReadOnlyList<ApprovalEntry> List()
    {
        lock (_gate)
        {
            return _entries.Values.OrderByDescending(e => e.LastUsedAt).ToList();
        }
    }

    public void Revoke(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
        Save();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
        Save();
    }

    /// <summary>
    /// 授权记录的键。命令类工具按“工具 + Shell + 规范化后的命令”区分；
    /// 其他工具按“工具 + 规范化后的参数”区分。
    /// </summary>
    public static string KeyFor(string toolName, JsonElement args)
    {
        if (toolName == "run_shell")
        {
            var shell = Str(args, "shell");
            shell = shell.Equals("cmd", StringComparison.OrdinalIgnoreCase) ? "cmd" : "powershell";
            return $"run_shell|{shell}|{NormalizeCommand(Str(args, "command"))}";
        }
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in args.EnumerateObject())
            {
                parts[p.Name] = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "").Trim() : p.Value.GetRawText();
            }
        }
        return $"{toolName}|{JsonSerializer.Serialize(parts)}".ToLowerInvariant();
    }

    public static string NormalizeCommand(string command) =>
        Regex.Replace(command, @"\s+", " ").Trim().ToLowerInvariant();

    private static string Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }
        try
        {
            var list = JsonSerializer.Deserialize<List<ApprovalEntry>>(File.ReadAllText(_file), Json) ?? new();
            foreach (var e in list.Where(e => e.Key.Length > 0))
            {
                _entries[e.Key] = e;
            }
        }
        catch (Exception)
        {
            // 文件损坏时从空白开始，最坏情况只是需要重新确认
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }
        try
        {
            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(_entries.Values.ToList(), Json);
            }
            lock (_saveGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _file, overwrite: true);
            }
        }
        catch (Exception)
        {
            // 保存失败不影响执行
        }
    }
}
