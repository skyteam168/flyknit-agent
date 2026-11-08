using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flyknit.Core.Tools;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Git;

/// <summary>GitHub CLI（gh）没装或没登录。消息直接给员工和模型看。</summary>
public sealed class GhUnavailableException : Exception
{
    public GhUnavailableException(string message) : base(message) { }
}

/// <summary>
/// 调用员工电脑上的 GitHub CLI（gh）。用的是员工自己 gh auth login 的登录，令牌不经过 FlyknitBuddy 和公司服务器
/// （员工电脑只持有设备令牌这条规矩不变）。不弹交互、不检查更新、不带颜色。
/// </summary>
public static class GhCli
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

    public const string MissingMessage = "这台电脑没有安装 GitHub CLI（gh），或者还没登录。请安装 gh（https://cli.github.com），在命令行运行 gh auth login 登录后再试";

    public static async Task<CliRunner.Result> RunAsync(string directory, IEnumerable<string> args, CancellationToken ct)
    {
        var env = new Dictionary<string, string>
        {
            ["GH_PROMPT_DISABLED"] = "1",
            ["GH_NO_UPDATE_NOTIFIER"] = "1",
            ["NO_COLOR"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
        try
        {
            var r = await CliRunner.RunAsync("gh", directory, args, env, Timeout, ct);
            if (r.ExitCode != 0 && (r.Stderr.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
                || r.Stderr.Contains("not logged", StringComparison.OrdinalIgnoreCase)))
            {
                throw new GhUnavailableException(MissingMessage);
            }
            return r;
        }
        catch (CliMissingException)
        {
            throw new GhUnavailableException(MissingMessage);
        }
    }

    public const string ViewFields = "number,title,url,state,isDraft,mergeable,reviewDecision,headRefName,baseRefName,statusCheckRollup,comments,reviews";

    /// <summary>查一个 PR 的状态。number 为 0 时查当前分支对应的 PR。</summary>
    public static async Task<PrStatus> ViewAsync(string directory, int number, CancellationToken ct)
    {
        var args = new List<string> { "pr", "view" };
        if (number > 0)
        {
            args.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "--json", ViewFields });
        var r = await RunAsync(directory, args, ct);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException(Error(r, number > 0 ? $"查不到 PR #{number}" : "当前分支还没有 PR"));
        }
        using var doc = JsonDocument.Parse(r.Stdout);
        return PrStatus.Parse(doc.RootElement);
    }

    /// <summary>
    /// 聊天里 PR / Issue 链接的悬停卡片。只读查询（gh pr view / gh issue view 直接给地址），
    /// 不需要在那个仓库的工作副本里，所以在临时目录里跑。
    /// </summary>
    public static async Task<RefCard?> CardAsync(RefLink link, CancellationToken ct)
    {
        var args = link.Kind == "pr"
            ? new List<string> { "pr", "view", link.Url, "--json", RefCard.PrFields }
            : new List<string> { "issue", "view", link.Url, "--json", RefCard.IssueFields };
        var r = await RunAsync(Path.GetTempPath(), args, ct);
        if (r.ExitCode != 0 || r.Stdout.Trim().Length == 0)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(r.Stdout);
        return RefCard.Parse(link, doc.RootElement);
    }

    /// <summary>建 PR，返回 PR 地址。</summary>
    /// <param name="repo">owner/repo：不在那个仓库的工作副本里时指定（沙箱出口从临时目录建 PR）。</param>
    public static async Task<string> CreateAsync(string directory, string title, string body, string baseBranch, string head, bool draft, CancellationToken ct, string? repo = null)
    {
        var args = new List<string> { "pr", "create", "--title", title, "--body", body, "--head", head };
        if (!string.IsNullOrEmpty(repo))
        {
            args.AddRange(new[] { "--repo", repo });
        }
        if (baseBranch.Length > 0)
        {
            args.AddRange(new[] { "--base", baseBranch });
        }
        if (draft)
        {
            args.Add("--draft");
        }
        var r = await RunAsync(directory, args, ct);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException(Error(r, "建 PR 失败"));
        }
        // gh 把 PR 地址打在标准输出最后一行
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith("https://", StringComparison.Ordinal)) ?? r.Stdout.Trim();
    }

    private static string Error(CliRunner.Result r, string fallback)
    {
        var text = (r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim();
        return Security.SecretRedactor.Redact(text.Length > 0 ? text : fallback);
    }
}

/// <summary>一项检查（CI 任务或提交状态）。State：pass / fail / pending / skip。</summary>
public sealed record PrCheck(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("url")] string Url);

/// <summary>一个 PR 此刻的状态（gh pr view --json 的结果整理过）。</summary>
public sealed record PrStatus
{
    [JsonPropertyName("number")] public int Number { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";

    /// <summary>OPEN / MERGED / CLOSED。</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "OPEN";
    [JsonPropertyName("draft")] public bool Draft { get; init; }

    /// <summary>MERGEABLE / CONFLICTING / UNKNOWN。</summary>
    [JsonPropertyName("mergeable")] public string Mergeable { get; init; } = "UNKNOWN";

    /// <summary>APPROVED / CHANGES_REQUESTED / REVIEW_REQUIRED / 空。</summary>
    [JsonPropertyName("review")] public string Review { get; init; } = "";
    [JsonPropertyName("head")] public string Head { get; init; } = "";
    [JsonPropertyName("base")] public string Base { get; init; } = "";
    [JsonPropertyName("checks")] public List<PrCheck> Checks { get; init; } = new();

    /// <summary>评论加评审的总数（看有没有新的）。</summary>
    [JsonPropertyName("comments")] public int Comments { get; init; }

    /// <summary>CI 总体：none 没有检查 / pending 还在跑 / success 全过 / failure 有失败。</summary>
    [JsonIgnore]
    public string Ci =>
        Checks.Count == 0 ? "none"
        : Checks.Any(c => c.State == "fail") ? "failure"
        : Checks.Any(c => c.State == "pending") ? "pending"
        : "success";

    [JsonIgnore] public bool Open => State == "OPEN";

    public static PrStatus Parse(JsonElement e)
    {
        string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int Count(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
        var checks = new List<PrCheck>();
        if (e.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in rollup.EnumerateArray())
            {
                checks.Add(ParseCheck(c));
            }
        }
        return new PrStatus
        {
            Number = e.TryGetProperty("number", out var n) && n.TryGetInt32(out var num) ? num : 0,
            Title = S("title"),
            Url = S("url"),
            State = S("state") is { Length: > 0 } st ? st : "OPEN",
            Draft = e.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True,
            Mergeable = S("mergeable") is { Length: > 0 } m ? m : "UNKNOWN",
            Review = S("reviewDecision"),
            Head = S("headRefName"),
            Base = S("baseRefName"),
            Checks = checks,
            Comments = Count("comments") + Count("reviews"),
        };
    }

    // statusCheckRollup 里有两种：CheckRun（GitHub Actions 等，status + conclusion）和 StatusContext（老式提交状态，state）
    private static PrCheck ParseCheck(JsonElement c)
    {
        string S(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var name = S("name") is { Length: > 0 } n ? n : S("context");
        var url = S("detailsUrl") is { Length: > 0 } u ? u : S("targetUrl");
        string state;
        if (S("__typename") == "StatusContext" || (S("state").Length > 0 && S("status").Length == 0))
        {
            state = S("state").ToUpperInvariant() switch
            {
                "SUCCESS" => "pass",
                "FAILURE" or "ERROR" => "fail",
                _ => "pending",
            };
        }
        else if (!S("status").Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            state = "pending";
        }
        else
        {
            state = S("conclusion").ToUpperInvariant() switch
            {
                "SUCCESS" or "NEUTRAL" => "pass",
                "SKIPPED" => "skip",
                "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" or "STALE" => "fail",
                _ => "pending",
            };
        }
        return new PrCheck(name, state, url);
    }

    /// <summary>给人和模型看的一段状态说明。</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PR #{Number}「{Title}」：{StateText}{(Draft ? "（草稿）" : "")}，{Head} → {Base}");
        sb.AppendLine($"地址：{Url}");
        sb.AppendLine($"CI：{CiText}" + (Checks.Count > 0 ? $"（{Checks.Count(c => c.State == "pass")} 过 / {Checks.Count(c => c.State == "fail")} 失败 / {Checks.Count(c => c.State == "pending")} 进行中）" : ""));
        foreach (var c in Checks.Where(c => c.State == "fail"))
        {
            sb.AppendLine($"  ✗ {c.Name} {c.Url}");
        }
        sb.AppendLine($"合并：{(Mergeable == "CONFLICTING" ? "和目标分支有冲突" : Mergeable == "MERGEABLE" ? "可以合并" : "未知")}；评审：{ReviewText}；评论 {Comments} 条");
        return sb.ToString().TrimEnd();
    }

    [JsonIgnore] public string StateText => State switch { "MERGED" => "已合并", "CLOSED" => "已关闭", _ => "进行中" };

    [JsonIgnore] public string CiText => Ci switch { "failure" => "有失败", "pending" => "还在跑", "success" => "全部通过", _ => "没有检查" };

    [JsonIgnore]
    public string ReviewText => Review switch
    {
        "APPROVED" => "已批准",
        "CHANGES_REQUESTED" => "要求修改",
        "REVIEW_REQUIRED" => "等待评审",
        _ => "无",
    };

    public string ToJson() => JsonSerializer.Serialize(this, PrJson.Options);

    public static PrStatus? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<PrStatus>(json, PrJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class PrJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>两次检查之间 PR 发生的一件事。</summary>
/// <param name="Kind">ci_failed / ci_passed / conflict / changes_requested / approved / new_comments / merged / closed。</param>
public sealed record PrEvent(string Kind, string Text)
{
    /// <summary>这件事要不要 AI 动手（开着自动修复时）：CI 失败、冲突、要求修改、新评论。</summary>
    public bool Actionable => Kind is "ci_failed" or "conflict" or "changes_requested" or "new_comments";
}

/// <summary>盯着的一个 PR（存在本机数据库里，按对话归属）。</summary>
public sealed record PrWatch
{
    public string Id { get; init; } = "pw_" + Guid.NewGuid().ToString("N")[..12];
    public string ConversationId { get; init; } = "";
    public string RepoDir { get; init; } = "";
    public int Number { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>上次检查到的状态。</summary>
    public PrStatus? Last { get; init; }

    /// <summary>CI 失败、冲突、要求修改时自动在这个对话里开一轮去修（操作照常要员工确认）。</summary>
    public bool AutoFix { get; init; } = true;

    public int IntervalMinutes { get; init; } = PrWatchPolicy.DefaultIntervalMinutes;
    public DateTimeOffset NextCheckAt { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public string LastError { get; init; } = "";

    /// <summary>这次盯 PR 以来自动开了几轮修复（防止无休止地自己改自己）。</summary>
    public int AutoFixes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>不再盯了（merged / closed / stopped / expired）。</summary>
    public string EndedReason { get; init; } = "";
    public bool Active => EndedReason.Length == 0;
}

/// <summary>
/// 盯 PR 的规矩（同 Claude Code 盯 PR）：每 50 分钟查一次 CI、合并状态和评审；
/// 合并或关闭了就不再盯；盯满 14 天自动停；查不到（网络、gh 掉登录）不算变化，下次再查。
/// 自动修复每个 PR 最多 <see cref="MaxAutoFixes"/> 轮——再修不好要人来看，免得自己改自己没完没了。
/// </summary>
public static class PrWatchPolicy
{
    public const int DefaultIntervalMinutes = 50;
    public const int MaxAutoFixes = 5;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>和上次比，发生了哪些事。第一次检查（before 为空）只报已经存在的问题。</summary>
    public static IReadOnlyList<PrEvent> Changes(PrStatus? before, PrStatus after)
    {
        var events = new List<PrEvent>();
        if (after.State == "MERGED" && before?.State != "MERGED")
        {
            events.Add(new PrEvent("merged", $"PR #{after.Number} 已合并"));
            return events;
        }
        if (after.State == "CLOSED" && before?.State != "CLOSED")
        {
            events.Add(new PrEvent("closed", $"PR #{after.Number} 已关闭"));
            return events;
        }
        if (!after.Open)
        {
            return events;
        }
        var failedNow = after.Checks.Where(c => c.State == "fail").Select(c => c.Name).ToHashSet();
        var failedBefore = before?.Checks.Where(c => c.State == "fail").Select(c => c.Name).ToHashSet() ?? new HashSet<string>();
        if (failedNow.Count > 0 && !failedNow.SetEquals(failedBefore))
        {
            events.Add(new PrEvent("ci_failed", $"CI 失败：{string.Join("、", failedNow)}"));
        }
        else if (after.Ci == "success" && before is not null && before.Ci != "success")
        {
            events.Add(new PrEvent("ci_passed", "CI 全部通过"));
        }
        if (after.Mergeable == "CONFLICTING" && before?.Mergeable != "CONFLICTING")
        {
            events.Add(new PrEvent("conflict", $"和目标分支 {after.Base} 有冲突"));
        }
        if (after.Review == "CHANGES_REQUESTED" && before?.Review != "CHANGES_REQUESTED")
        {
            events.Add(new PrEvent("changes_requested", "评审要求修改"));
        }
        else if (after.Review == "APPROVED" && before is not null && before.Review != "APPROVED")
        {
            events.Add(new PrEvent("approved", "评审已批准"));
        }
        if (before is not null && after.Comments > before.Comments)
        {
            events.Add(new PrEvent("new_comments", $"有 {after.Comments - before.Comments} 条新的评论或评审"));
        }
        return events;
    }

    /// <summary>
    /// 查完之后这个 PR 怎么办：更新状态、算下次什么时候查、要不要停。
    /// 查失败（status 为空）时保留上次的状态，照常排下次。
    /// </summary>
    public static PrWatch Next(PrWatch watch, PrStatus? status, string error, DateTimeOffset now)
    {
        var updated = watch with
        {
            Last = status ?? watch.Last,
            Title = status?.Title is { Length: > 0 } t ? t : watch.Title,
            Url = status?.Url is { Length: > 0 } u ? u : watch.Url,
            LastCheckedAt = now,
            LastError = error,
            NextCheckAt = now.AddMinutes(Math.Max(5, watch.IntervalMinutes)),
        };
        if (status is { State: "MERGED" })
        {
            return updated with { EndedReason = "merged" };
        }
        if (status is { State: "CLOSED" })
        {
            return updated with { EndedReason = "closed" };
        }
        if (now - watch.CreatedAt > MaxAge)
        {
            return updated with { EndedReason = "expired" };
        }
        return updated;
    }

    /// <summary>开着自动修复、有要动手的事、还没超过轮数：返回发给 AI 的那句话，否则 null。</summary>
    public static string? FixPrompt(PrWatch watch, PrStatus status, IReadOnlyList<PrEvent> events)
    {
        var todo = events.Where(e => e.Actionable).ToList();
        if (!watch.AutoFix || todo.Count == 0 || watch.AutoFixes >= MaxAutoFixes || !status.Open)
        {
            return null;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"（自动检查 PR，第 {watch.AutoFixes + 1}/{MaxAutoFixes} 轮自动修复）PR #{status.Number} 有需要处理的情况：{string.Join("；", todo.Select(e => e.Text))}。");
        sb.AppendLine();
        sb.AppendLine(status.Describe());
        sb.AppendLine();
        sb.AppendLine("请按顺序处理：");
        sb.AppendLine("1. 有冲突：把目标分支合并进来（git fetch 后 git merge），解决冲突。不要 rebase、不要 push --force。");
        sb.AppendLine("2. CI 失败：先看失败日志（run_shell 执行 gh run view <运行编号> --log-failed，或者 gh pr checks），找到根本原因再改；" +
            "先在本地复现、修好后跑同一项检查确认通过。不要跳过、禁用测试来让 CI 变绿。失败如果和这次改动无关（目标分支本来就是红的），说明情况，不要乱改。");
        sb.AppendLine("3. 评审意见：用 gh pr view --comments 看具体意见，能改的小问题直接改，大的设计改动先说明方案、等员工决定。");
        sb.AppendLine("改完用 git_commit 提交、git push 推送（推送照常要员工确认），最后用一两句话说明修了什么、为什么。");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>盯着的 PR 存在本机数据库（和对话同一个库）。</summary>
public sealed class PrWatchStore
{
    private readonly Func<SqliteConnection> _open;

    public PrWatchStore(Func<SqliteConnection> open) => _open = open;

    public static void Migrate(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pr_watches (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL DEFAULT '',
                repo_dir TEXT NOT NULL DEFAULT '',
                number INTEGER NOT NULL DEFAULT 0,
                url TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                last_status TEXT NULL,
                auto_fix INTEGER NOT NULL DEFAULT 1,
                interval_minutes INTEGER NOT NULL DEFAULT 50,
                next_check_at TEXT NOT NULL,
                last_checked_at TEXT NULL,
                last_error TEXT NOT NULL DEFAULT '',
                auto_fixes INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                ended_reason TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_pr_watches_next ON pr_watches (ended_reason, next_check_at);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>开始盯（同一个对话、同一个仓库、同一个 PR 已经在盯就更新那一条，不重复）。</summary>
    public PrWatch Watch(PrWatch watch)
    {
        var existing = All().FirstOrDefault(w => w.Active && w.ConversationId == watch.ConversationId
            && w.Number == watch.Number && string.Equals(w.RepoDir, watch.RepoDir, StringComparison.OrdinalIgnoreCase));
        var saved = existing is null ? watch : watch with { Id = existing.Id, CreatedAt = existing.CreatedAt, AutoFixes = existing.AutoFixes };
        Save(saved);
        return saved;
    }

    public void Save(PrWatch w)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pr_watches (id, conversation_id, repo_dir, number, url, title, last_status, auto_fix, interval_minutes,
                next_check_at, last_checked_at, last_error, auto_fixes, created_at, ended_reason)
            VALUES ($id, $conv, $dir, $num, $url, $title, $last, $fix, $interval, $next, $checked, $err, $fixes, $created, $ended)
            ON CONFLICT(id) DO UPDATE SET url = $url, title = $title, last_status = $last, auto_fix = $fix, interval_minutes = $interval,
                next_check_at = $next, last_checked_at = $checked, last_error = $err, auto_fixes = $fixes, ended_reason = $ended
            """;
        cmd.Parameters.AddWithValue("$id", w.Id);
        cmd.Parameters.AddWithValue("$conv", w.ConversationId);
        cmd.Parameters.AddWithValue("$dir", w.RepoDir);
        cmd.Parameters.AddWithValue("$num", w.Number);
        cmd.Parameters.AddWithValue("$url", w.Url);
        cmd.Parameters.AddWithValue("$title", w.Title);
        cmd.Parameters.AddWithValue("$last", (object?)w.Last?.ToJson() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fix", w.AutoFix ? 1 : 0);
        cmd.Parameters.AddWithValue("$interval", w.IntervalMinutes);
        cmd.Parameters.AddWithValue("$next", w.NextCheckAt.ToString("O"));
        cmd.Parameters.AddWithValue("$checked", (object?)w.LastCheckedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", w.LastError);
        cmd.Parameters.AddWithValue("$fixes", w.AutoFixes);
        cmd.Parameters.AddWithValue("$created", w.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$ended", w.EndedReason);
        cmd.ExecuteNonQuery();
    }

    public PrWatch? Get(string id) => Query("WHERE id = $p", id).FirstOrDefault();

    /// <summary>这个对话盯过的 PR（含已结束的），新的在前。</summary>
    public IReadOnlyList<PrWatch> OfConversation(string conversationId) => Query("WHERE conversation_id = $p ORDER BY created_at DESC", conversationId);

    /// <summary>到点该查的。</summary>
    public IReadOnlyList<PrWatch> Due(DateTimeOffset now) =>
        Query("WHERE ended_reason = '' ORDER BY next_check_at", null).Where(w => w.NextCheckAt <= now).ToList();

    public IReadOnlyList<PrWatch> All() => Query("ORDER BY created_at DESC", null);

    private List<PrWatch> Query(string where, string? param)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, conversation_id, repo_dir, number, url, title, last_status, auto_fix, interval_minutes, next_check_at,
                last_checked_at, last_error, auto_fixes, created_at, ended_reason FROM pr_watches {where}
            """;
        if (param is not null)
        {
            cmd.Parameters.AddWithValue("$p", param);
        }
        using var r = cmd.ExecuteReader();
        var list = new List<PrWatch>();
        while (r.Read())
        {
            list.Add(new PrWatch
            {
                Id = r.GetString(0),
                ConversationId = r.GetString(1),
                RepoDir = r.GetString(2),
                Number = r.GetInt32(3),
                Url = r.GetString(4),
                Title = r.GetString(5),
                Last = PrStatus.FromJson(r.IsDBNull(6) ? null : r.GetString(6)),
                AutoFix = r.GetInt32(7) != 0,
                IntervalMinutes = r.GetInt32(8),
                NextCheckAt = DateTimeOffset.Parse(r.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                LastCheckedAt = r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10), System.Globalization.CultureInfo.InvariantCulture),
                LastError = r.GetString(11),
                AutoFixes = r.GetInt32(12),
                CreatedAt = DateTimeOffset.Parse(r.GetString(13), System.Globalization.CultureInfo.InvariantCulture),
                EndedReason = r.GetString(14),
            });
        }
        return list;
    }
}

/// <summary>一次检查的结果：更新后的记录、发生的事、要不要开一轮修复（发给 AI 的话）。</summary>
public sealed record PrCheckOutcome(PrWatch Watch, IReadOnlyList<PrEvent> Events, string? FixPrompt);

/// <summary>
/// 查盯着的 PR：到点的逐个用 gh 查一遍，和上次比出发生了什么，存回去。查不到不算变化（保留上次状态，下次再查）。
/// 宿主拿结果去通知员工、按需开修复；这里不碰界面，方便测试。
/// </summary>
public sealed class PrWatcher : IPrWatchRegistry
{
    private readonly PrWatchStore _store;
    private readonly Func<string, int, CancellationToken, Task<PrStatus>> _view;
    private readonly Func<DateTimeOffset> _now;

    public PrWatcher(PrWatchStore store, Func<string, int, CancellationToken, Task<PrStatus>>? view = null, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _view = view ?? ((dir, n, ct) => GhCli.ViewAsync(dir, n, ct));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public PrWatchStore Store => _store;

    /// <summary>有新的 PR 开始被盯（界面刷新列表用）。</summary>
    public event Action<PrWatch>? Watched;

    public PrWatch Watch(string conversationId, string repoDir, PrStatus status, bool autoFix)
    {
        var now = _now();
        var watch = _store.Watch(new PrWatch
        {
            ConversationId = conversationId,
            RepoDir = repoDir,
            Number = status.Number,
            Url = status.Url,
            Title = status.Title,
            Last = status,
            AutoFix = autoFix,
            LastCheckedAt = now,
            NextCheckAt = now.AddMinutes(PrWatchPolicy.DefaultIntervalMinutes),
            CreatedAt = now,
        });
        Watched?.Invoke(watch);
        return watch;
    }

    public async Task<IReadOnlyList<PrCheckOutcome>> CheckDueAsync(CancellationToken ct)
    {
        var outcomes = new List<PrCheckOutcome>();
        foreach (var w in _store.Due(_now()))
        {
            outcomes.Add(await CheckAsync(w, ct));
        }
        return outcomes;
    }

    public async Task<PrCheckOutcome> CheckAsync(PrWatch watch, CancellationToken ct)
    {
        PrStatus? status = null;
        var error = "";
        try
        {
            status = await _view(watch.RepoDir, watch.Number, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        var events = status is null ? Array.Empty<PrEvent>() : PrWatchPolicy.Changes(watch.Last, status);
        var fix = status is null ? null : PrWatchPolicy.FixPrompt(watch, status, events);
        var next = PrWatchPolicy.Next(watch, status, error, _now());
        if (fix is not null)
        {
            next = next with { AutoFixes = watch.AutoFixes + 1 };
        }
        _store.Save(next);
        return new PrCheckOutcome(next, events, fix);
    }

    public PrWatch? Stop(string id)
    {
        if (_store.Get(id) is not { Active: true } w)
        {
            return null;
        }
        var stopped = w with { EndedReason = "stopped" };
        _store.Save(stopped);
        return stopped;
    }

    public PrWatch? SetAutoFix(string id, bool on)
    {
        if (_store.Get(id) is not { } w)
        {
            return null;
        }
        var updated = w with { AutoFix = on };
        _store.Save(updated);
        return updated;
    }
}
