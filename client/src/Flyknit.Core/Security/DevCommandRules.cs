namespace Flyknit.Core.Security;

/// <summary>
/// git / gh（GitHub CLI）按「子命令 + 关键参数」定级。只看子命令不够：
/// git push 是日常写入，git push --force 会把远端别人的提交冲掉；git branch 是查询，git branch -D 是删分支。
/// 这些会丢数据、改历史、动凭证的组合一律 Destructive（永远逐次确认，不能记成规则）。
/// 认不出来的（别名、插件子命令、git -c 改配置后执行）按 Unknown 保守确认。
/// </summary>
internal static class DevCommandRules
{
    private static readonly HashSet<string> GitRead = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "describe", "blame", "ls-files", "ls-tree", "ls-remote", "rev-parse", "rev-list",
        "shortlog", "cat-file", "grep", "show-ref", "for-each-ref", "merge-base", "name-rev", "count-objects", "fsck",
        "whatchanged", "cherry", "range-diff", "check-ignore", "check-attr", "var", "version", "help",
    };

    // 会丢本地修改、毁掉恢复手段或改写共享历史的子命令（无论带什么参数）
    private static readonly HashSet<string> GitDestructive = new(StringComparer.OrdinalIgnoreCase)
    {
        "reset", "clean", "filter-branch", "filter-repo", "gc", "prune", "rm", "update-ref", "replace",
    };

    private static readonly HashSet<string> GitWrite = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "commit", "init", "clone", "fetch", "pull", "merge", "apply", "am", "cherry-pick", "revert", "rebase",
        "mv", "notes", "submodule", "bisect", "format-patch", "archive", "bundle", "restore", "checkout", "switch",
        "stash", "push", "branch", "tag", "remote", "config", "worktree", "reflog", "sparse-checkout", "maintenance",
    };

    /// <param name="tokens">git 后面的参数（已按空白和引号拆好）。</param>
    public static (CommandEffect Effect, string Reason) Git(IReadOnlyList<string> tokens)
    {
        var i = 0;
        // 全局参数：-C <目录>、--no-pager 这类不影响副作用；-c 能临时改配置（core.pager、alias.x=!命令）等于任意执行
        while (i < tokens.Count && tokens[i].StartsWith('-'))
        {
            var t = tokens[i];
            if (t is "-c" || t.StartsWith("--config-env", StringComparison.Ordinal) || t.StartsWith("--exec-path", StringComparison.Ordinal))
            {
                return (CommandEffect.Unknown, "git -c 会临时改配置（可以借此执行任意命令），无法事先判断会做什么");
            }
            i += t is "-C" or "--git-dir" or "--work-tree" or "--namespace" ? 2 : 1;
        }
        if (i >= tokens.Count)
        {
            return (CommandEffect.Read, "git 只是查询");
        }
        var sub = tokens[i];
        var args = tokens.Skip(i + 1).ToList();
        bool Has(params string[] flags) => args.Any(a => flags.Any(f => a.Equals(f, StringComparison.Ordinal) || (f.StartsWith("--") && a.StartsWith(f + "=", StringComparison.Ordinal))));
        bool HasShort(char c) => args.Any(a => a.Length > 1 && a[0] == '-' && a[1] != '-' && a.IndexOf(c, 1) > 0);
        var positional = args.Where(a => !a.StartsWith('-')).ToList();
        string D(string why) => $"git {sub} {why}";

        if (GitDestructive.Contains(sub))
        {
            return (CommandEffect.Destructive, D("会丢失本地修改或改写历史"));
        }
        if (GitRead.Contains(sub))
        {
            // log / diff --output=文件 会写文件
            return Has("--output") ? (CommandEffect.Write, D("会把结果写进文件")) : (CommandEffect.Read, D("只是查询"));
        }
        switch (sub.ToLowerInvariant())
        {
            case "push":
                if (Has("--force", "--force-with-lease", "--force-if-includes", "--mirror", "--delete", "--prune") || HasShort('f') || HasShort('d')
                    || positional.Skip(1).Any(r => r.StartsWith('+') || r.StartsWith(':')))
                {
                    return (CommandEffect.Destructive, D("会强制覆盖或删除远端分支，别人的提交可能被冲掉"));
                }
                return (CommandEffect.Write, D("会把提交推到远端"));
            case "checkout":
                if (args.Contains("--") || positional.Contains(".") || args.Contains("-B") || Has("--force", "--ours", "--theirs", "--patch") || HasShort('f') || HasShort('p'))
                {
                    return (CommandEffect.Destructive, D("会用仓库里的版本覆盖文件，未提交的修改会丢"));
                }
                return (CommandEffect.Write, D("会切换分支"));
            case "switch":
                return Has("--force", "--discard-changes", "--force-create") || HasShort('f') || args.Contains("-C")
                    ? (CommandEffect.Destructive, D("会丢弃未提交的修改或重置已有分支"))
                    : (CommandEffect.Write, D("会切换分支"));
            case "restore":
                // 只 --staged 是把文件移出暂存区，工作区不动；其余都会用仓库版本覆盖工作区文件
                return Has("--staged") && !Has("--worktree") && !HasShort('W') || HasShort('S') && !HasShort('W')
                    ? (CommandEffect.Write, D("会把文件移出暂存区"))
                    : (CommandEffect.Destructive, D("会用仓库里的版本覆盖文件，未提交的修改会丢"));
            case "stash":
                var action = positional.FirstOrDefault() ?? "push";
                return action switch
                {
                    "drop" or "clear" => (CommandEffect.Destructive, D($"{action} 会永久丢掉暂存的修改")),
                    "list" or "show" => (CommandEffect.Read, D("只是查询")),
                    _ => (CommandEffect.Write, D("会暂存或恢复修改")),
                };
            case "branch":
                if (Has("--delete", "--force") || args.Any(a => a is "-d" or "-D" or "-f" or "-M" or "-C"))
                {
                    return (CommandEffect.Destructive, D("会删除、强制移动或覆盖分支"));
                }
                if (Has("--move", "--copy", "--set-upstream-to", "--unset-upstream") || args.Any(a => a is "-m" or "-c" or "-u"))
                {
                    return (CommandEffect.Write, D("会重命名、复制分支或改上游"));
                }
                return positional.Count > 0 && !Has("--list", "--contains", "--merged", "--no-merged", "--points-at") && !args.Contains("-l")
                    ? (CommandEffect.Write, D("会新建分支"))
                    : (CommandEffect.Read, D("只是查询"));
            case "tag":
                if (Has("--delete", "--force") || args.Any(a => a is "-d" or "-f"))
                {
                    return (CommandEffect.Destructive, D("会删除或覆盖标签"));
                }
                return positional.Count > 0 && !Has("--list", "--contains", "--points-at") && !args.Contains("-l")
                    ? (CommandEffect.Write, D("会新建标签"))
                    : (CommandEffect.Read, D("只是查询"));
            case "remote":
                var verb = positional.FirstOrDefault() ?? "";
                return verb is "" or "show" or "get-url"
                    ? (CommandEffect.Read, D("只是查询"))
                    : (CommandEffect.Write, D("会修改远端仓库配置"));
            case "config":
                if (Has("--get", "--get-all", "--get-regexp", "--list", "--show-origin") || args.Contains("-l") || positional.Count <= 1 && !Has("--unset", "--unset-all", "--add", "--replace-all", "--remove-section", "--rename-section", "--edit") && !args.Contains("-e"))
                {
                    return (CommandEffect.Read, D("只是查询"));
                }
                return Has("--system")
                    ? (CommandEffect.Destructive, D("会改整台电脑的 git 设置"))
                    : (CommandEffect.Write, D("会修改 git 设置"));
            case "reflog":
                var reflogAction = positional.FirstOrDefault() ?? "show";
                return reflogAction is "expire" or "delete"
                    ? (CommandEffect.Destructive, D("会删掉找回误删提交的记录"))
                    : (CommandEffect.Read, D("只是查询"));
            case "worktree":
                var wt = positional.FirstOrDefault() ?? "";
                return wt switch
                {
                    "list" => (CommandEffect.Read, D("只是查询")),
                    "remove" or "prune" => (CommandEffect.Destructive, D("会删除工作树目录")),
                    _ => (CommandEffect.Write, D("会新建或修改工作树")),
                };
            case "rebase":
            case "commit":
                // 改写的是本地历史，reflog 里能找回；推到远端时 push --force 那一步另外按高危确认
                return (CommandEffect.Write, D("会生成或改写本地提交"));
        }
        return GitWrite.Contains(sub)
            ? (CommandEffect.Write, D("会生成或更新文件"))
            : (CommandEffect.Unknown, $"认不出 git {sub} 会做什么（可能是别名或插件）");
    }

    private static readonly Dictionary<string, HashSet<string>> GhRead = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pr"] = new(StringComparer.OrdinalIgnoreCase) { "list", "view", "diff", "checks", "status" },
        ["issue"] = new(StringComparer.OrdinalIgnoreCase) { "list", "view", "status" },
        ["repo"] = new(StringComparer.OrdinalIgnoreCase) { "view", "list" },
        ["run"] = new(StringComparer.OrdinalIgnoreCase) { "list", "view", "watch" },
        ["workflow"] = new(StringComparer.OrdinalIgnoreCase) { "list", "view" },
        ["release"] = new(StringComparer.OrdinalIgnoreCase) { "list", "view" },
        ["label"] = new(StringComparer.OrdinalIgnoreCase) { "list" },
        ["gist"] = new(StringComparer.OrdinalIgnoreCase) { "list", "view" },
        ["search"] = new(StringComparer.OrdinalIgnoreCase) { "repos", "issues", "prs", "code", "commits" },
        ["cache"] = new(StringComparer.OrdinalIgnoreCase) { "list" },
        ["auth"] = new(StringComparer.OrdinalIgnoreCase) { "status" },
    };

    private static readonly Dictionary<string, HashSet<string>> GhWrite = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pr"] = new(StringComparer.OrdinalIgnoreCase) { "create", "comment", "edit", "review", "checkout", "ready", "reopen", "close", "merge" },
        ["issue"] = new(StringComparer.OrdinalIgnoreCase) { "create", "comment", "edit", "reopen", "close", "pin", "unpin", "lock", "unlock" },
        ["repo"] = new(StringComparer.OrdinalIgnoreCase) { "clone", "fork", "create", "sync" },
        ["run"] = new(StringComparer.OrdinalIgnoreCase) { "rerun", "cancel", "download" },
        ["workflow"] = new(StringComparer.OrdinalIgnoreCase) { "run", "enable", "disable" },
        ["release"] = new(StringComparer.OrdinalIgnoreCase) { "create", "edit", "upload", "download" },
        ["label"] = new(StringComparer.OrdinalIgnoreCase) { "create", "edit" },
        ["gist"] = new(StringComparer.OrdinalIgnoreCase) { "create", "edit" },
    };

    // 删库删数据、动凭证和密钥、装扩展（等于装软件）：永远逐次确认
    private static readonly HashSet<string> GhDestructiveGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth", "secret", "variable", "ssh-key", "gpg-key", "extension", "alias", "config", "codespace",
    };

    public static (CommandEffect Effect, string Reason) Gh(IReadOnlyList<string> tokens)
    {
        var group = tokens.FirstOrDefault(t => !t.StartsWith('-')) ?? "";
        var gi = tokens.ToList().IndexOf(group);
        var action = tokens.Skip(gi + 1).FirstOrDefault(t => !t.StartsWith('-')) ?? "";
        var label = $"gh {group} {action}".Trim();
        if (group.Length == 0 || group is "status" or "version" or "help" or "browse")
        {
            return (CommandEffect.Read, $"{label} 只是查询");
        }
        if (group.Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return GhApi(tokens.Skip(gi + 1).ToList());
        }
        if (GhRead.TryGetValue(group, out var read) && read.Contains(action))
        {
            return (CommandEffect.Read, $"{label} 只是查询");
        }
        if (GhDestructiveGroups.Contains(group) || action is "delete" or "archive" or "unarchive" or "rename" or "transfer" or "delete-asset"
            || group.Equals("repo", StringComparison.OrdinalIgnoreCase) && action == "edit")
        {
            return (CommandEffect.Destructive, $"{label} 会删除、转移数据或改动账号凭证、仓库设置");
        }
        if (GhWrite.TryGetValue(group, out var write) && write.Contains(action))
        {
            return (CommandEffect.Write, $"{label} 会在 GitHub 上创建或修改内容");
        }
        return (CommandEffect.Unknown, $"认不出 {label} 会做什么");
    }

    // gh api：默认 GET 只读；-X/--method 指定别的方法、或者带了字段（会自动变 POST）就是写；DELETE 是删除
    private static (CommandEffect, string) GhApi(IReadOnlyList<string> args)
    {
        string? method = null;
        for (var k = 0; k < args.Count; k++)
        {
            var a = args[k];
            if ((a is "-X" or "--method") && k + 1 < args.Count)
            {
                method = args[k + 1].ToUpperInvariant();
            }
            else if (a.StartsWith("--method=", StringComparison.Ordinal))
            {
                method = a["--method=".Length..].ToUpperInvariant();
            }
            else if (a.StartsWith("-X", StringComparison.Ordinal) && a.Length > 2)
            {
                method = a[2..].ToUpperInvariant();
            }
        }
        var fields = args.Any(a => a is "-f" or "-F" or "--field" or "--raw-field" or "--input" || a.StartsWith("--field=") || a.StartsWith("--raw-field=") || a.StartsWith("--input="));
        method ??= fields ? "POST" : "GET";
        return method switch
        {
            "GET" or "HEAD" => (CommandEffect.Read, "gh api GET 只是查询"),
            "DELETE" => (CommandEffect.Destructive, "gh api DELETE 会删除 GitHub 上的数据"),
            _ => (CommandEffect.Write, $"gh api {method} 会在 GitHub 上创建或修改内容"),
        };
    }

    /// <summary>按空白拆参数，引号里的空白不拆，引号本身去掉。</summary>
    public static List<string> Tokens(string text)
    {
        var list = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var any = false;
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                any = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || any)
                {
                    list.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0 || any)
        {
            list.Add(current.ToString());
        }
        return list;
    }
}
