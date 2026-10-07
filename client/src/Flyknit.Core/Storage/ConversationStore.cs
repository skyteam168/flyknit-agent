using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Storage;

public sealed class Conversation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";

    /// <summary>auto：AI 生成；manual：用户改过，不再自动覆盖。</summary>
    public string TitleSource { get; set; } = "auto";

    public ConversationMode Mode { get; set; } = ConversationMode.Agent;
    public bool Pinned { get; set; }
    public string TranslateFrom { get; set; } = "auto";
    public string TranslateTo { get; set; } = "vi";

    /// <summary>用户为该会话选择的模型（服务端模型 ID），为空表示自动。</summary>
    public int? ModelId { get; set; }

    /// <summary>办事模式的工作区目录，为空时使用默认工作区。</summary>
    public string? Workspace { get; set; }

    public Security.PermissionMode Permission { get; set; } = Security.PermissionMode.Workspace;

    /// <summary>较早对话的压缩摘要，以及它覆盖到的最后一条消息 ID。</summary>
    public string? Summary { get; set; }
    public string? SummaryUpto { get; set; }

    /// <summary>update_plan 写下的任务计划（JSON），跟着对话保存：下一轮、压缩之后都还在。</summary>
    public string? Plan { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? DeletedAt { get; set; }
    public int MessageCount { get; set; }
}

/// <summary>一条安全记录：被阻止的危险命令，或用户放行过的操作。</summary>
public sealed class SecurityEvent
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string ConversationTitle { get; set; } = "";

    /// <summary>发生在哪种模式：agent / chat / translate。</summary>
    public string Scene { get; set; } = "";

    public string Tool { get; set; } = "";

    /// <summary>命令原文或操作对象。</summary>
    public string Detail { get; set; } = "";

    /// <summary>blocked 阻止 / approved 用户放行 / remembered 记住后自动放行 / rejected 用户拒绝。</summary>
    public string Decision { get; set; } = "";

    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>会话与消息的本地存储（SQLite）。删除为软删除，回收站保留 30 天。</summary>
/// <summary>一轮办事任务的结果（只有数字）。</summary>
public sealed record RunStatsRecord(string MessageId, string ConversationId, string StopReason, int Steps, int ToolCalls,
    int OutputProblems, int OutputProblemsAtEnd, bool PlanGuidance, bool VerifyOutputs, bool PlanNudged, DateTimeOffset? At = null);

/// <summary>任务效果统计，见 <see cref="ConversationStore.RunStats"/>。</summary>
public sealed record RunStatsSummary(int Days, int Runs, int Completed, int Paused, int Cancelled, int Errors, double AvgSteps,
    int Disliked, int Liked, int RunsWithOutputProblems, int RunsWithProblemsAtEnd, int PlanNudges)
{
    public double CompletionRate => Runs == 0 ? 0 : Math.Round((double)Completed / Runs, 2);
}

public sealed class ConversationStore
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public ConversationStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        // 不使用共享缓存：多线程同时读写时共享缓存会出现表锁等待；WAL 模式本身支持并发读写
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 5 }.ToString();
        Migrate();
    }

    /// <summary>定时任务与会话共用同一个数据库文件。</summary>
    public Scheduling.ScheduledTaskStore Schedules => _schedules ??= new Scheduling.ScheduledTaskStore(Open);

    private Scheduling.ScheduledTaskStore? _schedules;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL DEFAULT '',
                title_source TEXT NOT NULL DEFAULT 'auto',
                mode TEXT NOT NULL DEFAULT 'agent',
                pinned INTEGER NOT NULL DEFAULT 0,
                translate_from TEXT NOT NULL DEFAULT 'auto',
                translate_to TEXT NOT NULL DEFAULT 'vi',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL DEFAULT '',
                reasoning TEXT NULL,
                tool_calls TEXT NULL,
                tool_call_id TEXT NULL,
                tool_name TEXT NULL,
                attachments TEXT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_conv ON messages(conversation_id, seq);
            CREATE INDEX IF NOT EXISTS ix_conv_updated ON conversations(updated_at);
            """;
        cmd.ExecuteNonQuery();

        // v0.2：会话记录所选模型；v0.3：工作区、权限、回答评价
        AddColumn(c, "conversations", "model_id", "INTEGER NULL");
        AddColumn(c, "conversations", "workspace", "TEXT NULL");
        AddColumn(c, "conversations", "permission", "TEXT NOT NULL DEFAULT 'workspace'");
        AddColumn(c, "messages", "feedback", "INTEGER NULL");
        // v0.4：上下文压缩摘要、模型与 token 用量
        AddColumn(c, "conversations", "summary", "TEXT NULL");
        AddColumn(c, "conversations", "summary_upto", "TEXT NULL");
        AddColumn(c, "conversations", "plan", "TEXT NULL");
        AddColumn(c, "messages", "model", "TEXT NULL");
        AddColumn(c, "messages", "prompt_tokens", "INTEGER NULL");
        AddColumn(c, "messages", "completion_tokens", "INTEGER NULL");
        AddColumn(c, "messages", "outputs", "TEXT NULL");
        AddColumn(c, "messages", "trace", "TEXT NULL");

        // v0.5：安全记录（危险命令拦截与放行）
        using var sec = c.CreateCommand();
        sec.CommandText = """
            CREATE TABLE IF NOT EXISTS security_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL DEFAULT '',
                scene TEXT NOT NULL DEFAULT '',
                tool TEXT NOT NULL DEFAULT '',
                detail TEXT NOT NULL DEFAULT '',
                decision TEXT NOT NULL DEFAULT '',
                reason TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_security_created ON security_events(created_at);
            """;
        sec.ExecuteNonQuery();

        // v0.6：定时任务
        Scheduling.ScheduledTaskStore.Migrate(c);

        // v0.7：每轮任务的结果（任务效果统计：完成率、步数、产出文件问题，打开/关闭规划提醒和产出检查前后对比用）
        using var runs = c.CreateCommand();
        runs.CommandText = """
            CREATE TABLE IF NOT EXISTS run_stats (
                message_id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                stop_reason TEXT NOT NULL,
                steps INTEGER NOT NULL,
                tool_calls INTEGER NOT NULL,
                output_problems INTEGER NOT NULL DEFAULT 0,
                output_problems_at_end INTEGER NOT NULL DEFAULT 0,
                plan_guidance INTEGER NOT NULL DEFAULT 0,
                verify_outputs INTEGER NOT NULL DEFAULT 0,
                plan_nudged INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_run_stats_created ON run_stats(created_at);
            """;
        runs.ExecuteNonQuery();
    }

    /// <summary>记一轮办事任务的结果。只记数字，不记内容。</summary>
    public void AddRunStats(RunStatsRecord r)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO run_stats(message_id, conversation_id, stop_reason, steps, tool_calls, output_problems,
                output_problems_at_end, plan_guidance, verify_outputs, plan_nudged, created_at)
            VALUES ($m, $c, $r, $s, $t, $p, $pe, $pg, $vo, $pn, $at)
            """;
        cmd.Parameters.AddWithValue("$m", r.MessageId);
        cmd.Parameters.AddWithValue("$c", r.ConversationId);
        cmd.Parameters.AddWithValue("$r", r.StopReason);
        cmd.Parameters.AddWithValue("$s", r.Steps);
        cmd.Parameters.AddWithValue("$t", r.ToolCalls);
        cmd.Parameters.AddWithValue("$p", r.OutputProblems);
        cmd.Parameters.AddWithValue("$pe", r.OutputProblemsAtEnd);
        cmd.Parameters.AddWithValue("$pg", r.PlanGuidance ? 1 : 0);
        cmd.Parameters.AddWithValue("$vo", r.VerifyOutputs ? 1 : 0);
        cmd.Parameters.AddWithValue("$pn", r.PlanNudged ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", (r.At ?? DateTimeOffset.Now).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 最近 <paramref name="days"/> 天办事任务的效果：完成、被暂停（步数用完、连续失败、空转）、被用户停止的次数，
    /// 平均步数，被点踩的次数，产出文件检查发现的问题。
    /// </summary>
    public RunStatsSummary RunStats(int days = 30)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN r.stop_reason = 'Completed' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN r.stop_reason IN ('MaxSteps', 'TooManyFailures', 'Stuck') THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN r.stop_reason = 'Cancelled' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN r.stop_reason = 'Failed' THEN 1 ELSE 0 END), 0),
                   COALESCE(AVG(r.steps), 0),
                   COALESCE(SUM(CASE WHEN m.feedback < 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN m.feedback > 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN r.output_problems > 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN r.output_problems_at_end > 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(r.plan_nudged), 0)
            FROM run_stats r LEFT JOIN messages m ON m.id = r.message_id
            WHERE r.created_at >= $since
            """;
        cmd.Parameters.AddWithValue("$since", DateTimeOffset.Now.AddDays(-days).ToString("O"));
        using var x = cmd.ExecuteReader();
        x.Read();
        return new RunStatsSummary(days, x.GetInt32(0), x.GetInt32(1), x.GetInt32(2), x.GetInt32(3), x.GetInt32(4),
            Math.Round(x.GetDouble(5), 1), x.GetInt32(6), x.GetInt32(7), x.GetInt32(8), x.GetInt32(9), x.GetInt32(10));
    }

    private static void AddColumn(SqliteConnection c, string table, string column, string definition)
    {
        using var info = c.CreateCommand();
        info.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt64(info.ExecuteScalar()) == 0)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();
        }
    }

    public Conversation Create(
        ConversationMode mode,
        string title = "",
        int? modelId = null,
        string? workspace = null,
        Security.PermissionMode permission = Security.PermissionMode.Workspace)
    {
        var conv = new Conversation { Mode = mode, Title = title, ModelId = modelId, Workspace = workspace, Permission = permission };
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations (id, title, title_source, mode, pinned, translate_from, translate_to, model_id, workspace, permission, created_at, updated_at)
            VALUES ($id, $title, 'auto', $mode, 0, $from, $to, $model, $ws, $perm, $created, $updated)
            """;
        cmd.Parameters.AddWithValue("$model", (object?)modelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ws", (object?)workspace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$perm", Security.PermissionModes.ToText(permission));
        cmd.Parameters.AddWithValue("$id", conv.Id);
        cmd.Parameters.AddWithValue("$title", conv.Title);
        cmd.Parameters.AddWithValue("$mode", ModeToText(mode));
        cmd.Parameters.AddWithValue("$from", conv.TranslateFrom);
        cmd.Parameters.AddWithValue("$to", conv.TranslateTo);
        cmd.Parameters.AddWithValue("$created", conv.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", conv.UpdatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return conv;
    }

    public Conversation? Get(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelectConversation + " WHERE c.id = $id GROUP BY c.id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadConversation(r) : null;
    }

    /// <summary>会话列表：置顶在前，其余按更新时间倒序。query 不为空时按标题和内容搜索。</summary>
    public IReadOnlyList<Conversation> List(string? query = null, bool trash = false, int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = trash ? "c.deleted_at IS NOT NULL" : "c.deleted_at IS NULL";
        if (!string.IsNullOrWhiteSpace(query))
        {
            where += """
                 AND (c.title LIKE $q ESCAPE '\' OR EXISTS (
                    SELECT 1 FROM messages s WHERE s.conversation_id = c.id AND s.role IN ('user','assistant')
                    AND s.content LIKE $q ESCAPE '\'))
                """;
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(query.Trim()) + "%");
        }
        cmd.CommandText = $"{SelectConversation} WHERE {where} GROUP BY c.id ORDER BY c.pinned DESC, c.updated_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Conversation>();
        while (r.Read())
        {
            list.Add(ReadConversation(r));
        }
        return list;
    }

    /// <summary>用户手动改名，之后不再自动生成标题。</summary>
    public void Rename(string id, string title) => Update(id, "title = $v, title_source = 'manual'", Trim(title, 100));

    /// <summary>AI 生成的标题；用户改过名的会话不会被覆盖。</summary>
    public bool SetAutoTitle(string id, string title)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET title = $v WHERE id = $id AND title_source = 'auto'";
        cmd.Parameters.AddWithValue("$v", Trim(title, 100));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void SetPinned(string id, bool pinned) => Update(id, "pinned = $v", pinned ? 1 : 0);

    public void SetMode(string id, ConversationMode mode) => Update(id, "mode = $v", ModeToText(mode));

    public void SetModel(string id, int? modelId) => Update(id, "model_id = $v", (object?)modelId ?? DBNull.Value);

    public void SetWorkspace(string id, string? workspace) => Update(id, "workspace = $v", (object?)workspace ?? DBNull.Value);

    /// <summary>保存上下文压缩的摘要；summary 为 null 时清除。</summary>
    public void SetSummary(string id, string? summary, string? uptoMessageId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET summary = $s, summary_upto = $u WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", (object?)uptoMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>保存任务计划（JSON）；null 时清除。</summary>
    public void SetPlan(string id, string? planJson) => Update(id, "plan = $v", (object?)planJson ?? DBNull.Value);

    public void SetPermission(string id, Security.PermissionMode mode) => Update(id, "permission = $v", Security.PermissionModes.ToText(mode));

    /// <summary>给回答点赞（1）、踩（-1）或取消（null）。</summary>
    /// <summary>把本轮产出的文件记到某条回答上，重新打开会话时卡片还在。</summary>
    public void SetOutputs(string messageId, IReadOnlyList<string> paths)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE messages SET outputs = $outputs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$outputs", paths.Count > 0 ? JsonSerializer.Serialize(paths, Json) : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SetFeedback(string messageId, int? value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE messages SET feedback = $v WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", value is null ? DBNull.Value : Math.Sign(value.Value));
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除某条消息之后的所有消息（inclusive 为 true 时连同这条消息一起删除），用于重新生成和编辑后重发。
    /// 返回删除的条数；消息不存在时返回 -1。
    /// </summary>
    public int DeleteMessagesFrom(string conversationId, string messageId, bool inclusive)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        long seq;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT seq FROM messages WHERE conversation_id = $conv AND id = $id";
            q.Parameters.AddWithValue("$conv", conversationId);
            q.Parameters.AddWithValue("$id", messageId);
            var found = q.ExecuteScalar();
            if (found is null)
            {
                return -1;
            }
            seq = Convert.ToInt64(found);
        }
        int removed;
        using (var d = c.CreateCommand())
        {
            d.Transaction = tx;
            d.CommandText = $"DELETE FROM messages WHERE conversation_id = $conv AND seq {(inclusive ? ">=" : ">")} $seq";
            d.Parameters.AddWithValue("$conv", conversationId);
            d.Parameters.AddWithValue("$seq", seq);
            removed = d.ExecuteNonQuery();
        }
        using (var u = c.CreateCommand())
        {
            // 摘要覆盖的消息被删掉后，摘要也失效
            u.Transaction = tx;
            u.CommandText = """
                UPDATE conversations SET summary = NULL, summary_upto = NULL
                WHERE id = $conv AND summary_upto IS NOT NULL
                  AND summary_upto NOT IN (SELECT id FROM messages WHERE conversation_id = $conv)
                """;
            u.Parameters.AddWithValue("$conv", conversationId);
            u.ExecuteNonQuery();
        }
        tx.Commit();
        return removed;
    }

    public void SetTranslateLanguages(string id, string from, string to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET translate_from = $f, translate_to = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$f", from);
        cmd.Parameters.AddWithValue("$t", to);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>移入回收站。</summary>
    public void Delete(string id) => Update(id, "deleted_at = $v", DateTimeOffset.Now.ToString("O"));

    public void Restore(string id) => Update(id, "deleted_at = $v", DBNull.Value);

    /// <summary>彻底删除（含消息）。</summary>
    public void Purge(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>清理回收站中超过保留期的会话。</summary>
    public int PurgeExpired()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", (DateTimeOffset.Now - TrashRetention).ToString("O"));
        return cmd.ExecuteNonQuery();
    }

    public void AddMessages(string conversationId, IEnumerable<ChatMessage> messages)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        long seq;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE conversation_id = $id";
            q.Parameters.AddWithValue("$id", conversationId);
            seq = Convert.ToInt64(q.ExecuteScalar());
        }
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System)
            {
                continue; // 系统提示词每次动态生成，不保存
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO messages (id, conversation_id, seq, role, content, reasoning, tool_calls, tool_call_id, tool_name, attachments, created_at, model, prompt_tokens, completion_tokens, outputs, trace)
                VALUES ($id, $conv, $seq, $role, $content, $reasoning, $calls, $callId, $toolName, $attachments, $created, $model, $pt, $ct, $outputs, $trace)
                """;
            cmd.Parameters.AddWithValue("$id", m.Id);
            cmd.Parameters.AddWithValue("$conv", conversationId);
            cmd.Parameters.AddWithValue("$seq", ++seq);
            cmd.Parameters.AddWithValue("$role", m.Role.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$content", m.Content);
            cmd.Parameters.AddWithValue("$reasoning", (object?)m.Reasoning ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$calls", m.ToolCalls.Count > 0 ? JsonSerializer.Serialize(m.ToolCalls, Json) : DBNull.Value);
            cmd.Parameters.AddWithValue("$callId", (object?)m.ToolCallId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$toolName", (object?)m.ToolName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attachments", m.Attachments.Count > 0 ? JsonSerializer.Serialize(m.Attachments, Json) : DBNull.Value);
            cmd.Parameters.AddWithValue("$created", m.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$outputs", m.Outputs.Count > 0 ? JsonSerializer.Serialize(m.Outputs, Json) : DBNull.Value);
            cmd.Parameters.AddWithValue("$trace", (object?)m.TraceJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$model", (object?)m.ModelName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pt", (object?)m.PromptTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ct", (object?)m.CompletionTokens ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        using (var u = c.CreateCommand())
        {
            u.Transaction = tx;
            u.CommandText = "UPDATE conversations SET updated_at = $now WHERE id = $id";
            u.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            u.Parameters.AddWithValue("$id", conversationId);
            u.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 所有对话里出现过的文件：用户上传的附件和 AI 产出的文件（不含回收站里的对话）。资料库升级后补登历史时用。
    /// </summary>
    public List<(string Path, string Source, string ConversationId, DateTimeOffset At)> FileReferences()
    {
        var list = new List<(string, string, string, DateTimeOffset)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT m.conversation_id, m.created_at, m.attachments, m.outputs
            FROM messages m JOIN conversations v ON v.id = m.conversation_id
            WHERE v.deleted_at IS NULL AND (m.attachments IS NOT NULL OR m.outputs IS NOT NULL)
            ORDER BY m.created_at
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var conv = r.GetString(0);
            var at = DateTimeOffset.TryParse(r.GetString(1), out var t) ? t : DateTimeOffset.Now;
            if (!r.IsDBNull(2))
            {
                try
                {
                    foreach (var a in JsonSerializer.Deserialize<List<Attachment>>(r.GetString(2), Json) ?? new())
                    {
                        list.Add((a.LocalPath, "upload", conv, at));
                    }
                }
                catch (JsonException)
                {
                }
            }
            if (!r.IsDBNull(3))
            {
                try
                {
                    foreach (var path in JsonSerializer.Deserialize<List<string>>(r.GetString(3), Json) ?? new())
                    {
                        list.Add((path, "output", conv, at));
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        return list;
    }

    public List<ChatMessage> GetMessages(string conversationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, role, content, reasoning, tool_calls, tool_call_id, tool_name, attachments, created_at, feedback,
                   model, prompt_tokens, completion_tokens, outputs, trace
            FROM messages WHERE conversation_id = $id ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$id", conversationId);
        using var r = cmd.ExecuteReader();
        var list = new List<ChatMessage>();
        while (r.Read())
        {
            list.Add(new ChatMessage
            {
                Id = r.GetString(0),
                Role = Enum.Parse<ChatRole>(r.GetString(1), ignoreCase: true),
                Content = r.GetString(2),
                Reasoning = r.IsDBNull(3) ? null : r.GetString(3),
                ToolCalls = r.IsDBNull(4) ? new() : JsonSerializer.Deserialize<List<ToolCall>>(r.GetString(4), Json) ?? new(),
                ToolCallId = r.IsDBNull(5) ? null : r.GetString(5),
                ToolName = r.IsDBNull(6) ? null : r.GetString(6),
                Attachments = r.IsDBNull(7) ? new() : JsonSerializer.Deserialize<List<Attachment>>(r.GetString(7), Json) ?? new(),
                CreatedAt = DateTimeOffset.Parse(r.GetString(8)),
                Feedback = r.IsDBNull(9) ? null : (int)r.GetInt64(9),
                ModelName = r.IsDBNull(10) ? null : r.GetString(10),
                PromptTokens = r.IsDBNull(11) ? null : (int)r.GetInt64(11),
                CompletionTokens = r.IsDBNull(12) ? null : (int)r.GetInt64(12),
                Outputs = r.IsDBNull(13) ? new() : JsonSerializer.Deserialize<List<string>>(r.GetString(13), Json) ?? new(),
                TraceJson = r.IsDBNull(14) ? null : r.GetString(14),
            });
        }
        return list;
    }

    /// <summary>清空会话内的消息，保留会话本身。</summary>
    public void ClearMessages(string conversationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE conversation_id = $id";
        cmd.Parameters.AddWithValue("$id", conversationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>保留最近这么多条安全记录。</summary>
    public const int SecurityEventLimit = 500;

    public void AddSecurityEvent(SecurityEvent e)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO security_events (conversation_id, scene, tool, detail, decision, reason, created_at)
            VALUES ($conv, $scene, $tool, $detail, $decision, $reason, $created);
            DELETE FROM security_events WHERE id <= (SELECT MAX(id) FROM security_events) - $limit;
            """;
        cmd.Parameters.AddWithValue("$conv", e.ConversationId);
        cmd.Parameters.AddWithValue("$scene", e.Scene);
        cmd.Parameters.AddWithValue("$tool", e.Tool);
        cmd.Parameters.AddWithValue("$detail", Trim(e.Detail, 1000));
        cmd.Parameters.AddWithValue("$decision", e.Decision);
        cmd.Parameters.AddWithValue("$reason", Trim(e.Reason, 500));
        cmd.Parameters.AddWithValue("$created", e.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", SecurityEventLimit);
        cmd.ExecuteNonQuery();
    }

    /// <summary>安全记录，最新的在前。decision 为空表示全部。</summary>
    public List<SecurityEvent> ListSecurityEvents(string? decision = null, int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = string.IsNullOrEmpty(decision) ? "" : " WHERE e.decision = $d";
        cmd.CommandText = $"""
            SELECT e.id, e.conversation_id, e.scene, e.tool, e.detail, e.decision, e.reason, e.created_at,
                   COALESCE(c.title, '')
            FROM security_events e LEFT JOIN conversations c ON c.id = e.conversation_id
            {where}
            ORDER BY e.id DESC LIMIT $limit
            """;
        if (!string.IsNullOrEmpty(decision))
        {
            cmd.Parameters.AddWithValue("$d", decision);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<SecurityEvent>();
        while (r.Read())
        {
            list.Add(new SecurityEvent
            {
                Id = r.GetInt64(0),
                ConversationId = r.GetString(1),
                Scene = r.GetString(2),
                Tool = r.GetString(3),
                Detail = r.GetString(4),
                Decision = r.GetString(5),
                Reason = r.GetString(6),
                CreatedAt = DateTimeOffset.Parse(r.GetString(7)),
                ConversationTitle = r.GetString(8),
            });
        }
        return list;
    }

    /// <summary>
    /// 把安全记录写成 CSV。带 BOM 的 UTF-8，Excel 双击打开中文不乱码；
    /// 以 = + - @ 开头的单元格前面加单引号，免得 Excel 把命令原文当公式执行。
    /// </summary>
    public static void WriteSecurityEventsCsv(IEnumerable<SecurityEvent> events, TextWriter writer)
    {
        static string Cell(string value)
        {
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            {
                value = "'" + value;
            }
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        writer.Write('\uFEFF');
        writer.Write("时间,判定,模式,工具,命令或操作对象,原因,所属任务\r\n");
        foreach (var e in events)
        {
            var row = new[]
            {
                e.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                e.Decision, e.Scene, e.Tool, e.Detail, e.Reason, e.ConversationTitle,
            };
            writer.Write(string.Join(",", row.Select(Cell)));
            writer.Write("\r\n");
        }
    }

    public void ClearSecurityEvents()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM security_events";
        cmd.ExecuteNonQuery();
    }

    private const string SelectConversation = """
        SELECT c.id, c.title, c.title_source, c.mode, c.pinned, c.translate_from, c.translate_to,
               c.created_at, c.updated_at, c.deleted_at, COUNT(m.id), c.model_id, c.workspace, c.permission, c.summary, c.summary_upto, c.plan
        FROM conversations c LEFT JOIN messages m ON m.conversation_id = c.id AND m.role IN ('user','assistant')
        """;

    private static Conversation ReadConversation(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        TitleSource = r.GetString(2),
        Mode = TextToMode(r.GetString(3)),
        Pinned = r.GetInt64(4) != 0,
        TranslateFrom = r.GetString(5),
        TranslateTo = r.GetString(6),
        CreatedAt = DateTimeOffset.Parse(r.GetString(7)),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(8)),
        DeletedAt = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9)),
        MessageCount = (int)r.GetInt64(10),
        ModelId = r.IsDBNull(11) ? null : (int)r.GetInt64(11),
        Workspace = r.IsDBNull(12) ? null : r.GetString(12),
        Permission = Security.PermissionModes.Parse(r.IsDBNull(13) ? null : r.GetString(13)),
        Summary = r.IsDBNull(14) ? null : r.GetString(14),
        SummaryUpto = r.IsDBNull(15) ? null : r.GetString(15),
        Plan = r.IsDBNull(16) ? null : r.GetString(16),
    };

    private void Update(string id, string set, object value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE conversations SET {set} WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"会话不存在：{id}");
        }
    }

    public static string ModeToText(ConversationMode mode) => mode.ToString().ToLowerInvariant();

    public static ConversationMode TextToMode(string text) =>
        Enum.TryParse<ConversationMode>(text, ignoreCase: true, out var m) ? m : ConversationMode.Agent;

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Trim(string s, int max)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length > max ? s[..max] : s;
    }
}
