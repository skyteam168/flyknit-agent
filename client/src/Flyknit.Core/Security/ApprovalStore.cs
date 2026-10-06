using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>
/// 一条「以后自动执行」规则。
/// 记的是命令前缀 + 适用范围，不是某一条一模一样的命令——
/// 用户允许 `npm run build` 之后，`npm run test` 也不用再问，但 `npm uninstall` 仍然会问，
/// 因为规则在匹配之前要先过一遍副作用分级，高危命令永远匹配不上任何规则。
/// </summary>
public sealed class ApprovalRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Tool { get; set; } = "";

    /// <summary>powershell / cmd；"*" 表示不限。</summary>
    public string Shell { get; set; } = "*";

    /// <summary>命令前缀，已规范化为小写，例如 "npm run"。</summary>
    public string Prefix { get; set; } = "";

    /// <summary>适用的工作区绝对路径；"*" 表示所有工作区。</summary>
    public string Scope { get; set; } = "*";

    /// <summary>给用户看的说明。</summary>
    public string Display { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;
    public int Uses { get; set; }
}

/// <summary>
/// 某个待确认操作「能不能被规则放行 / 能不能变成规则」。
/// 只有安全的命令才会带上它；删除、改系统、动态执行的命令拿不到，也就永远要人确认。
/// </summary>
public sealed record ApprovalCandidate(
    string Tool,
    string Shell,
    string Command,
    string Prefix,
    string Scope,
    string Display);

/// <summary>
/// 自动执行规则的本地存储（approval-rules.json），可在设置里查看和撤销。
/// </summary>
public sealed class ApprovalStore
{
    public const int MaxRules = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? _file;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly List<ApprovalRule> _rules = new();

    /// <param name="file">保存位置；为 null 时只保存在内存中（测试用）。</param>
    public ApprovalStore(string? file = null)
    {
        _file = file;
        Load();
    }

    /// <summary>这个待确认操作是否已被某条规则放行。</summary>
    public bool IsAllowed(ApprovalCandidate? candidate)
    {
        if (candidate is null || candidate.Prefix.Length == 0)
        {
            return false;
        }
        ApprovalRule? hit;
        lock (_gate)
        {
            hit = _rules.FirstOrDefault(r => Matches(r, candidate));
            if (hit is not null)
            {
                hit.Uses++;
                hit.LastUsedAt = DateTimeOffset.Now;
            }
        }
        if (hit is null)
        {
            return false;
        }
        Save();
        return true;
    }

    /// <summary>把这次确认变成一条规则。同样的前缀 + 范围只会存一条。</summary>
    public ApprovalRule Add(ApprovalCandidate candidate)
    {
        ApprovalRule rule;
        lock (_gate)
        {
            var existing = _rules.FirstOrDefault(r =>
                r.Tool == candidate.Tool &&
                r.Shell.Equals(candidate.Shell, StringComparison.OrdinalIgnoreCase) &&
                r.Prefix.Equals(candidate.Prefix, StringComparison.OrdinalIgnoreCase) &&
                ScopeEquals(r.Scope, candidate.Scope));
            if (existing is not null)
            {
                existing.LastUsedAt = DateTimeOffset.Now;
                rule = existing;
            }
            else
            {
                rule = new ApprovalRule
                {
                    Tool = candidate.Tool,
                    Shell = candidate.Shell,
                    Prefix = candidate.Prefix,
                    Scope = candidate.Scope,
                    Display = Trim(candidate.Display, 300),
                };
                _rules.Add(rule);
                if (_rules.Count > MaxRules)
                {
                    foreach (var old in _rules.OrderBy(r => r.LastUsedAt).Take(_rules.Count - MaxRules).ToList())
                    {
                        _rules.Remove(old);
                    }
                }
            }
        }
        Save();
        return rule;
    }

    public IReadOnlyList<ApprovalRule> List()
    {
        lock (_gate)
        {
            return _rules.OrderByDescending(r => r.LastUsedAt).ToList();
        }
    }

    public void Revoke(string id)
    {
        lock (_gate)
        {
            _rules.RemoveAll(r => r.Id == id);
        }
        Save();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
        Save();
    }

    private static bool Matches(ApprovalRule rule, ApprovalCandidate c)
    {
        if (rule.Tool != c.Tool)
        {
            return false;
        }
        if (rule.Shell != "*" && !rule.Shell.Equals(c.Shell, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!ScopeEquals(rule.Scope, c.Scope) && rule.Scope != "*")
        {
            return false;
        }
        return StartsWithToken(Normalize(c.Command), rule.Prefix);
    }

    /// <summary>前缀必须落在词边界上：`npm run` 匹配 `npm run build`，但不匹配 `npm runaway`。</summary>
    public static bool StartsWithToken(string command, string prefix)
    {
        if (prefix.Length == 0 || command.Length < prefix.Length)
        {
            return false;
        }
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return command.Length == prefix.Length || command[prefix.Length] is ' ' or '\t';
    }

    public static string Normalize(string command) =>
        Regex.Replace(command ?? "", @"\s+", " ").Trim().ToLowerInvariant();

    private static bool ScopeEquals(string a, string b) =>
        a.TrimEnd('\\', '/').Equals(b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }
        try
        {
            var list = JsonSerializer.Deserialize<List<ApprovalRule>>(File.ReadAllText(_file), Json) ?? new();
            _rules.AddRange(list.Where(r => r.Prefix.Length > 0));
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
                json = JsonSerializer.Serialize(_rules, Json);
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
