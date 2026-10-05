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

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? DeletedAt { get; set; }
    public int MessageCount { get; set; }
}

/// <summary>会话与消息的本地存储（SQLite）。删除为软删除，回收站保留 30 天。</summary>
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

        // v0.2：会话记录所选模型
        using var info = c.CreateCommand();
        info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('conversations') WHERE name = 'model_id'";
        if (Convert.ToInt64(info.ExecuteScalar()) == 0)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE conversations ADD COLUMN model_id INTEGER NULL";
            alter.ExecuteNonQuery();
        }
    }

    public Conversation Create(ConversationMode mode, string title = "", int? modelId = null)
    {
        var conv = new Conversation { Mode = mode, Title = title, ModelId = modelId };
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations (id, title, title_source, mode, pinned, translate_from, translate_to, model_id, created_at, updated_at)
            VALUES ($id, $title, 'auto', $mode, 0, $from, $to, $model, $created, $updated)
            """;
        cmd.Parameters.AddWithValue("$model", (object?)modelId ?? DBNull.Value);
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
                INSERT INTO messages (id, conversation_id, seq, role, content, reasoning, tool_calls, tool_call_id, tool_name, attachments, created_at)
                VALUES ($id, $conv, $seq, $role, $content, $reasoning, $calls, $callId, $toolName, $attachments, $created)
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

    public List<ChatMessage> GetMessages(string conversationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, role, content, reasoning, tool_calls, tool_call_id, tool_name, attachments, created_at
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

    private const string SelectConversation = """
        SELECT c.id, c.title, c.title_source, c.mode, c.pinned, c.translate_from, c.translate_to,
               c.created_at, c.updated_at, c.deleted_at, COUNT(m.id), c.model_id
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
