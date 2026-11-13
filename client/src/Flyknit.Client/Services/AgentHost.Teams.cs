using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Teams;

namespace Flyknit.Client.Services;

/// <summary>
/// 团队（docs/team-mode.md）：列表、建、改、解散、复制。
/// 范围、权限、成员校验都在服务端；这里只是转发，并把团队模式关着时的入口收起来。
/// </summary>
public sealed partial class AgentHost
{
    /// <summary>团队模式开关（安全中心 teams，默认关）。关着时侧栏没有「团队」。</summary>
    public bool TeamsOn => Security.On(SecuritySettings.Teams, fallback: false);

    /// <summary>员工能不能自己建团队（安全中心 team_create）。</summary>
    public bool TeamCreateOn => TeamsOn && Security.On(SecuritySettings.TeamCreate, fallback: true);

    /// <summary>一个团队最多几位成员（安全中心 team_max_members，2～8）。</summary>
    public int TeamMaxMembers => Math.Clamp(Security.Number(SecuritySettings.TeamMaxMembers, 8), 2, 8);

    public async Task<List<Team>> TeamsAsync(CancellationToken ct)
    {
        if (!TeamsOn)
        {
            return new();
        }
        var list = await Server.GetTeamsAsync(ct);
        _teamCache = list.ToDictionary(t => t.Id, StringComparer.Ordinal);
        return list;
    }

    /// <summary>最近一次从服务端拉到的团队（开始一轮团队任务时用；拉不到就重新拉一次）。</summary>
    private Dictionary<string, Team> _teamCache = new(StringComparer.Ordinal);

    /// <summary>这台电脑能用的某个团队。不在列表里（解散了、范围收回了、团队模式关了）返回 null。</summary>
    public async Task<Team?> TeamAsync(string id, CancellationToken ct)
    {
        if (!TeamsOn || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        try
        {
            await TeamsAsync(ct);
        }
        catch (Exception ex)
        {
            // 服务端暂时连不上：用上次拉到的（范围、权限下次派遣登记时服务端还会再查一遍）
            Log.Warn($"拉取团队列表失败：{ex.Message}");
        }
        return _teamCache.GetValueOrDefault(id);
    }

    public Task<Team> SaveTeamAsync(string? id, TeamInput input, CancellationToken ct)
    {
        if (!TeamsOn)
        {
            throw new InvalidOperationException("管理员没有开启团队模式");
        }
        // 只收认识的工具类别，去掉空成员；其余校验（人数、范围、重名）由服务端做
        input.DisabledTools = input.DisabledTools.Where(TeamInput.ToolKinds.Contains).Distinct().ToList();
        input.Members = input.Members.Where(m => !string.IsNullOrWhiteSpace(m.ExpertId)).ToList();
        return Server.SaveTeamAsync(id, input, ct);
    }

    public Task DissolveTeamAsync(string id, CancellationToken ct) => Server.DissolveTeamAsync(id, ct);

    // ---------- 团队执行（docs/team-mode.md 第 6 节）----------

    /// <summary>一次团队任务里同时干活的成员数（安全中心 team_parallel，1～4）。</summary>
    public int TeamParallel => Math.Clamp(Security.Number(SecuritySettings.TeamParallel, 3), 1, 4);

    /// <summary>团队对话一轮的 token 上限（安全中心 team_token_budget，单位万，含队长和所有成员）。</summary>
    private long TeamRunTokens => Math.Clamp(Security.Number(SecuritySettings.TeamTokenBudget, 100), 10, 10000) * 10_000L;

    /// <summary>正在进行的团队任务：对话 ID → 会话（面板上「停止这位成员」按它找）。</summary>
    private readonly ConcurrentDictionary<string, TeamSession> _teamSessions = new(StringComparer.Ordinal);

    /// <summary>等员工回答的计划卡：请求 ID → 回答。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TeamPlanAnswer>> _teamPlans = new(StringComparer.Ordinal);

    /// <summary>这一轮的团队会话：建调度器、宿主回调，交给 team_plan / team_assign。</summary>
    private TeamSession StartTeamSession(Conversation conv, Team team, string runId, IHostEvents events, IConfirmationHandler confirm)
    {
        var scheduler = new TeamScheduler(Dispatcher.DispatchDetailedAsync, Experts.Get) { Parallel = TeamParallel };
        var session = new TeamSession(team, runId, scheduler, new TeamUiHost(this, events, confirm), Dispatcher, Experts.Get);
        _teamSessions[conv.Id] = session;
        return session;
    }

    private void EndTeamSession(string conversationId, TeamSession? session)
    {
        if (session is not null)
        {
            _teamSessions.TryRemove(new KeyValuePair<string, TeamSession>(conversationId, session));
        }
    }

    /// <summary>员工在计划卡上点了开始 / 让队长改 / 取消。</summary>
    public bool AnswerTeamPlan(string requestId, TeamPlanAnswer answer) =>
        _teamPlans.TryRemove(requestId, out var pending) && pending.TrySetResult(answer);

    /// <summary>面板上「停止这位成员」。</summary>
    public bool StopTeamMember(string conversationId, string expertId) =>
        _teamSessions.TryGetValue(conversationId, out var session) && session.Scheduler.StopMember(expertId);

    /// <summary>团队任务的界面部分：计划卡、开工授权卡、成员进度和看板推给页面。</summary>
    private sealed class TeamUiHost(AgentHost host, IHostEvents events, IConfirmationHandler confirm) : ITeamHost
    {
        public async Task<TeamPlanAnswer> ReviewPlanAsync(TeamPlanReview review, CancellationToken ct)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var pending = new TaskCompletionSource<TeamPlanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            host._teamPlans[requestId] = pending;
            events.Post(new
            {
                type = "team.plan",
                conversationId = review.ConversationId,
                requestId,
                callId = review.CallId,
                runId = review.RunId,
                round = review.Round,
                goal = review.Plan.Goal,
                items = review.Plan.Items.Select(i => new
                {
                    expertId = i.ExpertId,
                    name = host.Experts.Get(i.ExpertId)?.Name ?? i.ExpertId,
                    avatar = host.Experts.Get(i.ExpertId)?.Avatar ?? "",
                    readOnly = host.Experts.Get(i.ExpertId)?.ReadOnly == true,
                    task = i.Task,
                    done = i.Done,
                    dependsOn = i.DependsOn,
                }).ToList(),
                skipped = review.Skipped.Select(id => host.Experts.Get(id)?.Name ?? id).ToList(),
                estimateTokens = review.EstimateTokens,
                budgetTokens = host.TeamRunTokens,
            });
            await using var registration = ct.Register(() => pending.TrySetResult(new TeamPlanAnswer(TeamPlanAction.Cancel, Array.Empty<string>(), "")));
            try
            {
                return await pending.Task;
            }
            finally
            {
                host._teamPlans.TryRemove(requestId, out _);
                events.Post(new { type = "team.planClosed", conversationId = review.ConversationId, requestId });
            }
        }

        public async Task<bool> GrantAsync(string conversationId, string callId, IReadOnlyList<string> labels, CancellationToken ct)
        {
            var choice = await confirm.ConfirmAsync(new ConfirmRequest
            {
                ConversationId = conversationId,
                Call = new ToolCall(callId, TeamPlanTool.ToolName, "{}"),
                Summary = $"团队这次可能要用：{string.Join("、", labels)}",
                Decision = PolicyDecision.Confirm("这次团队任务内这些操作直接执行，不再逐条问；命令、写文件、删除这类操作到时候照旧单独问")
                    with { Effect = CommandEffect.Write },
                Bundle = labels,
            }, ct);
            return choice != ConfirmChoice.Reject;
        }

        public void PlanStarted(TeamPlanReview review, TeamPlan plan) => events.Post(new
        {
            type = "team.started",
            conversationId = review.ConversationId,
            runId = review.RunId,
            callId = review.CallId,
            round = review.Round,
            items = plan.Items.Select(i => new { expertId = i.ExpertId, task = i.Task, done = i.Done, dependsOn = i.DependsOn }).ToList(),
        });

        public void MemberUpdated(string conversationId, TeamMemberUpdate update) => events.Post(new
        {
            type = "team.member",
            conversationId,
            runId = update.RunId,
            expertId = update.ExpertId,
            status = update.Status.ToString().ToLowerInvariant(),
            note = update.Note,
            subtaskId = update.SubtaskId,
            callId = update.CallId,
        });

        public void BoardAppended(string conversationId, string runId, TeamBoardEntry entry) => events.Post(new
        {
            type = "team.board",
            conversationId,
            runId,
            expertId = entry.ExpertId,
            name = entry.ExpertName,
            kind = entry.Kind.ToString().ToLowerInvariant(),
            text = entry.Text,
            at = entry.At.ToString("O"),
        });

        public void RoundFinished(string conversationId, string runId, IReadOnlyList<TeamMemberResult> results) => events.Post(new
        {
            type = "team.round",
            conversationId,
            runId,
            results = results.Select(r => new { expertId = r.Item.ExpertId, status = r.Status.ToString().ToLowerInvariant(), delivery = r.Delivery, retries = r.Retries }).ToList(),
        });
    }

    public Task<Team> CopyTeamAsync(string id, CancellationToken ct) => Server.CopyTeamAsync(id, ct);
}
