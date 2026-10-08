using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>一条命令对这台电脑做了什么。判定按「副作用」而不是按命令原文。</summary>
public enum CommandEffect
{
    /// <summary>只查询，不改变任何东西。</summary>
    Read,

    /// <summary>会新建或写入文件，但不会毁掉已有数据（mkdir、npm install、git commit…）。</summary>
    Write,

    /// <summary>会删除、覆盖已有数据，或改变系统状态（del、注册表、服务、关机、装卸软件…）。</summary>
    Destructive,

    /// <summary>认不出来的命令。保守按「需要确认」处理。</summary>
    Unknown,
}

/// <summary>命令里的一段（按 ; && || | 拆开后的一条）。</summary>
public sealed record CommandSegment(string Raw, string Executable, string Arguments, CommandEffect Effect, string Reason);

/// <summary>整条命令的分析结果。</summary>
public sealed record CommandAnalysis(
    IReadOnlyList<CommandSegment> Segments,
    CommandEffect Effect,
    string Reason,
    bool HasIndirection,
    string? IndirectionReason,
    IReadOnlyList<string> WrittenPaths)
{
    /// <summary>认不出内容的命令（动态拼接、下载即执行、Base64 编码…）永远不能自动执行，也不能被规则放行。</summary>
    public bool CanBeAutomatic => !HasIndirection && Effect is CommandEffect.Read or CommandEffect.Write;

    /// <summary>最该让用户看到的那一段（决定整条命令等级的那段）。</summary>
    public CommandSegment? Worst => Segments.Count == 0
        ? null
        : Segments.OrderByDescending(s => Rank(s.Effect)).First();

    internal static int Rank(CommandEffect e) => e switch
    {
        CommandEffect.Read => 0,
        CommandEffect.Write => 1,
        CommandEffect.Unknown => 2,
        _ => 3,
    };
}

/// <summary>
/// 把一条 Shell 命令拆成若干段，逐段判断副作用。
///
/// 这么做是为了替掉「记住一模一样的命令」那套：那套既挡不住换个参数就变危险的命令，
/// 又让用户为每条无害的查询反复点确认。按副作用分级之后：
/// 只读和工作区内的写入自动执行，删除 / 改系统状态永远要人确认，认不出来的保守确认。
/// </summary>
public static class CommandAnalyzer
{
    // ---------- 只读：查询、列目录、看内容 ----------
    private static readonly HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        // cmd
        "dir", "type", "more", "tree", "findstr", "find", "where", "ver", "vol", "hostname", "whoami",
        "systeminfo", "ipconfig", "ping", "tracert", "nslookup", "getmac", "chcp", "date", "time", "echo",
        "tasklist", "driverquery", "fc", "comp", "set", "path", "assoc", "ftype", "cd", "pwd", "pushd", "popd",
        // unix 风格（Git Bash / WSL 里常见）
        "ls", "cat", "head", "tail", "grep", "wc", "du", "df", "stat", "file", "realpath", "basename", "dirname",
        // PowerShell 动词
        "get-childitem", "get-content", "get-item", "get-itemproperty", "get-location", "get-process",
        "get-service", "get-date", "get-host", "get-command", "get-help", "get-member", "get-variable",
        "get-computerinfo", "get-wmiobject", "get-ciminstance", "get-volume", "get-disk", "get-psdrive",
        "get-netipconfiguration", "get-netadapter", "get-hotfix", "get-eventlog", "get-winevent",
        "get-filehash", "get-acl", "get-package", "get-appxpackage", "get-scheduledtask", "get-localuser",
        "select-object", "select-string", "where-object", "sort-object", "measure-object", "group-object",
        "format-table", "format-list", "out-string", "out-host", "compare-object", "convertfrom-json",
        "convertto-json", "test-path", "resolve-path", "split-path", "join-path", "write-host", "write-output",
    };

    // 带子命令才算只读的程序：git status 只读，git push 不是
    private static readonly Dictionary<string, HashSet<string>> ReadSubCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git"] = new(StringComparer.OrdinalIgnoreCase) { "status", "log", "diff", "show", "branch", "remote", "config", "describe", "blame", "ls-files", "rev-parse", "shortlog", "tag" },
        ["npm"] = new(StringComparer.OrdinalIgnoreCase) { "ls", "list", "view", "info", "outdated", "root", "prefix", "config", "ping", "whoami" },
        ["pip"] = new(StringComparer.OrdinalIgnoreCase) { "list", "show", "freeze", "check", "config" },
        ["dotnet"] = new(StringComparer.OrdinalIgnoreCase) { "--version", "--info", "--list-sdks", "--list-runtimes" },
        ["sc"] = new(StringComparer.OrdinalIgnoreCase) { "query", "queryex", "qc", "showsid" },
        ["net"] = new(StringComparer.OrdinalIgnoreCase) { "view", "share", "time", "statistics" },
        ["wmic"] = new(StringComparer.OrdinalIgnoreCase) { "os", "cpu", "logicaldisk", "process", "product", "bios", "computersystem" },
        ["reg"] = new(StringComparer.OrdinalIgnoreCase) { "query" },
        ["docker"] = new(StringComparer.OrdinalIgnoreCase) { "ps", "images", "logs", "inspect", "version", "info" },
    };

    // ---------- 高危：删除、覆盖、改系统 ----------
    private static readonly HashSet<string> DestructiveVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        // 删除与覆盖
        "del", "erase", "rd", "rmdir", "rm", "unlink", "shred", "move", "mv", "ren", "rename", "replace",
        "remove-item", "remove-itemproperty", "clear-content", "clear-item", "move-item", "rename-item",
        // 磁盘与系统
        "format", "diskpart", "chkdsk", "bcdedit", "vssadmin", "cipher", "fsutil", "label", "convert",
        "shutdown", "restart-computer", "stop-computer", "logoff", "bootcfg",
        // 进程与服务
        "taskkill", "stop-process", "stop-service", "set-service", "restart-service", "new-service", "remove-service",
        // 注册表
        "regedit", "set-itemproperty", "new-itemproperty", "remove-item -path hkcu:", "reg",
        // 账号与权限
        "icacls", "cacls", "takeown", "attrib", "new-localuser", "remove-localuser", "add-localgroupmember",
        "set-executionpolicy", "set-acl",
        // 计划任务、网络配置
        "schtasks", "register-scheduledtask", "unregister-scheduledtask", "netsh", "route", "arp",
        // 装卸软件
        "msiexec", "winget", "choco", "scoop", "install-package", "uninstall-package", "install-module",
        "add-appxpackage", "remove-appxpackage", "dism", "sfc",
    };

    // 带子命令才算高危的程序（只保留真正危险的：会丢数据、破坏历史、改系统）
    private static readonly Dictionary<string, HashSet<string>> DestructiveSubCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // git: 只有会丢失本地修改或破坏历史的才算高危
        ["git"] = new(StringComparer.OrdinalIgnoreCase) { "reset", "clean", "filter-branch", "gc", "prune", "rm" },
        ["npm"] = new(StringComparer.OrdinalIgnoreCase) { "uninstall", "publish", "unpublish", "link", "adduser", "login" },
        ["pip"] = new(StringComparer.OrdinalIgnoreCase) { "uninstall" },
        ["docker"] = new(StringComparer.OrdinalIgnoreCase) { "rm", "rmi", "prune", "kill", "stop", "system" },
        ["sc"] = new(StringComparer.OrdinalIgnoreCase) { "config", "delete", "create", "stop", "start", "failure" },
        ["net"] = new(StringComparer.OrdinalIgnoreCase) { "user", "localgroup", "group", "stop", "start", "accounts", "use", "share", "config" },
        ["wmic"] = new(StringComparer.OrdinalIgnoreCase) { "process", "product", "service" }, // 只有 call/delete 才危险，下面另判
    };

    // ---------- 会写文件但不毁数据 ----------
    private static readonly HashSet<string> WriteVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "mkdir", "new-item", "copy", "xcopy", "robocopy", "copy-item", "set-content", "add-content",
        "out-file", "tee-object", "export-csv", "exportto-json", "export-clixml", "compress-archive",
        "expand-archive", "tar", "7z", "zip", "unzip", "curl", "wget", "invoke-webrequest", "start-sleep",
        "new-itemproperty-dummy",
    };

    private static readonly Dictionary<string, HashSet<string>> WriteSubCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // git: push/checkout/switch/stash/rebase 等日常操作放到 Write，确认一次后可生成规则
        ["git"] = new(StringComparer.OrdinalIgnoreCase) { "add", "commit", "init", "clone", "fetch", "pull", "merge", "apply", "cherry-pick", "push", "checkout", "switch", "stash", "rebase", "restore" },
        ["npm"] = new(StringComparer.OrdinalIgnoreCase) { "install", "i", "ci", "run", "run-script", "build", "test", "start", "exec" },
        ["pip"] = new(StringComparer.OrdinalIgnoreCase) { "install", "download", "wheel" },
        ["dotnet"] = new(StringComparer.OrdinalIgnoreCase) { "build", "restore", "publish", "test", "run", "new", "pack", "clean" },
        ["yarn"] = new(StringComparer.OrdinalIgnoreCase) { "install", "add", "build", "test", "run" },
        ["pnpm"] = new(StringComparer.OrdinalIgnoreCase) { "install", "add", "build", "test", "run" },
    };

    /// <summary>解释器：同样一条 `python x.py` 今天和明天跑的内容可能完全不同，所以永远不给它建自动执行规则。</summary>
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "py", "node", "deno", "bun", "powershell", "pwsh", "cmd", "bash", "sh", "zsh",
        "wscript", "cscript", "perl", "ruby", "php", "java", "dotnet-script", "iex", "invoke-expression",
    };

    // 动态执行 / 下载即执行 / 编码命令：认不出真正要跑什么
    private static readonly (Regex Pattern, string Reason)[] Indirections =
    {
        (new Regex(@"\$\(|`\(|\$\{", RegexOptions.Compiled), "命令里有动态拼接（$( ) 之类），实际执行的内容要运行时才知道"),
        (new Regex(@"\b(iex|invoke-expression)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "命令里有 Invoke-Expression，会把文本当代码执行"),
        (new Regex(@"-enc(odedcommand)?\s+[A-Za-z0-9+/=]{16,}", RegexOptions.IgnoreCase | RegexOptions.Compiled), "命令是 Base64 编码的，看不出实际内容"),
        (new Regex(@"frombase64string|downloadstring|downloadfile", RegexOptions.IgnoreCase | RegexOptions.Compiled), "命令会从网上取内容直接执行"),
        (new Regex(@"\|\s*(iex|invoke-expression|powershell|pwsh|cmd|bash|sh)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "命令把输出管道给了解释器执行"),
        (new Regex(@"\b(runas|start-process)\b[^|]*-verb\s+runas", RegexOptions.IgnoreCase | RegexOptions.Compiled), "命令要以管理员身份运行"),
        (new Regex(@"(?:^|[;&|]\s*)&?\s*\$[A-Za-z_]", RegexOptions.Compiled), "命令会执行变量里的内容，实际跑什么要运行时才知道"),
    };

    private static readonly Regex Separators = new(@"(?:&&|\|\||;|\||&|\r?\n)", RegexOptions.Compiled);
    private static readonly Regex RedirectTarget = new(@"(?<!\d)>{1,2}\s*(""[^""]+""|\S+)", RegexOptions.Compiled);

    public static CommandAnalysis Analyze(string command)
    {
        command = (command ?? "").Trim();
        if (command.Length == 0)
        {
            return new CommandAnalysis(Array.Empty<CommandSegment>(), CommandEffect.Unknown, "命令为空", false, null, Array.Empty<string>());
        }

        string? indirection = null;
        foreach (var (pattern, reason) in Indirections)
        {
            if (pattern.IsMatch(command))
            {
                indirection = reason;
                break;
            }
        }

        var written = new List<string>();
        foreach (Match m in RedirectTarget.Matches(command))
        {
            var target = m.Groups[1].Value.Trim('"');
            if (target.Length > 0 && !written.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                written.Add(target);
            }
        }

        var segments = new List<CommandSegment>();
        foreach (var raw in SplitSegments(command))
        {
            var text = raw.Trim();
            if (text.Length == 0)
            {
                continue;
            }
            segments.Add(Classify(text, written));
        }
        if (segments.Count == 0)
        {
            segments.Add(Classify(command, written));
        }

        var worst = segments.OrderByDescending(s => CommandAnalysis.Rank(s.Effect)).First();
        return new CommandAnalysis(segments, worst.Effect, worst.Reason, indirection is not null, indirection, written);
    }

    /// <summary>
    /// 这条命令能不能作为「以后自动执行」的规则，能的话规则前缀是什么。
    /// 只有整条命令都认得出、且不是高危、不含动态执行时才给前缀。
    /// </summary>
    public static string? SuggestRulePrefix(string command)
    {
        var analysis = Analyze(command);
        if (analysis.HasIndirection || analysis.Effect == CommandEffect.Destructive || analysis.Segments.Count != 1)
        {
            return null; // 复合命令不整条记住，避免 `npm run build && del x` 这种被一起放行
        }
        var seg = analysis.Segments[0];
        var exe = seg.Executable;
        if (exe.Length == 0 || Interpreters.Contains(exe))
        {
            return null;
        }
        var sub = FirstToken(seg.Arguments);
        // 子命令是干净的单词（不是路径、不是开关）才带上，否则只记住程序名
        if (sub.Length > 0 && Regex.IsMatch(sub, @"^[A-Za-z][A-Za-z0-9_.-]*$") && !sub.Contains('.') && !sub.StartsWith('-'))
        {
            return $"{exe} {sub}".ToLowerInvariant();
        }
        return exe.ToLowerInvariant();
    }

    public static IEnumerable<string> SplitSegments(string command)
    {
        // 引号内的分隔符不算分隔符
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                current.Append(c);
                continue;
            }
            var rest = command.AsSpan(i);
            var sep = rest.StartsWith("&&") || rest.StartsWith("||") ? 2
                : c is ';' or '|' or '&' or '\n' ? 1
                : 0;
            if (sep > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                i += sep - 1;
                continue;
            }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }

    private static CommandSegment Classify(string segment, List<string> written)
    {
        var text = Regex.Replace(segment, @"\s+", " ").Trim();
        // 去掉 PowerShell 的调用操作符和前置的环境变量赋值
        text = Regex.Replace(text, @"^&\s*", "");
        var exeRaw = FirstToken(text);
        var args = text.Length > exeRaw.Length ? text[exeRaw.Length..].Trim() : "";
        var exe = Path.GetFileNameWithoutExtension(exeRaw.Trim('"', '\'', '&'));

        // 重定向写文件：无论什么命令，都至少算「写」
        var redirects = written.Count > 0 && RedirectTarget.IsMatch(segment);

        if (DestructiveSubCommands.TryGetValue(exe, out var dsub) && dsub.Contains(FirstToken(args)))
        {
            return new CommandSegment(segment, exe, args, CommandEffect.Destructive, $"{exe} {FirstToken(args)} 会改动或丢失已有内容");
        }
        if (DestructiveVerbs.Contains(exe))
        {
            return new CommandSegment(segment, exe, args, CommandEffect.Destructive, $"{exe} 会删除、覆盖数据或改变系统设置");
        }
        if (ReadSubCommands.TryGetValue(exe, out var rsub) && rsub.Contains(FirstToken(args)))
        {
            return new CommandSegment(segment, exe, args, redirects ? CommandEffect.Write : CommandEffect.Read, $"{exe} {FirstToken(args)} 只是查询");
        }
        if (WriteSubCommands.TryGetValue(exe, out var wsub) && wsub.Contains(FirstToken(args)))
        {
            return new CommandSegment(segment, exe, args, CommandEffect.Write, $"{exe} {FirstToken(args)} 会生成或更新文件");
        }
        if (ReadVerbs.Contains(exe))
        {
            return new CommandSegment(segment, exe, args, redirects ? CommandEffect.Write : CommandEffect.Read, $"{exe} 只是查询");
        }
        if (WriteVerbs.Contains(exe))
        {
            return new CommandSegment(segment, exe, args, CommandEffect.Write, $"{exe} 会生成或写入文件");
        }
        if (Interpreters.Contains(exe))
        {
            return new CommandSegment(segment, exe, args, CommandEffect.Unknown, $"{exe} 执行的是脚本内容，无法事先判断会做什么");
        }
        return new CommandSegment(segment, exe, args, CommandEffect.Unknown, $"认不出 {exe} 会做什么");
    }

    private static string FirstToken(string text)
    {
        text = text.TrimStart();
        if (text.Length == 0)
        {
            return "";
        }
        if (text[0] is '"' or '\'')
        {
            var end = text.IndexOf(text[0], 1);
            return end > 0 ? text[1..end] : text[1..];
        }
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
