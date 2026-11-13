using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Tasks;

// VNext 第 1 期：统一任务模型（docs/c1-unified-tasks.md）。
//
// Task = 一件要办成的事：外部来的（手机 / 钉钉 / 企业微信 / 指令 / 定时 / 文件夹 / 事件 / PR 自动修复）一次触发一个；
// 员工自己在窗口里说话，从开始跑到停下来的一串一个（排队的话、插话、长任务自动续跑、中断后续跑都在里面）。
// Run = Task 里的一次 AgentLoop 运行。
//
// 这是一本账：挂在现有的「开始跑 / 停下」节点上记录，不参与执行，也不放宽任何权限。记账失败不能挡住任务。

public enum TaskState
{
    Pending,
    Running,
    /// <summary>在等员工确认某个操作。</summary>
    WaitingConfirm,
    /// <summary>程序被关时还在跑：下次打开可以续跑或放弃。</summary>
    Interrupted,
    Done,
    Failed,
    Stopped,
}

public enum TaskEvent
{
    Start,
    WaitConfirm,
    Confirmed,
    Interrupt,
    Resume,
    Complete,
    Fail,
    Stop,
}

/// <summary>任务状态怎么变。不允许的变化返回 null（调用方只记日志，不抛异常）。</summary>
public static class TaskStateMachine
{
    public static bool IsFinal(TaskState s) => s is TaskState.Done or TaskState.Failed or TaskState.Stopped;

    public static TaskState? Next(TaskState state, TaskEvent e) => (state, e) switch
    {
        (TaskState.Pending, TaskEvent.Start) => TaskState.Running,
        (TaskState.Pending, TaskEvent.Stop) => TaskState.Stopped,
        (TaskState.Running, TaskEvent.WaitConfirm) => TaskState.WaitingConfirm,
        (TaskState.WaitingConfirm, TaskEvent.Confirmed) => TaskState.Running,
        // 同一个任务里接着开下一个 Run（排队的话、自动续跑）：保持进行中
        (TaskState.Running, TaskEvent.Start) => TaskState.Running,
        (TaskState.Running or TaskState.WaitingConfirm, TaskEvent.Interrupt) => TaskState.Interrupted,
        (TaskState.Interrupted, TaskEvent.Resume) => TaskState.Running,
        (TaskState.Running or TaskState.WaitingConfirm, TaskEvent.Complete) => TaskState.Done,
        (TaskState.Running or TaskState.WaitingConfirm or TaskState.Pending, TaskEvent.Fail) => TaskState.Failed,
        (TaskState.Running or TaskState.WaitingConfirm or TaskState.Interrupted or TaskState.Pending, TaskEvent.Stop) => TaskState.Stopped,
        // 中断的任务被放弃 / 没续跑就结束
        (TaskState.Interrupted, TaskEvent.Fail) => TaskState.Failed,
        _ => null,
    };

    public static string ToText(TaskState s) => s switch
    {
        TaskState.Pending => "pending",
        TaskState.Running => "running",
        TaskState.WaitingConfirm => "waiting_confirm",
        TaskState.Interrupted => "interrupted",
        TaskState.Done => "done",
        TaskState.Failed => "failed",
        _ => "stopped",
    };

    public static TaskState Parse(string text) => text switch
    {
        "pending" => TaskState.Pending,
        "running" => TaskState.Running,
        "waiting_confirm" => TaskState.WaitingConfirm,
        "interrupted" => TaskState.Interrupted,
        "done" => TaskState.Done,
        "failed" => TaskState.Failed,
        _ => TaskState.Stopped,
    };
}

/// <summary>任务从哪来。</summary>
public static class TaskOrigins
{
    public const string Ui = "ui";
    public const string Mobile = "mobile";
    public const string DingTalk = "dingtalk";
    public const string WeCom = "wecom";
    public const string Instruction = "instruction";
    public const string Schedule = "schedule";
    public const string Folder = "folder";
    public const string Event = "event";
    public const string PullRequest = "pr";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Ui, Mobile, DingTalk, WeCom, Instruction, Schedule, Folder, Event, PullRequest,
    };
}

/// <summary>一次触发：各入口交给任务账本的信息。</summary>
public sealed record TriggerEvent(
    string Origin,
    string ConversationId,
    string Title,
    string Reference = "",
    string RequestedBy = "",
    string? GrantId = null,
    long TokenBudget = 0,
    string Sender = "",
    string GroupId = "");

public sealed record TaskRecord
{
    public string Id { get; init; } = "";
    public string ConversationId { get; init; } = "";
    public string Origin { get; init; } = TaskOrigins.Ui;
    public string Reference { get; init; } = "";
    public string Title { get; init; } = "";
    public string RequestedBy { get; init; } = "";
    public string? GrantId { get; init; }
    public TaskState State { get; init; } = TaskState.Pending;
    /// <summary>这个任务所有 Run 合计的 token 上限，0 = 不限。</summary>
    public long TokenBudget { get; init; }
    public long TokensUsed { get; init; }
    public int Runs { get; init; }
    public string StopReason { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>渠道发送人（钉钉 / 企业微信的账号）：Router 只在同一个人的任务里找追问归属（docs/c2-router.md）。</summary>
    public string Sender { get; init; } = "";
    /// <summary>一句话拆开的几件事共用一个组编号；空 = 不属于任何组。</summary>
    public string GroupId { get; init; } = "";

    /// <summary>预算用完了：不该再开新的 Run。</summary>
    public bool BudgetExhausted => TokenBudget > 0 && TokensUsed >= TokenBudget;
}

public sealed record RunRecord(
    string Id,
    string TaskId,
    string Kind,
    string Status,
    string StopReason,
    long PromptTokens,
    long CompletionTokens,
    string? AnswerId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Run 的种类。</summary>
public static class RunKinds
{
    public const string First = "first";
    /// <summary>员工排队的话、插话接着跑。</summary>
    public const string Queued = "queued";
    /// <summary>长任务模式走到上限后自动接着做。</summary>
    public const string AutoContinue = "auto_continue";
    /// <summary>程序被关后续跑。</summary>
    public const string Resume = "resume";
}

/// <summary>任务和运行记录，存在对话同一个库里（对话彻底删除时跟着删）。</summary>
public sealed class TaskStore
{
    private readonly Func<SqliteConnection> _open;
    private readonly Func<DateTimeOffset> _now;

    public TaskStore(Func<SqliteConnection> open, Func<DateTimeOffset>? now = null)
    {
        _open = open;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static void Migrate(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                origin TEXT NOT NULL,
                reference TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                requested_by TEXT NOT NULL DEFAULT '',
                grant_id TEXT NULL,
                status TEXT NOT NULL,
                token_budget INTEGER NOT NULL DEFAULT 0,
                tokens_used INTEGER NOT NULL DEFAULT 0,
                runs INTEGER NOT NULL DEFAULT 0,
                stop_reason TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                started_at TEXT NULL,
                finished_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_conversation ON tasks (conversation_id, created_at);
            CREATE INDEX IF NOT EXISTS ix_tasks_status ON tasks (status, created_at);
            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                status TEXT NOT NULL,
                stop_reason TEXT NOT NULL DEFAULT '',
                prompt_tokens INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                answer_id TEXT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_runs_task ON runs (task_id, started_at);
            """;
        cmd.ExecuteNonQuery();
        // 第 2 期（Router）加的列：只加不删
        AddColumn(c, "sender", "TEXT NOT NULL DEFAULT ''");
        AddColumn(c, "group_id", "TEXT NOT NULL DEFAULT ''");
        using var ix = c.CreateCommand();
        ix.CommandText = "CREATE INDEX IF NOT EXISTS ix_tasks_sender ON tasks (origin, sender, created_at); CREATE INDEX IF NOT EXISTS ix_tasks_group ON tasks (group_id)";
        ix.ExecuteNonQuery();
    }

    private static void AddColumn(SqliteConnection c, string column, string definition)
    {
        using (var check = c.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name = $n";
            check.Parameters.AddWithValue("$n", column);
            if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }
        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE tasks ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private const string Columns =
        "id, conversation_id, origin, reference, title, requested_by, grant_id, status, token_budget, tokens_used, runs, stop_reason, created_at, started_at, finished_at, sender, group_id";

    public TaskRecord Create(TriggerEvent trigger)
    {
        var task = new TaskRecord
        {
            Id = "t" + Guid.NewGuid().ToString("N"),
            ConversationId = trigger.ConversationId,
            Origin = TaskOrigins.All.Contains(trigger.Origin) ? trigger.Origin : TaskOrigins.Ui,
            Reference = Clip(trigger.Reference, 200),
            Title = Clip(trigger.Title, 200),
            RequestedBy = Clip(trigger.RequestedBy, 200),
            GrantId = trigger.GrantId,
            State = TaskState.Pending,
            TokenBudget = Math.Max(0, trigger.TokenBudget),
            CreatedAt = _now(),
            Sender = Clip(trigger.Sender, 200),
            GroupId = Clip(trigger.GroupId, 40),
        };
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO tasks ({Columns})
            VALUES ($id, $conv, $origin, $ref, $title, $by, $grant, $status, $budget, 0, 0, '', $at, NULL, NULL, $sender, $group)
            """;
        cmd.Parameters.AddWithValue("$id", task.Id);
        cmd.Parameters.AddWithValue("$conv", task.ConversationId);
        cmd.Parameters.AddWithValue("$origin", task.Origin);
        cmd.Parameters.AddWithValue("$ref", task.Reference);
        cmd.Parameters.AddWithValue("$title", task.Title);
        cmd.Parameters.AddWithValue("$by", task.RequestedBy);
        cmd.Parameters.AddWithValue("$grant", (object?)task.GrantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", TaskStateMachine.ToText(task.State));
        cmd.Parameters.AddWithValue("$budget", task.TokenBudget);
        cmd.Parameters.AddWithValue("$at", task.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$sender", task.Sender);
        cmd.Parameters.AddWithValue("$group", task.GroupId);
        cmd.ExecuteNonQuery();
        return task;
    }

    /// <summary>
    /// 同一个来源、同一个发送人的任务：没结束的，加上 since 之后结束的，新的在前，最多 limit 个（Router 找追问归属的候选）。
    /// 发送人为空时不返回任何任务——不知道是谁发的，就不往别人的任务里归。
    /// </summary>
    public IReadOnlyList<TaskRecord> ForSender(string origin, string sender, DateTimeOffset since, int limit = 8)
    {
        if (sender.Length == 0)
        {
            return Array.Empty<TaskRecord>();
        }
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM tasks
            WHERE origin = $origin AND sender = $sender
              AND (status IN ('pending', 'running', 'waiting_confirm', 'interrupted') OR COALESCE(finished_at, created_at) >= $since)
            ORDER BY created_at DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$origin", origin);
        cmd.Parameters.AddWithValue("$sender", sender);
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        cmd.Parameters.AddWithValue("$n", limit);
        return ReadAll(cmd);
    }

    /// <summary>一组（一句话拆开的几件事），按创建顺序。</summary>
    public IReadOnlyList<TaskRecord> OfGroup(string groupId)
    {
        if (groupId.Length == 0)
        {
            return Array.Empty<TaskRecord>();
        }
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tasks WHERE group_id = $g ORDER BY created_at, rowid";
        cmd.Parameters.AddWithValue("$g", groupId);
        return ReadAll(cmd);
    }

    /// <summary>引用完全相同的任务，按创建顺序（一句话拆开的几件事：引用是 split:原对话）。</summary>
    public IReadOnlyList<TaskRecord> WithReference(string reference)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tasks WHERE reference = $r ORDER BY created_at, rowid";
        cmd.Parameters.AddWithValue("$r", reference);
        return ReadAll(cmd);
    }

    private static List<TaskRecord> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<TaskRecord>();
        while (r.Read())
        {
            list.Add(Read(r));
        }
        return list;
    }

    public TaskRecord? Get(string id)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tasks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>这个对话里还没结束的任务（进行中、等确认、中断、待办），最近的那个。</summary>
    public TaskRecord? OpenFor(string conversationId)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM tasks
            WHERE conversation_id = $conv AND status IN ('pending', 'running', 'waiting_confirm', 'interrupted')
            ORDER BY created_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>任务中心：没结束的全部，加上最近结束的若干条。</summary>
    public IReadOnlyList<TaskRecord> List(int recentFinished = 50)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM tasks t
            WHERE status IN ('pending', 'running', 'waiting_confirm', 'interrupted')
               OR id IN (SELECT id FROM tasks WHERE status IN ('done', 'failed', 'stopped') ORDER BY COALESCE(finished_at, created_at) DESC LIMIT $n)
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("$n", recentFinished);
        using var r = cmd.ExecuteReader();
        var list = new List<TaskRecord>();
        while (r.Read())
        {
            list.Add(Read(r));
        }
        return list;
    }

    /// <summary>
    /// 状态变化。不允许的变化不改库，返回 null（调用方记日志即可）。
    /// 结束时写 finished_at 和原因；第一次开始时写 started_at。
    /// </summary>
    public TaskRecord? Transition(string id, TaskEvent e, string stopReason = "")
    {
        var task = Get(id);
        if (task is null || TaskStateMachine.Next(task.State, e) is not { } next)
        {
            return null;
        }
        var now = _now();
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE tasks SET status = $status,
                started_at = COALESCE(started_at, $started),
                finished_at = $finished,
                stop_reason = CASE WHEN $reason = '' THEN stop_reason ELSE $reason END
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", TaskStateMachine.ToText(next));
        cmd.Parameters.AddWithValue("$started", next == TaskState.Running ? now.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$finished", TaskStateMachine.IsFinal(next) ? now.ToString("O") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", Clip(stopReason, 200));
        cmd.ExecuteNonQuery();
        return Get(id);
    }

    /// <summary>开一个 Run（任务同时进入进行中）。runId 用 AgentLoop 这一轮的 RunId。</summary>
    public RunRecord StartRun(string taskId, string runId, string kind)
    {
        var task = Get(taskId);
        if (task is not null)
        {
            Transition(taskId, task.State == TaskState.Interrupted ? TaskEvent.Resume : TaskEvent.Start);
        }
        var run = new RunRecord(runId, taskId, kind, "running", "", 0, 0, null, _now(), null);
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO runs (id, task_id, kind, status, started_at) VALUES ($id, $task, $kind, 'running', $at);
            UPDATE tasks SET runs = runs + 1 WHERE id = $task;
            """;
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$at", run.StartedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return run;
    }

    /// <summary>Run 结束：记下用了多少 token、为什么停，累计到任务上。不改任务状态（任务是否结束由调用方决定）。</summary>
    public void EndRun(string runId, string stopReason, long promptTokens, long completionTokens, string? answerId)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE tasks SET tokens_used = tokens_used + $total
            WHERE id = (SELECT task_id FROM runs WHERE id = $id AND finished_at IS NULL);
            UPDATE runs SET status = 'finished', stop_reason = $reason, prompt_tokens = $p, completion_tokens = $c,
                answer_id = $answer, finished_at = $at
            WHERE id = $id AND finished_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.Parameters.AddWithValue("$reason", Clip(stopReason, 100));
        cmd.Parameters.AddWithValue("$p", Math.Max(0, promptTokens));
        cmd.Parameters.AddWithValue("$c", Math.Max(0, completionTokens));
        cmd.Parameters.AddWithValue("$total", Math.Max(0, promptTokens) + Math.Max(0, completionTokens));
        cmd.Parameters.AddWithValue("$answer", (object?)answerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", _now().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<RunRecord> RunsOf(string taskId)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_id, kind, status, stop_reason, prompt_tokens, completion_tokens, answer_id, started_at, finished_at
            FROM runs WHERE task_id = $task ORDER BY started_at
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        using var r = cmd.ExecuteReader();
        var list = new List<RunRecord>();
        while (r.Read())
        {
            list.Add(new RunRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetInt64(5), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7),
                Time(r.GetString(8)), r.IsDBNull(9) ? null : Time(r.GetString(9))));
        }
        return list;
    }

    /// <summary>
    /// 程序启动时：还标着进行中 / 等确认的任务，说明上次程序被关了，改成中断。
    /// 再把 conversations 里标着没跑完、却没有对应任务的（升级前的老数据）补成中断的任务。只做该做的，重复调用不重复补。
    /// </summary>
    public int RecoverAfterRestart()
    {
        using var c = _open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE tasks SET status = 'interrupted' WHERE status IN ('running', 'waiting_confirm');
            UPDATE runs SET status = 'interrupted', finished_at = $at WHERE finished_at IS NULL;
            INSERT INTO tasks (id, conversation_id, origin, reference, title, requested_by, grant_id, status,
                               token_budget, tokens_used, runs, stop_reason, created_at, started_at, finished_at)
            SELECT 't' || lower(hex(randomblob(16))), c.id,
                   CASE COALESCE(c.run_origin, 'ui') WHEN 'remote' THEN 'mobile' WHEN 'headless' THEN 'instruction' ELSE 'ui' END,
                   '', c.title, '', NULL, 'interrupted', 0, 0, 0, '', c.run_started_at, c.run_started_at, NULL
            FROM conversations c
            WHERE c.run_started_at IS NOT NULL AND c.deleted_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM tasks t WHERE t.conversation_id = c.id
                              AND t.status IN ('pending', 'running', 'waiting_confirm', 'interrupted'));
            """;
        cmd.Parameters.AddWithValue("$at", _now().ToString("O"));
        var n = cmd.ExecuteNonQuery();
        tx.Commit();
        return n;
    }

    private static TaskRecord Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        ConversationId = r.GetString(1),
        Origin = r.GetString(2),
        Reference = r.GetString(3),
        Title = r.GetString(4),
        RequestedBy = r.GetString(5),
        GrantId = r.IsDBNull(6) ? null : r.GetString(6),
        State = TaskStateMachine.Parse(r.GetString(7)),
        TokenBudget = r.GetInt64(8),
        TokensUsed = r.GetInt64(9),
        Runs = r.GetInt32(10),
        StopReason = r.GetString(11),
        CreatedAt = Time(r.GetString(12)),
        StartedAt = r.IsDBNull(13) ? null : Time(r.GetString(13)),
        FinishedAt = r.IsDBNull(14) ? null : Time(r.GetString(14)),
        Sender = r.GetString(15),
        GroupId = r.GetString(16),
    };

    private static DateTimeOffset Time(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
