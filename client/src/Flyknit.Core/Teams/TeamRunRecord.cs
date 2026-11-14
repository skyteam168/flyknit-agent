using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Teams;

/// <summary>
/// 一次团队任务的面板数据（计划、成员状态、看板、结果、评价），存在员工电脑本地（docs/team-mode.md 第 15 节）：
/// 关掉对话再打开、程序重启后面板照样能看；程序中途关掉的标成「中断」，下次接着做时已交卷的成员可以沿用结果。
/// 这里有成员交卷的结论（业务数据），所以只存本地，服务端只拿数字（<see cref="TeamRunReport"/>）。
/// 字段名和界面的 TeamRunView 一致（小驼峰）。
/// </summary>
public sealed class TeamRunRecord
{
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("conversationId")] public string ConversationId { get; set; } = "";
    [JsonPropertyName("teamId")] public string TeamId { get; set; } = "";
    [JsonPropertyName("teamName")] public string TeamName { get; set; } = "";
    [JsonPropertyName("goal")] public string Goal { get; set; } = "";
    [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("finishedAt")] public DateTimeOffset? FinishedAt { get; set; }
    [JsonPropertyName("finished")] public bool Finished { get; set; }

    /// <summary>程序在团队任务进行中被关掉了（没有正常收尾）。</summary>
    [JsonPropertyName("interrupted")] public bool Interrupted { get; set; }

    /// <summary>到点自动开始的（定时任务等，没人在电脑前确认计划）。</summary>
    [JsonPropertyName("autoStarted")] public bool AutoStarted { get; set; }
    [JsonPropertyName("rounds")] public int Rounds { get; set; }
    [JsonPropertyName("reassigned")] public bool Reassigned { get; set; }

    /// <summary>成员用掉的 token（不含队长）。</summary>
    [JsonPropertyName("tokens")] public long Tokens { get; set; }

    /// <summary>员工评价：1 = 👍，-1 = 👎，0 = 没评。</summary>
    [JsonPropertyName("rating")] public int Rating { get; set; }
    [JsonPropertyName("feedback")] public string Feedback { get; set; } = "";

    /// <summary>结果数字已经报给服务端了（评价前要先有这条）。</summary>
    [JsonPropertyName("reported")] public bool Reported { get; set; }

    /// <summary>这次队长记的团队经验条数。</summary>
    [JsonPropertyName("notes")] public int Notes { get; set; }

    /// <summary>这次团队任务的 token 上限（面板上的预算条）。</summary>
    [JsonPropertyName("budgetTokens")] public long BudgetTokens { get; set; }

    /// <summary>团队开工授权：卡上列出的操作（没出卡时为空）和员工的回答（null = 没问）。</summary>
    [JsonPropertyName("grantLabels")] public List<string> GrantLabels { get; set; } = new();
    [JsonPropertyName("granted")] public bool? Granted { get; set; }
    [JsonPropertyName("members")] public List<TeamRunMember> Members { get; set; } = new();
    [JsonPropertyName("board")] public List<TeamRunBoardItem> Board { get; set; } = new();

    public static readonly string[] Final = { "done", "failed", "stopped" };

    public TeamRunMember? Member(string expertId) => Members.FirstOrDefault(m => m.ExpertId == expertId);

    /// <summary>上报给服务端的数字。</summary>
    public TeamRunReport ToReport(string status)
    {
        var duration = (int)Math.Clamp(((FinishedAt ?? DateTimeOffset.Now) - StartedAt).TotalSeconds, 0, 7 * 86400);
        return new TeamRunReport(RunId, Members.Count,
            Members.Count(m => m.Status == "done"),
            Members.Count(m => m.Status == "failed"),
            Members.Count(m => m.Status == "stopped"),
            Reassigned, Tokens, duration, status);
    }

    public TeamRunRecord Clone() => JsonSerializer.Deserialize<TeamRunRecord>(JsonSerializer.Serialize(this))!;
}

public sealed class TeamRunMember
{
    [JsonPropertyName("expertId")] public string ExpertId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>waiting / upstream / running / retrying / done / failed / stopped（和界面一致）。</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "waiting";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("callId")] public string CallId { get; set; } = "";
    [JsonPropertyName("subtaskId")] public string SubtaskId { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("done")] public string Done { get; set; } = "";
    [JsonPropertyName("dependsOn")] public List<string> DependsOn { get; set; } = new();
    [JsonPropertyName("delivery")] public string Delivery { get; set; } = "";
    [JsonPropertyName("retries")] public int Retries { get; set; }

    /// <summary>交卷时离开工过了多少秒（面板上「已交卷 0:08」）。</summary>
    [JsonPropertyName("doneSeconds")] public int? DoneSeconds { get; set; }

    /// <summary>沿用了中断前已交卷的结果（没有重做）。</summary>
    [JsonPropertyName("reused")] public bool Reused { get; set; }
}

public sealed class TeamRunBoardItem
{
    [JsonPropertyName("expertId")] public string ExpertId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("at")] public string At { get; set; } = "";
}

/// <summary>团队任务面板数据的本地存储。</summary>
public interface ITeamRunStore
{
    void Save(TeamRunRecord record);

    /// <summary>这个对话最近一次团队任务。</summary>
    TeamRunRecord? Latest(string conversationId);
}

/// <summary>存在内存里（测试、没有本地库时用）。</summary>
public sealed class MemoryTeamRunStore : ITeamRunStore
{
    private readonly ConcurrentDictionary<string, TeamRunRecord> _byConversation = new(StringComparer.Ordinal);

    public void Save(TeamRunRecord record) => _byConversation[record.ConversationId] = record.Clone();

    public TeamRunRecord? Latest(string conversationId) => _byConversation.TryGetValue(conversationId, out var r) ? r.Clone() : null;
}

/// <summary>
/// 存在本机的会话库里（和会话、定时任务同一个 SQLite 文件），表 team_runs 归团队模块所有，迁移只加不删。
/// 每个对话只留最近 20 次团队任务。
/// </summary>
public sealed class SqliteTeamRunStore : ITeamRunStore
{
    private const int KeepPerConversation = 20;
    private readonly Func<SqliteConnection> _open;
    private readonly object _gate = new();
    private bool _migrated;

    public SqliteTeamRunStore(Func<SqliteConnection> open) => _open = open;

    private SqliteConnection Open()
    {
        var c = _open();
        if (!_migrated)
        {
            lock (_gate)
            {
                if (!_migrated)
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS team_runs (
                            run_id TEXT PRIMARY KEY,
                            conversation_id TEXT NOT NULL,
                            team_id TEXT NOT NULL DEFAULT '',
                            data TEXT NOT NULL DEFAULT '{}',
                            started_at TEXT NOT NULL,
                            updated_at TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS ix_team_runs_conversation ON team_runs(conversation_id, started_at);
                        """;
                    cmd.ExecuteNonQuery();
                    _migrated = true;
                }
            }
        }
        return c;
    }

    public void Save(TeamRunRecord record)
    {
        var json = JsonSerializer.Serialize(record);
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO team_runs (run_id, conversation_id, team_id, data, started_at, updated_at)
                VALUES ($run, $conv, $team, $data, $started, $updated)
                ON CONFLICT(run_id) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at;
                DELETE FROM team_runs WHERE conversation_id = $conv AND run_id NOT IN (
                    SELECT run_id FROM team_runs WHERE conversation_id = $conv ORDER BY started_at DESC LIMIT $keep);
                """;
            cmd.Parameters.AddWithValue("$run", record.RunId);
            cmd.Parameters.AddWithValue("$conv", record.ConversationId);
            cmd.Parameters.AddWithValue("$team", record.TeamId);
            cmd.Parameters.AddWithValue("$data", json);
            cmd.Parameters.AddWithValue("$started", record.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$keep", KeepPerConversation);
            cmd.ExecuteNonQuery();
        }
    }

    public TeamRunRecord? Latest(string conversationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM team_runs WHERE conversation_id = $conv ORDER BY started_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$conv", conversationId);
        var data = cmd.ExecuteScalar() as string;
        if (data is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<TeamRunRecord>(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
