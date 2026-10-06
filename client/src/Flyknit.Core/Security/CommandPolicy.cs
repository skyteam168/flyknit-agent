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
    /// <summary>这条命令对电脑做了什么（只读 / 写入 / 高危 / 认不出）。</summary>
    public CommandEffect Effect { get; init; } = CommandEffect.Unknown;

    /// <summary>
    /// 能被「以后自动执行」规则放行、也能变成规则的候选。
    /// 只有安全的命令才有；删除、改系统设置、动态执行的命令拿不到，所以每次都要人确认。
    /// </summary>
    public ApprovalCandidate? Rule { get; init; }

    /// <summary>需要确认时，用户能否选「以后同类命令自动执行」。</summary>
    public bool Rememberable => Rule is not null;

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

    /// <summary>
    /// 递归强制删除类命令。命中只表示「要看目标」，不等于拦截——
    /// 删 node_modules 和删 C:\ 是两回事，拿动词一刀切太粗。
    /// </summary>
    [JsonPropertyName("recursive_delete_patterns")] public List<string> RecursiveDeletePatterns { get; set; } = new();
    [JsonPropertyName("readonly_commands")] public List<string> ReadonlyCommands { get; set; } = new();
    [JsonPropertyName("writable_roots")] public List<string> WritableRoots { get; set; } = new();
    [JsonPropertyName("protected_roots")] public List<string> ProtectedRoots { get; set; } = new();

    /// <summary>不许被整个删掉的目录。比 protected_roots 宽：这里的目录可以写，只是不能整个删。</summary>
    [JsonPropertyName("no_delete_roots")] public List<string> NoDeleteRoots { get; set; } = new();
    [JsonPropertyName("batch_confirm_threshold")] public int BatchConfirmThreshold { get; set; } = 20;

    /// <summary>网络白名单。写 example.com 同时放行它的子域名，写 * 等于不限制。</summary>
    [JsonPropertyName("allowed_domains")] public List<string> AllowedDomains { get; set; } = new();

    /// <summary>内网地址和不带点的内网主机名是否一律放行。</summary>
    [JsonPropertyName("allow_private_network")] public bool AllowPrivateNetwork { get; set; } = true;

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
    private readonly List<Regex> _recursiveDelete;
    private readonly HashSet<string> _readonly;
    private readonly List<string> _protectedRoots;

    public PolicyConfig Config { get; }

    /// <summary>网络白名单。是否生效由安全中心的开关决定，见 <see cref="PermissionRules.ForNetwork"/>。</summary>
    public NetworkPolicy Network { get; }

    public CommandPolicy(PolicyConfig config)
    {
        Config = config;
        Network = new NetworkPolicy(config.AllowedDomains, config.AllowPrivateNetwork);
        _blocked = Compile(config.BlockedPatterns);
        _recursiveDelete = Compile(config.RecursiveDeletePatterns);
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

        // 递归删除：看目标，不看动词
        var sweep = CheckRecursiveDelete(normalized, workingDirectory);
        if (sweep is not null)
        {
            return sweep;
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

        // 按副作用分级：只读 / 写入 / 高危 / 认不出
        var analysis = CommandAnalyzer.Analyze(normalized);
        if (analysis.HasIndirection)
        {
            return PolicyDecision.Confirm(analysis.IndirectionReason ?? "命令内容无法事先判断，需要确认")
                with { Effect = CommandEffect.Unknown };
        }
        // 管理员下发的 readonly_commands 可以把更多命令算成只读
        var readOnly = analysis.Effect == CommandEffect.Read || IsReadonly(normalized);
        if (readOnly)
        {
            return Config.AutoRunReadonly
                ? PolicyDecision.Auto(analysis.Reason) with { Effect = CommandEffect.Read }
                : PolicyDecision.Confirm("管理员关闭了只读命令自动执行") with { Effect = CommandEffect.Read };
        }
        return PolicyDecision.Confirm(analysis.Reason) with { Effect = analysis.Effect };
    }

    /// <summary>
    /// 递归强制删除的目标够不够安全。
    ///
    /// 返回 null 表示「这条不是递归删除，或者目标没问题」，交给后面的分级继续判
    /// （最终会落到 Destructive → 需要确认）。返回 Blocked 表示目标不能碰。
    /// </summary>
    private PolicyDecision? CheckRecursiveDelete(string command, string? workingDirectory)
    {
        if (!_recursiveDelete.Any(re => SafeMatch(re, command)))
        {
            return null;
        }

        // 命令文本里直接出现了不许删的目录名：先拦掉。
        // 这一条是为「没加引号的带空格路径」兜底——C:\Program Files 会被空格切开，
        // 切开之后反而看不出它是什么。只会让判断更严，不会放松。
        foreach (var root in Config.NoDeleteRoots)
        {
            if (root.Length > 3 && MentionsWholePath(command, root))
            {
                return PolicyDecision.Blocked($"不能递归删除 {root}：这是系统关键目录，不能整个删除");
            }
        }

        var raw = PathResolver.TargetsOf(command);
        if (raw.Count == 0)
        {
            // 认得出是递归删除，却没给目标：证不了它安全
            return PolicyDecision.Blocked("这是一条递归强制删除命令，但看不出要删什么，已阻止。请写明确的路径");
        }

        foreach (var target in raw.Select(t => PathResolver.Resolve(t, workingDirectory)))
        {
            if (!target.Known)
            {
                return PolicyDecision.Blocked(
                    $"递归强制删除的目标要到运行时才知道（{target.Raw}），无法事先判断影响范围，已阻止。" +
                    "请把路径写成确定的值");
            }
            var why = DangerousTarget(target.Full!);
            if (why is not null)
            {
                return PolicyDecision.Blocked($"不能递归删除 {target.Full}：{why}");
            }
        }
        return null; // 目标都正常，后面按高危命令走确认
    }

    /// <summary>这个路径能不能被整个删掉。能删返回 null，不能删返回原因。</summary>
    public string? DangerousTarget(string fullPath)
    {
        try
        {
            var path = PathResolver.RealPath(fullPath).TrimEnd('\\', '/');

            // 盘符根或文件系统根
            if (path.Length <= 3 && (path.EndsWith(":", StringComparison.Ordinal) || path is "/" or ""))
            {
                return "这是整个磁盘";
            }
            foreach (var root in Config.ProtectedRoots)
            {
                if (PathResolver.IsUnder(path, root))
                {
                    return $"位于受保护的系统目录 {root} 之下";
                }
            }
            // 这些目录本身不许整个删，但它们下面的子目录可以——
            // 不能删 C:\Users，但可以删 C:\Users\nguyen\project\build
            foreach (var root in Config.NoDeleteRoots)
            {
                if (SamePath(path, root))
                {
                    return $"{root} 是系统关键目录，不能整个删除";
                }
            }
            // 当前用户的主目录，删了等于毁掉这个人的全部数据
            var home = SafeFolder(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0 && SamePath(path, home))
            {
                return "这是用户主目录";
            }
            return null;
        }
        catch (Exception)
        {
            return "无法判断这个路径的位置";
        }
    }

    /// <summary>
    /// 命令里把 <paramref name="path"/> 当成完整目标提到了吗。
    ///
    /// 后面还跟着分隔符或字母的不算——C:\Users\nguyen\project 删的是人家的项目目录，
    /// 不是 C:\Users 本身，不该因为前缀撞上就拦掉。
    /// </summary>
    private static bool MentionsWholePath(string command, string path)
    {
        var at = 0;
        while (true)
        {
            at = command.IndexOf(path, at, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }
            var after = at + path.Length;
            if (after >= command.Length || !(command[after] is '\\' or '/' || char.IsLetterOrDigit(command[after])))
            {
                return true;
            }
            at = after;
        }
    }

    private static bool SamePath(string a, string b) =>
        a.TrimEnd('\\', '/').Equals(b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); }
        catch (Exception) { return ""; }
    }

    /// <summary>这条命令是否只读（配置里显式列出的命令名，用于兼容管理员下发的 readonly_commands）。</summary>
    public bool IsReadonlyCommand(string command) => IsReadonly(Regex.Replace(command, @"[ \t]+", " ").Trim());

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
            // 脚本里的递归删除同样按目标判，否则把命令塞进 .ps1 就绕过去了
            var sweep = CheckRecursiveDelete(line, Path.GetDirectoryName(path));
            if (sweep is not null && sweep.Level == RiskLevel.Blocked)
            {
                return PolicyDecision.Blocked($"脚本 {Path.GetFileName(path)} 第 {lineNo} 行：{sweep.Reason}");
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

    private static List<Regex> Compile(IEnumerable<string> patterns)
    {
        var list = new List<Regex>();
        foreach (var pattern in patterns)
        {
            try
            {
                list.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200)));
            }
            catch (ArgumentException)
            {
                // 无效正则：忽略该条，其余规则照常生效
            }
        }
        return list;
    }

    /// <summary>匹配时超时按「命中」处理——判断不了就不要放行。</summary>
    private static bool SafeMatch(Regex re, string text)
    {
        try { return re.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return true; }
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

    public static bool IsUnder(string fullPath, string root)
    {
        var a = fullPath.Replace('/', '\\').TrimEnd('\\');
        var b = root.Replace('/', '\\').TrimEnd('\\');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
               || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
