using System.Text;
using System.Text.Json;
using Flyknit.Core.Context;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Memory;

/// <summary>一次已完成任务的复盘记录（情景记忆）。</summary>
public sealed class Episode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string ConversationId { get; set; } = "";
    public string Workspace { get; set; } = "";

    /// <summary>任务类型的简短名称，例如“生成质检周报”。</summary>
    public string Title { get; set; } = "";

    /// <summary>用户的原始要求。</summary>
    public string Task { get; set; } = "";

    /// <summary>做了什么、结果如何。</summary>
    public string Summary { get; set; } = "";

    /// <summary>success / partial / failure</summary>
    public string Outcome { get; set; } = "success";

    /// <summary>下次可以直接复用的步骤。</summary>
    public string Procedure { get; set; } = "";

    /// <summary>
    /// 这次任务的教训：存的是长期记忆里那几条的 ID，正文只在记忆库里有一份（不再双写）。
    /// 记忆被删除、被合并时这里跟着变。
    /// </summary>
    public List<string> LessonIds { get; set; } = new();

    /// <summary>
    /// 教训正文：读出来时按 <see cref="LessonIds"/> 从记忆库取。新建时也可以直接给正文（旧数据、测试），
    /// 保存时换成记忆库里对应那条的 ID，记忆库里没有的不保存。
    /// </summary>
    public List<string> Lessons { get; set; } = new();
    public List<string> Tools { get; set; } = new();

    /// <summary>用户评价：1 赞、-1 踩、0 未评价。</summary>
    public int Feedback { get; set; }

    public int Uses { get; set; }

    /// <summary>任务发生的时间（事件时间）。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>最近一次被当作参考交给模型的时间。</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public string SearchText => $"{Title} {Task} {Summary}";
}


/// <summary>
/// 历史任务库（情景记忆）。和长期记忆放在同一个 SQLite 数据库里（episodes 表），
/// 以前的 episodes.json 第一次打开时导入，导入后删除。
/// 新任务开始时按相似度找出相关的历史任务，把当时的做法和教训交给模型，避免每次从头摸索。
/// </summary>
public sealed class EpisodeStore
{
    public const string LegacyFileName = "episodes.json";
    public const int MaxEpisodes = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly string _connectionString;
    private readonly object _lock = new();

    /// <summary>测试用的“现在”。</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    /// <param name="directory">记忆目录（旧版 episodes.json 所在位置）。</param>
    /// <param name="databasePath">数据库，默认和 <see cref="MemoryStore"/> 一样放在记忆目录下的 memory.db；两边要用同一个库，教训才能按 ID 取到。</param>
    public EpisodeStore(string directory, string? databasePath = null)
    {
        _directory = directory;
        databasePath ??= Path.Combine(directory, MemoryStore.DatabaseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 5, Pooling = false }.ToString();
        Migrate();
    }

    public IReadOnlyList<Episode> List()
    {
        lock (_lock)
        {
            return Load().OrderByDescending(e => e.CreatedAt).ToList();
        }
    }

    public void Add(Episode episode)
    {
        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            if (episode.LessonIds.Count == 0 && episode.Lessons.Count > 0)
            {
                episode.LessonIds = ResolveLessons(c, episode.Lessons);
            }
            Insert(c, episode);
            var count = Count(c);
            if (count > MaxEpisodes)
            {
                // 优先淘汰早期、未被复用、没有好评的记录
                using var drop = c.CreateCommand();
                drop.CommandText = """
                    DELETE FROM episodes WHERE id IN (
                        SELECT id FROM episodes ORDER BY (CASE WHEN feedback > 0 THEN 1 ELSE 0 END), uses, created_at LIMIT $n)
                    """;
                drop.Parameters.AddWithValue("$n", count - MaxEpisodes);
                drop.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM episodes WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// 用户删除一条记忆时，把历史任务里同样的内容也清掉：总结和步骤里原样出现的句子替换掉。
    /// （教训存的是记忆 ID，记忆删了自然就没了。）返回改动了几条历史任务。
    /// </summary>
    public int Forget(string text)
    {
        text = text.Trim();
        if (text.Length < 2)
        {
            return 0;
        }
        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var changed = 0;
            foreach (var e in LoadAll(c).Where(e => e.Summary.Contains(text, StringComparison.Ordinal) || e.Procedure.Contains(text, StringComparison.Ordinal)))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE episodes SET summary = $s, procedure = $p WHERE id = $id";
                cmd.Parameters.AddWithValue("$s", e.Summary.Replace(text, "（已删除）"));
                cmd.Parameters.AddWithValue("$p", e.Procedure.Replace(text, "（已删除）"));
                cmd.Parameters.AddWithValue("$id", e.Id);
                cmd.ExecuteNonQuery();
                changed++;
            }
            tx.Commit();
            return changed;
        }
    }

    /// <summary>用户对某个对话的回答点赞或点踩时，同步到该对话最近的复盘记录。</summary>
    public void SetFeedback(string conversationId, int feedback)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE episodes SET feedback = $f,
                    outcome = CASE WHEN $f < 0 AND outcome = 'success' THEN 'partial' ELSE outcome END
                WHERE id = (SELECT id FROM episodes WHERE conversation_id = $c ORDER BY created_at DESC LIMIT 1)
                """;
            cmd.Parameters.AddWithValue("$f", Math.Sign(feedback));
            cmd.Parameters.AddWithValue("$c", conversationId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>找出与当前任务相似的历史任务。被点踩、失败的任务也会返回（用于吸取教训），但排在后面。</summary>
    public List<(Episode Episode, double Score)> Search(string query, int max = 3, double minScore = 0.22, string? workspace = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new();
        }
        // “把上周做的周报再做一遍”：上周做过的排前面；算内容相关度时去掉“上周”两个字
        var range = TimeRange.Parse(query, Clock());
        var q = range?.Strip(query) ?? query;
        lock (_lock)
        {
            return Load()
                .Select(e =>
                {
                    // 标题最能代表任务类型；长文本的相关度会被长度稀释，所以取两者较大者
                    var score = Math.Max(TextSimilarity.Relevance(q, e.Title), 0.8 * TextSimilarity.Relevance(q, e.SearchText));
                    if (range is not null && range.Contains(e.CreatedAt.LocalDateTime))
                    {
                        // 只说了时间（“昨天做了什么”）也能找到
                        score = range.IsTimeOnly(query) ? Math.Max(score, minScore) + 0.3 : score * 1.3 + 0.1;
                    }
                    if (e.Outcome == "success") score *= 1.1;
                    if (e.Feedback > 0) score *= 1.15;
                    if (e.Feedback < 0) score *= 0.9;
                    if (workspace is not null && string.Equals(e.Workspace, workspace, StringComparison.OrdinalIgnoreCase)) score *= 1.1;
                    return (Episode: e, Score: score);
                })
                .Where(x => x.Score >= minScore)
                .OrderByDescending(x => x.Score)
                .Take(max)
                .ToList();
        }
    }

    /// <summary>标记被引用过的历史任务（用于淘汰排序和统计）。</summary>
    public void MarkUsed(IEnumerable<string> ids)
    {
        var set = ids.Distinct().ToList();
        if (set.Count == 0)
        {
            return;
        }
        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var id in set)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE episodes SET uses = uses + 1, last_used_at = $t WHERE id = $id";
                cmd.Parameters.AddWithValue("$t", new DateTimeOffset(Clock()).ToString("O"));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>同类任务成功的次数（用于决定是否沉淀为技能）。</summary>
    public int CountSimilarSuccesses(string title, string task, double threshold = 0.45)
    {
        lock (_lock)
        {
            return Load().Count(e => e.Outcome == "success" && e.Feedback >= 0
                                     && TextSimilarity.Relevance($"{title} {task}", $"{e.Title} {e.Task}") >= threshold);
        }
    }

    /// <summary>
    /// 放进系统提示词的“相关历史任务”。<paramref name="alreadyShown"/> 是已经在记忆区块里出现过的记忆 ID，
    /// 这些教训不再重复列出。
    /// </summary>
    public static string BuildPromptSection(IReadOnlyList<(Episode Episode, double Score)> found, IReadOnlyCollection<string>? alreadyShown = null)
    {
        if (found.Count == 0)
        {
            return "";
        }
        var sb = new StringBuilder();
        sb.AppendLine("<相关的历史任务>");
        sb.AppendLine("以下是用户以前做过的相似任务。优先沿用当时成功的做法和用户确认过的偏好，避开当时的错误；情况不同时以当前要求为准。");
        foreach (var (e, _) in found)
        {
            var outcome = e.Outcome switch { "success" => "成功", "partial" => "部分完成", _ => "失败" };
            if (e.Feedback > 0) outcome += "，用户点赞";
            if (e.Feedback < 0) outcome += "，用户不满意";
            sb.AppendLine($"### {e.Title}（{e.CreatedAt:yyyy-MM-dd}，{outcome}）");
            if (e.Task.Length > 0) sb.AppendLine($"要求：{Clip(e.Task, 300)}");
            if (e.Summary.Length > 0) sb.AppendLine($"经过：{Clip(e.Summary, 500)}");
            if (e.Procedure.Length > 0) sb.AppendLine($"可复用的步骤：\n{Clip(e.Procedure, 900)}");
            var lessons = e.LessonIds.Zip(e.Lessons)
                .Where(x => alreadyShown is null || !alreadyShown.Contains(x.First))
                .Select(x => x.Second);
            foreach (var l in lessons.Take(4)) sb.AppendLine($"教训：{l}");
        }
        sb.AppendLine("</相关的历史任务>");
        return sb.ToString();
    }

    /// <summary>统计用：条数、被复用过的条数、最近 30 天新增。</summary>
    public (int Total, int Reused, int Last30Days) Stats()
    {
        lock (_lock)
        {
            var list = Load();
            var since = new DateTimeOffset(Clock()).AddDays(-30);
            return (list.Count, list.Count(e => e.Uses > 0), list.Count(e => e.CreatedAt >= since));
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------- 数据库 ----------

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        lock (_lock)
        {
            using var c = Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS episodes (
                        id TEXT PRIMARY KEY,
                        conversation_id TEXT NOT NULL DEFAULT '',
                        workspace TEXT NOT NULL DEFAULT '',
                        title TEXT NOT NULL,
                        task TEXT NOT NULL DEFAULT '',
                        summary TEXT NOT NULL DEFAULT '',
                        outcome TEXT NOT NULL DEFAULT 'success',
                        procedure TEXT NOT NULL DEFAULT '',
                        lesson_ids TEXT NOT NULL DEFAULT '[]',
                        tools TEXT NOT NULL DEFAULT '[]',
                        feedback INTEGER NOT NULL DEFAULT 0,
                        uses INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        last_used_at TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_episodes_conversation ON episodes(conversation_id, created_at);
                    """;
                cmd.ExecuteNonQuery();
            }
            ImportLegacy(c);
        }
    }

    /// <summary>旧版的 episodes.json：导入数据库，教训换成记忆 ID，然后删掉文件（连同 .bak），不留第二份。</summary>
    private void ImportLegacy(SqliteConnection c)
    {
        var file = Path.Combine(_directory, LegacyFileName);
        if (!File.Exists(file))
        {
            return;
        }
        List<Episode> legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<List<Episode>>(File.ReadAllText(file), Json) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return; // 读不出来就不动它，下次再试；不影响使用
        }
        using (var tx = c.BeginTransaction())
        {
            foreach (var e in legacy.Where(e => e.Id.Length > 0))
            {
                if (e.LessonIds.Count == 0)
                {
                    e.LessonIds = ResolveLessons(c, e.Lessons);
                }
                Insert(c, e, ignoreExisting: true);
            }
            tx.Commit();
        }
        foreach (var f in new[] { file, file + ".bak", file + ".tmp" })
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Insert(SqliteConnection c, Episode e, bool ignoreExisting = false)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            INSERT {(ignoreExisting ? "OR IGNORE" : "OR REPLACE")} INTO episodes
                (id, conversation_id, workspace, title, task, summary, outcome, procedure, lesson_ids, tools, feedback, uses, created_at, last_used_at)
            VALUES ($id, $conv, $ws, $title, $task, $summary, $outcome, $procedure, $lessons, $tools, $feedback, $uses, $created, $used)
            """;
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$conv", e.ConversationId);
        cmd.Parameters.AddWithValue("$ws", e.Workspace);
        cmd.Parameters.AddWithValue("$title", e.Title);
        cmd.Parameters.AddWithValue("$task", e.Task);
        cmd.Parameters.AddWithValue("$summary", e.Summary);
        cmd.Parameters.AddWithValue("$outcome", e.Outcome);
        cmd.Parameters.AddWithValue("$procedure", e.Procedure);
        cmd.Parameters.AddWithValue("$lessons", JsonSerializer.Serialize(e.LessonIds.Distinct().ToList()));
        cmd.Parameters.AddWithValue("$tools", JsonSerializer.Serialize(e.Tools));
        cmd.Parameters.AddWithValue("$feedback", e.Feedback);
        cmd.Parameters.AddWithValue("$uses", e.Uses);
        cmd.Parameters.AddWithValue("$created", e.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$used", (object?)e.LastUsedAt?.ToString("O") ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM episodes";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 每次都从库里读（最多 1000 条，很快）：教训正文来自记忆库，用户删掉或合并了记忆后必须马上反映出来，不能用缓存。
    /// </summary>
    private List<Episode> Load()
    {
        using var c = Open();
        return LoadAll(c);
    }

    private static List<Episode> LoadAll(SqliteConnection c)
    {
        var lessons = LessonTexts(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, conversation_id, workspace, title, task, summary, outcome, procedure, lesson_ids, tools, feedback, uses, created_at, last_used_at
            FROM episodes
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<Episode>();
        while (r.Read())
        {
            // 教训 ID 指向的记忆被合并过：沿着“被谁取代”找到现在那条；被删了就不显示
            var ids = Ids(r.GetString(8)).Select(id => Current(id, lessons)).OfType<string>().Distinct().ToList();
            list.Add(new Episode
            {
                Id = r.GetString(0),
                ConversationId = r.GetString(1),
                Workspace = r.GetString(2),
                Title = r.GetString(3),
                Task = r.GetString(4),
                Summary = r.GetString(5),
                Outcome = r.GetString(6),
                Procedure = r.GetString(7),
                LessonIds = ids,
                Lessons = ids.Select(id => lessons[id].Text).ToList(),
                Tools = Ids(r.GetString(9)),
                Feedback = r.GetInt32(10),
                Uses = r.GetInt32(11),
                CreatedAt = DateTimeOffset.TryParse(r.GetString(12), out var at) ? at : DateTimeOffset.MinValue,
                LastUsedAt = !r.IsDBNull(13) && DateTimeOffset.TryParse(r.GetString(13), out var used) ? used : null,
            });
        }
        return list;
    }

    private static List<string> Ids(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private sealed record LessonRow(string Text, string Status, string? SupersededBy);

    /// <summary>记忆库里的经验和教训（含已被取代的，用来顺藤摸瓜）。记忆表还没建（单独使用历史任务库）时为空。</summary>
    private static Dictionary<string, LessonRow> LessonTexts(SqliteConnection c)
    {
        var map = new Dictionary<string, LessonRow>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'memory_items'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
        {
            return map;
        }
        cmd.CommandText = "SELECT id, text, status, superseded_by FROM memory_items WHERE kind IN ('lesson', 'success') AND status <> 'deleted'";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = new LessonRow(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
        }
        return map;
    }

    private static string? Current(string id, Dictionary<string, LessonRow> rows)
    {
        for (var hops = 0; hops < 20 && rows.TryGetValue(id, out var row); hops++)
        {
            if (row.Status == "active")
            {
                return id;
            }
            if (row.SupersededBy is null)
            {
                return null;
            }
            id = row.SupersededBy;
        }
        return null;
    }

    /// <summary>教训正文 → 记忆库里对应那条的 ID（说的是一回事就算）。找不到的丢掉：记忆库里没有，说明已被删除或淘汰。</summary>
    private static List<string> ResolveLessons(SqliteConnection c, IEnumerable<string> texts)
    {
        var rows = LessonTexts(c);
        var ids = new List<string>();
        foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            var match = rows.Where(x => x.Value.Text.Length > 0 && MemoryStore.IsDuplicate(x.Value.Text, text))
                .Select(x => Current(x.Key, rows))
                .FirstOrDefault(id => id is not null);
            if (match is not null && !ids.Contains(match))
            {
                ids.Add(match);
            }
        }
        return ids;
    }
}
