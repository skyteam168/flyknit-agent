using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

public enum RiskLevel
{
    /// <summary>自动执行。</summary>
    Auto,

    /// <summary>需要用户确认。</summary>
    Confirm,

    /// <summary>直接阻止。</summary>
    Blocked,
}

public sealed record PolicyDecision(RiskLevel Level, string Reason)
{
    public static PolicyDecision Auto(string reason = "") => new(RiskLevel.Auto, reason);
    public static PolicyDecision Confirm(string reason = "") => new(RiskLevel.Confirm, reason);
    public static PolicyDecision Blocked(string reason) => new(RiskLevel.Blocked, reason);
}

/// <summary>服务端下发的策略，字段与 server/app/default_policy.py 一致。</summary>
public sealed class PolicyConfig
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("auto_run_readonly")] public bool AutoRunReadonly { get; set; } = true;
    [JsonPropertyName("blocked_patterns")] public List<string> BlockedPatterns { get; set; } = new();
    [JsonPropertyName("readonly_commands")] public List<string> ReadonlyCommands { get; set; } = new();
    [JsonPropertyName("writable_roots")] public List<string> WritableRoots { get; set; } = new();
    [JsonPropertyName("protected_roots")] public List<string> ProtectedRoots { get; set; } = new();
    [JsonPropertyName("batch_confirm_threshold")] public int BatchConfirmThreshold { get; set; } = 20;

    /// <summary>客户端内置的默认策略（离线或尚未从服务端拉取时使用）。</summary>
    public static PolicyConfig LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Flyknit.Core.default-policy.json")
                           ?? throw new InvalidOperationException("缺少内置默认策略");
        return JsonSerializer.Deserialize<PolicyConfig>(stream) ?? new PolicyConfig();
    }
}

/// <summary>
/// 命令与路径的安全策略：
/// 1. 命中禁止规则 → 直接阻止；
/// 2. 全部为只读查询命令且允许自动执行 → 自动；
/// 3. 其余 → 需要用户确认。
/// </summary>
public sealed class CommandPolicy
{
    private static readonly string[] ScriptExtensions = { ".ps1", ".psm1", ".bat", ".cmd", ".py", ".vbs", ".js" };
    private static readonly Regex Separators = new(@"\s*(?:;|&&|\|\||\||&|\r?\n)\s*", RegexOptions.Compiled);
    private static readonly Regex Redirection = new(@"(^|[^2])>{1,2}", RegexOptions.Compiled);
    private static readonly Regex ScriptToken = new(
        @"(?:""([^""]+\.(?:ps1|psm1|bat|cmd|py|vbs|js))""|(\S+\.(?:ps1|psm1|bat|cmd|py|vbs|js)))(?=\s|$|"")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly List<Regex> _blocked;
    private readonly HashSet<string> _readonly;
    private readonly List<string> _protectedRoots;

    public PolicyConfig Config { get; }

    public CommandPolicy(PolicyConfig config)
    {
        Config = config;
        _blocked = new List<Regex>();
        foreach (var pattern in config.BlockedPatterns)
        {
            try
            {
                _blocked.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)));
            }
            catch (ArgumentException)
            {
                // 无效正则：忽略该条，其余规则照常生效
            }
        }
        _readonly = new HashSet<string>(config.ReadonlyCommands, StringComparer.OrdinalIgnoreCase);
        _protectedRoots = config.ProtectedRoots.Select(NormalizeRoot).Where(r => r.Length > 0).ToList();
    }

    public static CommandPolicy Default() => new(PolicyConfig.LoadDefault());

    /// <summary>评估一条 Shell 命令。</summary>
    public PolicyDecision EvaluateCommand(string command, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return PolicyDecision.Blocked("命令为空");
        }
        var normalized = Regex.Replace(command, @"[ \t]+", " ").Trim();

        var hit = MatchBlocked(normalized);
        if (hit is not null)
        {
            return PolicyDecision.Blocked($"命中安全规则（{hit}），此类命令可能损害系统，已阻止");
        }

        // 调用脚本文件时，扫描脚本内容
        foreach (Match m in ScriptToken.Matches(normalized))
        {
            var path = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var full = ResolvePath(path, workingDirectory);
            if (full is not null && File.Exists(full))
            {
                var script = EvaluateScript(full);
                if (script.Level == RiskLevel.Blocked)
                {
                    return script;
                }
            }
        }

        if (Config.AutoRunReadonly && IsReadonly(normalized))
        {
            return PolicyDecision.Auto("只读查询命令");
        }
        return PolicyDecision.Confirm("需要用户确认后执行");
    }

    /// <summary>扫描脚本文件内容，任一行命中禁止规则则阻止。</summary>
    public PolicyDecision EvaluateScript(string path)
    {
        var ext = Path.GetExtension(path);
        if (!ScriptExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return PolicyDecision.Confirm();
        }
        string content;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 2 * 1024 * 1024)
            {
                return PolicyDecision.Blocked("脚本文件过大，无法完成安全检查");
            }
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return PolicyDecision.Blocked($"无法读取脚本进行安全检查：{ex.Message}");
        }

        var lineNo = 0;
        foreach (var raw in content.Split('\n'))
        {
            lineNo++;
            var line = Regex.Replace(raw, @"[ \t]+", " ").Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var hit = MatchBlocked(line);
            if (hit is not null)
            {
                return PolicyDecision.Blocked($"脚本 {Path.GetFileName(path)} 第 {lineNo} 行包含被禁止的操作（{hit}）");
            }
        }
        return PolicyDecision.Confirm("执行脚本需要用户确认");
    }

    /// <summary>评估写入、修改、删除某个路径。受保护目录直接阻止，其余需确认。</summary>
    public PolicyDecision EvaluateWrite(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch (Exception)
        {
            return PolicyDecision.Blocked("路径无效");
        }
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return PolicyDecision.Blocked("不允许直接修改磁盘根目录");
        }
        foreach (var p in _protectedRoots)
        {
            if (IsUnder(full, p))
            {
                return PolicyDecision.Blocked($"{p} 是受保护的系统目录，不允许修改");
            }
        }
        return PolicyDecision.Confirm("修改文件需要用户确认");
    }

    private string? MatchBlocked(string text)
    {
        foreach (var re in _blocked)
        {
            try
            {
                var m = re.Match(text);
                if (m.Success)
                {
                    return m.Value.Trim();
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return "规则匹配超时";
            }
        }
        return null;
    }

    private bool IsReadonly(string command)
    {
        if (Redirection.IsMatch(command))
        {
            return false; // 输出重定向会写文件
        }
        foreach (var segment in Separators.Split(command))
        {
            if (segment.Length == 0)
            {
                continue;
            }
            var first = segment.Split(' ', 2)[0].Trim('"', '\'');
            var name = Path.GetFileNameWithoutExtension(first);
            if (!_readonly.Contains(name))
            {
                return false;
            }
        }
        return true;
    }

    private static string? ResolvePath(string path, string? workingDirectory)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim('"', '\''));
            return Path.IsPathRooted(expanded) || workingDirectory is null
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(workingDirectory, expanded));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)).TrimEnd('\\', '/');
        }
        catch (Exception)
        {
            return "";
        }
    }

    internal static bool IsUnder(string fullPath, string root)
    {
        var a = fullPath.Replace('/', '\\').TrimEnd('\\');
        var b = root.Replace('/', '\\').TrimEnd('\\');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
               || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
