using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Tasks;

/// <summary>
/// 对话订阅的外部事件（docs/c1-unified-tasks.md）。已订阅的事件直接进对应的对话，不用再判断归到哪。
/// 本期只有一种：盯着的 PR（selector = pr_watches.id）。员工在「会话信息」里能看到、能取消。
/// </summary>
public sealed record ThreadSubscription(
    string Id,
    string ConversationId,
    string Kind,
    string Selector,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt,
    string EndedReason)
{
    public bool Active => EndedAt is null;
}

public static class SubscriptionKinds
{
    public const string PullRequest = "pr";
}

public sealed class ThreadSubscriptionStore
{
    private readonly Func<SqliteConnection> _open;
    private readonly Func<DateTimeOffset> _now;

    public ThreadSubscriptionStore(Func<SqliteConnection> open, Func<DateTimeOffset>? now = null)
    {
        _open = open;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static void Migrate(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS thread_subscriptions (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                selector TEXT NOT NULL,
                label TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                ended_at TEXT NULL,
                ended_reason TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_subscriptions_conversation ON thread_subscriptions (conversation_id, created_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_subscriptions_active ON thread_subscriptions (kind, selector) WHERE ended_at IS NULL;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>订阅。同一个事件源已经有进行中的订阅时，返回已有的那条（不重复订）。</summary>
    public ThreadSubscription Subscribe(string conversationId, string kind, string selector, string label)
    {
        if (Find(kind, selector) is { } existing)
        {
            return existing;
        }
        var sub = new ThreadSubscription("s" + Guid.NewGuid().ToString("N"), conversationId, kind, selector,
            label.Length > 200 ? label[..200] : label, _now(), null, "");
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO thread_subscriptions (id, conversation_id, kind, selector, label, created_at)
            VALUES ($id, $conv, $kind, $sel, $label, $at)
            """;
        cmd.Parameters.AddWithValue("$id", sub.Id);
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$sel", selector);
        cmd.Parameters.AddWithValue("$label", sub.Label);
        cmd.Parameters.AddWithValue("$at", sub.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return sub;
    }

    /// <summary>进行中的订阅（按事件源找）。</summary>
    public ThreadSubscription? Find(string kind, string selector)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, conversation_id, kind, selector, label, created_at, ended_at, ended_reason FROM thread_subscriptions WHERE kind = $kind AND selector = $sel AND ended_at IS NULL";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$sel", selector);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>结束订阅（员工取消、PR 合并或关闭、过期）。已经结束的不动。</summary>
    public bool End(string kind, string selector, string reason)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE thread_subscriptions SET ended_at = $at, ended_reason = $reason WHERE kind = $kind AND selector = $sel AND ended_at IS NULL";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$sel", selector);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$at", _now().ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>某一种的全部进行中订阅（启动时和事件源对账用）。</summary>
    public IReadOnlyList<ThreadSubscription> ActiveOfKind(string kind)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, conversation_id, kind, selector, label, created_at, ended_at, ended_reason FROM thread_subscriptions WHERE kind = $kind AND ended_at IS NULL";
        cmd.Parameters.AddWithValue("$kind", kind);
        using var r = cmd.ExecuteReader();
        var list = new List<ThreadSubscription>();
        while (r.Read())
        {
            list.Add(Read(r));
        }
        return list;
    }

    /// <summary>这个对话的订阅（进行中的在前）。</summary>
    public IReadOnlyList<ThreadSubscription> For(string conversationId)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, conversation_id, kind, selector, label, created_at, ended_at, ended_reason FROM thread_subscriptions
            WHERE conversation_id = $conv ORDER BY ended_at IS NOT NULL, created_at DESC
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        using var r = cmd.ExecuteReader();
        var list = new List<ThreadSubscription>();
        while (r.Read())
        {
            list.Add(Read(r));
        }
        return list;
    }

    private static ThreadSubscription Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
        r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
        r.GetString(7));
}
