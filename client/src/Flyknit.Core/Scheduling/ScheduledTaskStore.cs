using Flyknit.Core.Security;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Scheduling;

/// <summary>一个定时任务。到点后 Agent 会新建一个会话，按 Instructions 自动执行。</summary>
public sealed class ScheduledTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>发给 Agent 的指令，等同于用户在输入框里打的话。</summary>
    public string Instructions { get; set; } = "";

    public ScheduleSpec Schedule { get; set; } = ScheduleSpec.Manual;
    public bool Enabled { get; set; } = true;

    /// <summary>运行时使用的工作区与权限；为空时用默认工作区。</summary>
    public string? Workspace { get; set; }
    public PermissionMode Permission { get; set; } = PermissionMode.Workspace;

    /// <summary>指定模型，为空按场景自动选择。</summary>
    public int? ModelId { get; set; }

    /// <summary>电脑关机错过的任务，开机后补跑最近一次。</summary>
    public bool CatchUp { get; set; } = true;

    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>ok / failed / stopped / running / rejected（需要确认但无人应答）。</summary>
    public string LastStatus { get; set; } = "";

    public string LastSummary { get; set; } = "";

    /// <summary>最近一次运行产生的会话，点进去能看完整过程。</summary>
    public string? LastConversationId { get; set; }

    public int RunCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>定时任务的本地存储，与会话共用一个 SQLite 文件。</summary>
public sealed class ScheduledTaskStore
{
    private readonly Func<SqliteConnection> _open;

    public ScheduledTaskStore(Func<SqliteConnection> open)
    {
        _open = open;
    }

    public static void Migrate(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scheduled_tasks (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL DEFAULT '',
                instructions TEXT NOT NULL DEFAULT '',
                schedule TEXT NOT NULL DEFAULT '',
                enabled INTEGER NOT NULL DEFAULT 1,
                workspace TEXT NULL,
                permission TEXT NOT NULL DEFAULT 'workspace',
                model_id INTEGER NULL,
                catch_up INTEGER NOT NULL DEFAULT 1,
                next_run_at TEXT NULL,
                last_run_at TEXT NULL,
                last_status TEXT NOT NULL DEFAULT '',
                last_summary TEXT NOT NULL DEFAULT '',
                last_conversation_id TEXT NULL,
                run_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public List<ScheduledTask> List()
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " ORDER BY enabled DESC, next_run_at IS NULL, next_run_at, created_at";
        using var r = cmd.ExecuteReader();
        var list = new List<ScheduledTask>();
        while (r.Read())
        {
            list.Add(Read(r));
        }
        return list;
    }

    public ScheduledTask? Get(string id)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>新增或整体更新。</summary>
    public void Save(ScheduledTask task)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scheduled_tasks
                (id, name, instructions, schedule, enabled, workspace, permission, model_id, catch_up,
                 next_run_at, last_run_at, last_status, last_summary, last_conversation_id, run_count, created_at)
            VALUES ($id, $name, $ins, $sched, $enabled, $ws, $perm, $model, $catch,
                    $next, $last, $status, $summary, $conv, $count, $created)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, instructions = excluded.instructions, schedule = excluded.schedule,
                enabled = excluded.enabled, workspace = excluded.workspace, permission = excluded.permission,
                model_id = excluded.model_id, catch_up = excluded.catch_up, next_run_at = excluded.next_run_at,
                last_run_at = excluded.last_run_at, last_status = excluded.last_status,
                last_summary = excluded.last_summary, last_conversation_id = excluded.last_conversation_id,
                run_count = excluded.run_count
            """;
        cmd.Parameters.AddWithValue("$id", task.Id);
        cmd.Parameters.AddWithValue("$name", Trim(task.Name, 100));
        cmd.Parameters.AddWithValue("$ins", Trim(task.Instructions, 4000));
        cmd.Parameters.AddWithValue("$sched", task.Schedule.Serialize());
        cmd.Parameters.AddWithValue("$enabled", task.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$ws", (object?)task.Workspace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$perm", PermissionModes.ToText(task.Permission));
        cmd.Parameters.AddWithValue("$model", (object?)task.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$catch", task.CatchUp ? 1 : 0);
        cmd.Parameters.AddWithValue("$next", (object?)task.NextRunAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last", (object?)task.LastRunAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", task.LastStatus);
        cmd.Parameters.AddWithValue("$summary", Trim(task.LastSummary, 500));
        cmd.Parameters.AddWithValue("$conv", (object?)task.LastConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$count", task.RunCount);
        cmd.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM scheduled_tasks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>到点该跑的任务（含补跑错过的）。</summary>
    public List<ScheduledTask> Due(DateTimeOffset now, TimeSpan catchUpWindow)
    {
        return List().Where(t => t.Enabled && t.NextRunAt is { } next && next <= now
                                 && (t.CatchUp || now - next <= TimeSpan.FromMinutes(5))
                                 && now - next <= catchUpWindow)
            .ToList();
    }

    /// <summary>补上缺失的下次运行时间（新任务、改过规则、或错过太久跳过的）。</summary>
    public void Reschedule(ScheduledTask task, DateTimeOffset now)
    {
        task.NextRunAt = task.Enabled ? task.Schedule.NextRun(now) : null;
        Save(task);
    }

    private const string Select = """
        SELECT id, name, instructions, schedule, enabled, workspace, permission, model_id, catch_up,
               next_run_at, last_run_at, last_status, last_summary, last_conversation_id, run_count, created_at
        FROM scheduled_tasks
        """;

    private static ScheduledTask Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Instructions = r.GetString(2),
        Schedule = ScheduleSpec.Parse(r.GetString(3)),
        Enabled = r.GetInt64(4) != 0,
        Workspace = r.IsDBNull(5) ? null : r.GetString(5),
        Permission = PermissionModes.Parse(r.GetString(6)),
        ModelId = r.IsDBNull(7) ? null : (int)r.GetInt64(7),
        CatchUp = r.GetInt64(8) != 0,
        NextRunAt = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9)),
        LastRunAt = r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10)),
        LastStatus = r.GetString(11),
        LastSummary = r.GetString(12),
        LastConversationId = r.IsDBNull(13) ? null : r.GetString(13),
        RunCount = (int)r.GetInt64(14),
        CreatedAt = DateTimeOffset.Parse(r.GetString(15)),
    };

    private static string Trim(string s, int max)
    {
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
