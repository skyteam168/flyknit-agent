using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Security;

/// <summary>
/// 任务授权记录：不是员工坐在电脑前发起的任务（手机、钉钉 / 企业微信、IT 指令、定时任务、事件触发），
/// 开始前记一份「谁让做、做到哪」。这一轮运行的权限按 <see cref="Permission"/> 封顶、工作区固定为 <see cref="Workspace"/>。
///
/// 只插入、不修改（store 里没有更新的方法）；同时报给服务端，服务端那份也只插入、通道来源的发起人和原话由服务端核对。
/// 同一个对话里后面再来的远程消息另起一份，权限上限取「这个对话上一份」和「对话现在的权限」里小的那个——
/// 后续消息扩大不了授权（员工在电脑上自己发消息不走授权，不受影响）。
/// </summary>
public sealed record TaskGrant
{
    /// <summary>pr：盯 PR 的自动修复；folder：文件夹监视（以前借用 event）。</summary>
    public static readonly string[] Origins = { "mobile", "dingtalk", "wecom", "instruction", "schedule", "event", "folder", "pr" };

    public string Id { get; init; } = "g" + Guid.NewGuid().ToString("N");
    public string ConversationId { get; init; } = "";
    public string Origin { get; init; } = "";
    public string Reference { get; init; } = "";
    public string RequestedBy { get; init; } = "";
    public string Instruction { get; init; } = "";
    public PermissionMode Permission { get; init; } = PermissionMode.ReadOnly;
    public string Workspace { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>这一轮实际能用的权限：不超过授权的。</summary>
    public PermissionMode Cap(PermissionMode requested) => (PermissionMode)Math.Min((int)requested, (int)Permission);

    /// <summary>
    /// 新起一份授权。<paramref name="previous"/> 是这个对话上一份授权：有的话权限上限不超过它，工作区沿用它的。
    /// </summary>
    public static TaskGrant Issue(string origin, string conversationId, string reference, string requestedBy, string instruction,
        PermissionMode permission, string workspace, TaskGrant? previous)
    {
        if (!Origins.Contains(origin))
        {
            throw new ArgumentException($"不认识的任务来源：{origin}", nameof(origin));
        }
        return new TaskGrant
        {
            ConversationId = conversationId,
            Origin = origin,
            Reference = reference,
            RequestedBy = requestedBy,
            Instruction = instruction.Length > 20000 ? instruction[..20000] : instruction,
            Permission = previous is null ? permission : previous.Cap(permission),
            Workspace = previous is null || previous.Workspace.Length == 0 ? workspace : previous.Workspace,
        };
    }
}

/// <summary>授权记录的本地存储（和会话同一个 SQLite 文件）。只有插入和查询。</summary>
public sealed class TaskGrantStore
{
    private readonly Func<SqliteConnection> _open;

    public TaskGrantStore(Func<SqliteConnection> open) => _open = open;

    public static void Migrate(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS task_grants (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL DEFAULT '',
                origin TEXT NOT NULL DEFAULT '',
                reference TEXT NOT NULL DEFAULT '',
                requested_by TEXT NOT NULL DEFAULT '',
                instruction TEXT NOT NULL DEFAULT '',
                permission TEXT NOT NULL DEFAULT 'readonly',
                workspace TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_task_grants_conversation ON task_grants (conversation_id, created_at);
            -- 只插入：库里也拦住改和删（防止别的代码路径误改；有人直接动数据库文件拦不住，所以服务端另存一份）
            CREATE TRIGGER IF NOT EXISTS task_grants_no_update BEFORE UPDATE ON task_grants
                BEGIN SELECT RAISE(ABORT, 'task_grants is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS task_grants_no_delete BEFORE DELETE ON task_grants
                BEGIN SELECT RAISE(ABORT, 'task_grants is append-only'); END;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Add(TaskGrant g)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task_grants (id, conversation_id, origin, reference, requested_by, instruction, permission, workspace, created_at)
            VALUES ($id, $conv, $origin, $ref, $by, $ins, $perm, $ws, $at)
            """;
        cmd.Parameters.AddWithValue("$id", g.Id);
        cmd.Parameters.AddWithValue("$conv", g.ConversationId);
        cmd.Parameters.AddWithValue("$origin", g.Origin);
        cmd.Parameters.AddWithValue("$ref", g.Reference);
        cmd.Parameters.AddWithValue("$by", g.RequestedBy);
        cmd.Parameters.AddWithValue("$ins", g.Instruction);
        cmd.Parameters.AddWithValue("$perm", PermissionModes.ToText(g.Permission));
        cmd.Parameters.AddWithValue("$ws", g.Workspace);
        cmd.Parameters.AddWithValue("$at", g.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>这个对话最近的一份授权。</summary>
    public TaskGrant? Latest(string conversationId) =>
        One("WHERE conversation_id = $p ORDER BY created_at DESC, rowid DESC LIMIT 1", conversationId);

    /// <summary>按编号取一份授权（任务中心显示任务按什么授权在跑）。</summary>
    public TaskGrant? Get(string id) => One("WHERE id = $p", id);

    private TaskGrant? One(string where, string param)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, conversation_id, origin, reference, requested_by, instruction, permission, workspace, created_at
            FROM task_grants {where}
            """;
        cmd.Parameters.AddWithValue("$p", param);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
        return new TaskGrant
        {
            Id = r.GetString(0),
            ConversationId = r.GetString(1),
            Origin = r.GetString(2),
            Reference = r.GetString(3),
            RequestedBy = r.GetString(4),
            Instruction = r.GetString(5),
            Permission = PermissionModes.Parse(r.GetString(6)),
            Workspace = r.GetString(7),
            CreatedAt = DateTimeOffset.Parse(r.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
