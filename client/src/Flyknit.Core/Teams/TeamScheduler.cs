using System.Collections.Concurrent;
using System.Text;
using Flyknit.Core.Experts;
using Flyknit.Core.Experts.Delegation;
using Flyknit.Core.Gateway;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Teams;

/// <summary>成员在面板上的状态。</summary>
public enum TeamMemberStatus
{
    /// <summary>排队：依赖都好了，等空位。</summary>
    Waiting,

    /// <summary>等上游交卷。</summary>
    Upstream,
    Running,

    /// <summary>临时故障，自动重试中。</summary>
    Retrying,
    Done,
    Failed,
    Stopped,
}

public sealed record TeamMemberUpdate(string RunId, string ExpertId, TeamMemberStatus Status, string Note, string SubtaskId, string CallId);

/// <summary>一位成员这一轮的结果：给队长的整理用。</summary>
public sealed record TeamMemberResult(
    TeamPlanItem Item,
    string ExpertName,
    TeamMemberStatus Status,
    string Delivery,
    int Retries,
    ToolResult? Result,
    SubtaskRecord? Record);

/// <summary>
/// 团队调度（docs/team-mode.md 第 3.1、6 节）：纯程序，不靠模型决定谁先谁后。
///
/// - 按依赖派活：依赖的成员都结束（交卷、失败、停止）了才开工；上游交回的结论从看板带给下游；
/// - 并发：最多 <see cref="Parallel"/> 位同时干；会改东西的成员一次只跑一位（只读的可以同时查），
///   所以几位成员不会同时改员工电脑上的文件；
/// - 重试：只对临时故障（模型服务出错、没能登记、执行崩了）自动重试 1 次，业务性的失败（没权限、缺信息、被拒绝）原样交回；
/// - 每位成员 = 一次专家派遣（<see cref="ExpertDispatcher"/>），专家原来的限制、团队的收紧、确认、审计全部照旧；
/// - 员工可以停掉某一位成员，其余照常。
/// </summary>
public sealed class TeamScheduler
{
    public delegate Task<(ToolResult Result, SubtaskRecord? Record)> Dispatch(DelegationRequest request, ToolContext parent, CancellationToken ct);

    private readonly Dispatch _dispatch;
    private readonly Func<string, Expert?> _experts;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _members = new(StringComparer.Ordinal);

    public TeamScheduler(Dispatch dispatch, Func<string, Expert?> experts)
    {
        _dispatch = dispatch;
        _experts = experts;
    }

    public int Parallel { get; init; } = 3;

    /// <summary>
    /// 沿用之前的结果（程序中途关掉后接着做时，同一位成员、同一项任务已经交过卷的不重做）。
    /// 返回之前那次的子任务记录；null = 照常派遣。
    /// </summary>
    public Func<TeamPlanItem, SubtaskRecord?>? Reuse { get; init; }

    public event Action<TeamMemberUpdate>? Updated;

    /// <summary>员工在面板上停掉某一位成员。没在跑的返回 false。</summary>
    public bool StopMember(string expertId)
    {
        if (_members.TryGetValue(expertId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    /// <param name="runId">这次团队任务的 ID（面板事件用）。</param>
    /// <param name="callId">队长那次 team_plan / team_assign 调用的 ID；每位成员的子任务用「callId#专家」，界面据此把步骤挂到面板上。</param>
    public async Task<IReadOnlyList<TeamMemberResult>> RunAsync(string runId, string callId, TeamPlan plan, Team team, DispatchConstraints dispatch,
        TeamBoard board, ToolContext parent, CancellationToken ct)
    {
        var items = plan.Items.ToList();
        var status = items.ToDictionary(i => i.ExpertId, _ => TeamMemberStatus.Waiting, StringComparer.Ordinal);
        var results = new ConcurrentDictionary<string, TeamMemberResult>(StringComparer.Ordinal);
        var running = new Dictionary<string, Task>(StringComparer.Ordinal);
        var writer = (string?)null; // 正在跑的会改东西的成员
        var gate = new object();

        bool ReadOnlyMember(string id) => team.ReadOnly || _experts(id)?.ReadOnly == true;
        bool Finished(string id) => status[id] is TeamMemberStatus.Done or TeamMemberStatus.Failed or TeamMemberStatus.Stopped;
        void Set(string id, TeamMemberStatus s, string note = "", string subtaskId = "")
        {
            status[id] = s;
            Updated?.Invoke(new TeamMemberUpdate(runId, id, s, note, subtaskId, $"{callId}#{id}"));
        }

        foreach (var item in items)
        {
            Set(item.ExpertId, item.DependsOn.Count > 0 ? TeamMemberStatus.Upstream : TeamMemberStatus.Waiting);
        }

        while (true)
        {
            lock (gate)
            {
                foreach (var item in items)
                {
                    var id = item.ExpertId;
                    if (status[id] is not (TeamMemberStatus.Waiting or TeamMemberStatus.Upstream))
                    {
                        continue;
                    }
                    if (!item.DependsOn.All(Finished))
                    {
                        continue;
                    }
                    if (ct.IsCancellationRequested)
                    {
                        Set(id, TeamMemberStatus.Stopped, "团队任务已停止");
                        results[id] = new TeamMemberResult(item, NameOf(id), TeamMemberStatus.Stopped, "未开始", 0, null, null);
                        continue;
                    }
                    var ro = ReadOnlyMember(id);
                    if (running.Count >= Math.Max(1, Parallel) || (!ro && writer is not null))
                    {
                        if (status[id] == TeamMemberStatus.Upstream)
                        {
                            Set(id, TeamMemberStatus.Waiting, ro ? "等空位" : "等前一位会改东西的成员做完");
                        }
                        continue;
                    }
                    if (!ro)
                    {
                        writer = id;
                    }
                    Set(id, TeamMemberStatus.Running);
                    running[id] = Task.Run(async () =>
                    {
                        var result = await RunMemberAsync(runId, callId, item, team, dispatch, board, parent, ct, s =>
                        {
                            lock (gate)
                            {
                                Set(id, s.Status, s.Note, s.SubtaskId);
                            }
                        });
                        results[id] = result;
                        lock (gate)
                        {
                            running.Remove(id);
                            if (writer == id)
                            {
                                writer = null;
                            }
                            Set(id, result.Status, result.Delivery, result.Record?.Id ?? "");
                        }
                    }, CancellationToken.None);
                }
                if (running.Count == 0 && items.All(i => Finished(i.ExpertId)))
                {
                    break;
                }
            }
            Task[] waiting;
            lock (gate)
            {
                waiting = running.Values.ToArray();
            }
            if (waiting.Length == 0)
            {
                // 剩下的都在等永远不会结束的依赖（不应发生：计划已经校验过不成环）。兜底：标成停止
                lock (gate)
                {
                    foreach (var item in items.Where(i => !Finished(i.ExpertId)))
                    {
                        Set(item.ExpertId, TeamMemberStatus.Stopped, "依赖没法满足");
                        results[item.ExpertId] = new TeamMemberResult(item, NameOf(item.ExpertId), TeamMemberStatus.Stopped, "未开始", 0, null, null);
                    }
                }
                break;
            }
            await Task.WhenAny(waiting);
        }
        return items.Select(i => results[i.ExpertId]).ToList();
    }

    private async Task<TeamMemberResult> RunMemberAsync(string runId, string callId, TeamPlanItem item, Team team, DispatchConstraints dispatch,
        TeamBoard board, ToolContext parent, CancellationToken ct, Action<TeamMemberUpdate> report)
    {
        var id = item.ExpertId;
        var name = NameOf(id);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _members[id] = cts;
        try
        {
            if (item.DependsOn.Count > 0)
            {
                board.Append(new TeamBoardEntry(id, name, TeamBoardKind.Read,
                    $"读取了 {string.Join("、", item.DependsOn.Select(NameOf))} 的结果", DateTimeOffset.Now));
            }
            if (Reuse?.Invoke(item) is { } previous)
            {
                board.Append(new TeamBoardEntry(id, name, TeamBoardKind.Progress, "沿用中断前已交卷的结果，没有重做", DateTimeOffset.Now));
                Post(board, id, name, previous);
                var reused = new ToolResult(true, ReusedText(previous));
                var (reusedStatus, reusedDelivery) = Grade(reused, previous);
                return new TeamMemberResult(item, name, reusedStatus, reusedDelivery + "（沿用）", 0, reused, previous);
            }
            var request = new DelegationRequest(id, TaskText(item, team), board.RenderFor(item.DependsOn), Array.Empty<string>(),
                $"{callId}#{id}", dispatch);
            var retries = 0;
            while (true)
            {
                var (result, record) = await _dispatch(request, parent, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    var stopped = record is not null && record.Status is "completed" or "partial";
                    if (!stopped)
                    {
                        return new TeamMemberResult(item, name, TeamMemberStatus.Stopped, ct.IsCancellationRequested ? "团队任务已停止" : "员工停止了这位成员",
                            retries, result, record);
                    }
                }
                if (retries == 0 && IsTransient(result, record))
                {
                    retries++;
                    board.Append(new TeamBoardEntry(id, name, TeamBoardKind.Progress, $"临时故障（{Why(result, record)}），调度器自动重试 1 次", DateTimeOffset.Now));
                    report(new TeamMemberUpdate(runId, id, TeamMemberStatus.Retrying, "临时故障，自动重试（1/1）", record?.Id ?? "", $"{callId}#{id}"));
                    continue;
                }
                Post(board, id, name, record);
                var (status, delivery) = Grade(result, record);
                return new TeamMemberResult(item, name, status, delivery, retries, result, record);
            }
        }
        finally
        {
            _members.TryRemove(id, out _);
        }
    }

    /// <summary>临时故障：没登记上（网络）、执行崩了、模型服务出错。缺信息、没权限、员工拒绝这些不算。</summary>
    internal static bool IsTransient(ToolResult result, SubtaskRecord? record)
    {
        if (record is null)
        {
            return result.Output.Contains("没能向服务器登记", StringComparison.Ordinal);
        }
        return record.Status == "failed" && record.ErrorCode is "crash" or "no_run" or "no_submission:failed";
    }

    private static string Why(ToolResult result, SubtaskRecord? record) =>
        record is null ? "没能向服务器登记" : record.ErrorCode == "crash" ? "执行出错" : "模型服务出错";

    /// <summary>交付标准的程序检查：完成且交回了结论或报告 = 达标；部分完成 = 部分达标；缺信息 = 未达标；其余 = 未完成。</summary>
    internal static (TeamMemberStatus Status, string Delivery) Grade(ToolResult result, SubtaskRecord? record)
    {
        if (record is null)
        {
            return (TeamMemberStatus.Failed, "未完成：" + Clip(result.Output, 120));
        }
        return record.Status switch
        {
            "completed" when record.Findings.Count > 0 || record.Report.Trim().Length > 0 || record.Summary.Trim().Length > 0
                => (TeamMemberStatus.Done, "达标"),
            "completed" => (TeamMemberStatus.Done, "未达标：交卷里没有结论"),
            "partial" => (TeamMemberStatus.Done, "部分达标"),
            "needs_input" => (TeamMemberStatus.Done, "未达标：缺信息"),
            "cancelled" => (TeamMemberStatus.Stopped, record.CancelReason == "user" ? "员工停止了这位成员" : "已停止"),
            "timed_out" => (TeamMemberStatus.Failed, "未完成：超时"),
            _ => (TeamMemberStatus.Failed, "未完成：" + (record.Error.Length > 0 ? record.Error : record.ErrorCode)),
        };
    }

    /// <summary>成员交卷后由调度器代写到看板：结论（带核验标记）、文件、没解决的问题。成员自己没有写看板的工具。</summary>
    private static void Post(TeamBoard board, string id, string name, SubtaskRecord? record)
    {
        if (record is null)
        {
            return;
        }
        var now = DateTimeOffset.Now;
        foreach (var f in record.Findings.Where(f => f.Statement.Trim().Length > 0))
        {
            board.Append(new TeamBoardEntry(id, name, TeamBoardKind.Finding, $"{f.Statement.Trim()}（{VerificationText(f.Verification)}）", now));
        }
        if (record.Findings.Count == 0 && record.Summary.Trim().Length > 0)
        {
            board.Append(new TeamBoardEntry(id, name, TeamBoardKind.Finding, record.Summary.Trim(), now));
        }
        foreach (var file in record.Artifacts)
        {
            board.Append(new TeamBoardEntry(id, name, TeamBoardKind.File, file, now));
        }
        foreach (var q in record.OpenQuestions.Where(q => q.Trim().Length > 0))
        {
            board.Append(new TeamBoardEntry(id, name, TeamBoardKind.Question, q.Trim(), now));
        }
    }

    internal static string VerificationText(string v) => v switch
    {
        "supported" => "已核验",
        "source_verified" => "来源已核实",
        "verification_failed" => "核验失败",
        _ => "未核实",
    };

    /// <summary>交给成员的任务书：队长给的任务 + 交付标准 + 团队里的职责补充。</summary>
    internal static string TaskText(TeamPlanItem item, Team team)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"你是「{team.Name}」团队的成员。队长（主助手）分给你的任务：");
        sb.AppendLine(item.Task.Trim());
        sb.AppendLine();
        sb.AppendLine($"交付标准：{item.Done.Trim()}");
        var note = team.Members.FirstOrDefault(m => m.ExpertId == item.ExpertId)?.Note.Trim() ?? "";
        if (note.Length > 0)
        {
            sb.AppendLine($"你在这个团队里的职责补充：{note}");
        }
        sb.AppendLine("只做分给你的这一项；别的成员负责的部分不要做。做完按交付标准交卷，缺的信息写进 open_questions。");
        return sb.ToString();
    }

    /// <summary>沿用的结果交回队长时的文字：结论（带核验标记）、报告、产出、没解决的问题。</summary>
    internal static string ReusedText(SubtaskRecord record)
    {
        var sb = new StringBuilder();
        sb.AppendLine("（这是程序中断前这位成员已经交回的结果，没有重做）");
        if (record.Summary.Trim().Length > 0)
        {
            sb.AppendLine("摘要：" + record.Summary.Trim());
        }
        foreach (var f in record.Findings.Where(f => f.Statement.Trim().Length > 0))
        {
            sb.AppendLine($"- {f.Statement.Trim()}（{VerificationText(f.Verification)}）");
        }
        if (record.Report.Trim().Length > 0)
        {
            sb.AppendLine(Clip(record.Report.Trim(), 4000));
        }
        foreach (var file in record.Artifacts)
        {
            sb.AppendLine("产出：" + file);
        }
        foreach (var q in record.OpenQuestions.Where(q => q.Trim().Length > 0))
        {
            sb.AppendLine("没解决：" + q.Trim());
        }
        return sb.ToString();
    }

    private string NameOf(string id) => _experts(id)?.Name ?? id;

    private static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
