using System.Collections.Concurrent;
using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Chat;
using Flyknit.Core.Experts;
using Flyknit.Core.Experts.Delegation;
using Flyknit.Core.Hosting;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Teams;

/// <summary>团队的服务端接口（由 <see cref="Gateway.FlyknitServerClient"/> 实现；范围、权限、成员校验都在服务端）。</summary>
public interface ITeamService
{
    Task<List<Team>> GetTeamsAsync(CancellationToken ct);
    Task<Team> SaveTeamAsync(string? id, TeamInput input, CancellationToken ct);
    Task DissolveTeamAsync(string id, CancellationToken ct);
    Task<Team> CopyTeamAsync(string id, CancellationToken ct);
    Task ReportTeamStartAsync(string id, CancellationToken ct);
}

/// <summary>
/// 团队模式（docs/team-mode.md）作为一个功能模块：底座不认识团队，团队通过两个扩展点接进来——
/// <list type="bullet">
/// <item>运行扩展（<see cref="IRunExtension{TScope}"/>）：团队对话里给主助手加队长的工作方式、team_plan / team_assign，
///   换上团队的模型和用量上限，并让「派遣专家」「子代理」这一轮不出场；</item>
/// <item>界面桥模块（<see cref="IBridgeModule"/>）：团队列表、建、改、解散、复制，计划卡回答、停止成员。</item>
/// </list>
/// 安全中心「团队模式」关着时两头都不出场；范围、成员资格、次数上限服务端另有一层校验（不靠这里）。
/// </summary>
public sealed class TeamFeature : IRunExtension<RunScope>, IBridgeModule
{
    private readonly SecuritySettings _security;
    private readonly ITeamService _server;
    private readonly Func<string, Expert?> _experts;
    private readonly Func<ExpertDispatcher> _dispatcher;
    private readonly Action<string> _log;

    /// <summary>最近一次从服务端拉到的团队（开始一轮团队任务时用；拉不到就用上次的）。</summary>
    private Dictionary<string, Team> _cache = new(StringComparer.Ordinal);

    /// <summary>正在进行的团队任务：对话 ID → 会话（面板上「停止这位成员」按它找）。</summary>
    private readonly ConcurrentDictionary<string, TeamSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>等员工回答的计划卡：请求 ID → 回答。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TeamPlanAnswer>> _plans = new(StringComparer.Ordinal);

    public TeamFeature(SecuritySettings security, ITeamService server, Func<string, Expert?> experts, Func<ExpertDispatcher> dispatcher,
        Action<string>? log = null)
    {
        _security = security;
        _server = server;
        _experts = experts;
        _dispatcher = dispatcher;
        _log = log ?? (_ => { });
    }

    // ---------- 安全中心的设置 ----------

    /// <summary>团队模式开关（安全中心 teams，默认关）。关着时侧栏没有「团队」，团队对话按普通助手继续。</summary>
    public bool On => _security.On(SecuritySettings.Teams, fallback: false);

    /// <summary>员工能不能自己建团队（安全中心 team_create）。</summary>
    public bool CreateOn => On && _security.On(SecuritySettings.TeamCreate, fallback: true);

    /// <summary>一个团队最多几位成员（安全中心 team_max_members，2～8）。</summary>
    public int MaxMembers => Math.Clamp(_security.Number(SecuritySettings.TeamMaxMembers, 8), 2, 8);

    /// <summary>一次团队任务里同时干活的成员数（安全中心 team_parallel，1～4）。</summary>
    public int Parallel => Math.Clamp(_security.Number(SecuritySettings.TeamParallel, 3), 1, 4);

    /// <summary>团队对话一轮的 token 上限（安全中心 team_token_budget，单位万，含队长和所有成员）。</summary>
    public long RunTokens => Math.Clamp(_security.Number(SecuritySettings.TeamTokenBudget, 100), 10, 10000) * 10_000L;

    // ---------- 团队管理 ----------

    public async Task<List<Team>> ListAsync(CancellationToken ct)
    {
        if (!On)
        {
            return new();
        }
        var list = await _server.GetTeamsAsync(ct);
        _cache = list.ToDictionary(t => t.Id, StringComparer.Ordinal);
        return list;
    }

    /// <summary>这台电脑能用的某个团队。不在列表里（解散了、范围收回了、团队模式关了）返回 null。</summary>
    public async Task<Team?> FindAsync(string? id, CancellationToken ct)
    {
        if (!On || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        try
        {
            await ListAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 服务端暂时连不上：用上次拉到的（范围、权限下次派遣登记时服务端还会再查一遍）
            _log($"拉取团队列表失败：{ex.Message}");
        }
        return _cache.GetValueOrDefault(id);
    }

    public Task<Team> SaveAsync(string? id, TeamInput input, CancellationToken ct)
    {
        if (!On)
        {
            throw new InvalidOperationException("管理员没有开启团队模式");
        }
        // 只收认识的工具类别，去掉空成员；其余校验（人数、范围、重名）由服务端做
        input.DisabledTools = input.DisabledTools.Where(ToolKinds.Known.Contains).Distinct().ToList();
        input.Members = input.Members.Where(m => !string.IsNullOrWhiteSpace(m.ExpertId)).ToList();
        return _server.SaveTeamAsync(id, input, ct);
    }

    public Task DissolveAsync(string id, CancellationToken ct) => _server.DissolveTeamAsync(id, ct);

    public Task<Team> CopyAsync(string id, CancellationToken ct) => _server.CopyTeamAsync(id, ct);

    /// <summary>员工在计划卡上点了开始 / 让队长改 / 取消。</summary>
    public bool AnswerPlan(string requestId, TeamPlanAnswer answer) =>
        _plans.TryRemove(requestId, out var pending) && pending.TrySetResult(answer);

    /// <summary>面板上「停止这位成员」。</summary>
    public bool StopMember(string conversationId, string expertId) =>
        _sessions.TryGetValue(conversationId, out var session) && session.Scheduler.StopMember(expertId);

    // ---------- 运行扩展 ----------

    public string Id => RunExtensionIds.Team;

    /// <summary>团队任务里成员由队长按计划派：不再给主助手 ask_expert，也不开子代理。</summary>
    public IReadOnlyCollection<string> Excludes { get; } = new[] { RunExtensionIds.Delegation, RunExtensionIds.SubAgents };

    public async ValueTask<RunContribution?> PrepareAsync(RunScope scope, CancellationToken ct)
    {
        var conv = scope.Conversation;
        if (conv.Mode != ConversationMode.Agent || scope.Expert is not null || string.IsNullOrEmpty(conv.TeamId))
        {
            return null;
        }
        // 团队解散、范围收回或团队模式关了：不出场，这个对话按普通助手继续
        var team = await FindAsync(conv.TeamId, ct);
        if (team is null)
        {
            return null;
        }
        if (scope.NewUserMessage && scope.History.Count(m => m.Role == ChatRole.User) == 1)
        {
            ReportStart(team.Id);
        }
        var scheduler = new TeamScheduler(_dispatcher().DispatchDetailedAsync, _experts) { Parallel = Parallel };
        var session = new TeamSession(team, scope.RunId, scheduler, new UiHost(this, scope.Post, scope.Confirm), _dispatcher(), _experts, () => On);
        _sessions[conv.Id] = session;
        return new RunContribution
        {
            // 团队名单和队长的工作方式：同一个对话里不变，放系统提示词（提示词缓存能命中）
            SystemPrompt = TeamLeader.PromptSection(team, _experts, session.UsableMembers),
            Tools = new ITool[] { new TeamPlanTool(session), new TeamAssignTool(session) },
            ModelId = team.ModelId,
            MaxRunTokens = RunTokens,
            OnEnd = () => _sessions.TryRemove(new KeyValuePair<string, TeamSession>(conv.Id, session)),
        };
    }

    private void ReportStart(string teamId) => _ = Task.Run(async () =>
    {
        try
        {
            await _server.ReportTeamStartAsync(teamId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log($"上报团队任务次数失败：{ex.Message}");
        }
    });

    // ---------- 界面桥模块 ----------

    public IReadOnlyCollection<string> Methods { get; } = new[]
    {
        "teams.list", "teams.save", "teams.dissolve", "teams.copy", "team.planAnswer", "team.stopMember",
    };

    public async Task<object?> HandleAsync(string method, BridgeArgs args, CancellationToken ct)
    {
        switch (method)
        {
            case "teams.list":
                return await ListAsync(ct);
            case "teams.save":
                return await SaveAsync(args.OptStr("id"), args.Get<TeamInput>("team") ?? new TeamInput(), ct);
            case "teams.dissolve":
                await DissolveAsync(args.Str("id"), ct);
                return null;
            case "teams.copy":
                return await CopyAsync(args.Str("id"), ct);
            case "team.planAnswer":
            {
                // 计划卡：start = 开始执行（removed 是员工去掉的成员）；revise = 让队长改（text）；cancel = 取消
                var action = args.Str("action") switch
                {
                    "start" => TeamPlanAction.Start,
                    "revise" => TeamPlanAction.Revise,
                    _ => TeamPlanAction.Cancel,
                };
                return AnswerPlan(args.Str("requestId"), new TeamPlanAnswer(action, args.StrList("removed"), args.Str("text")));
            }
            case "team.stopMember":
                return StopMember(args.Str("conversationId"), args.Str("expertId"));
            default:
                throw new ArgumentException($"团队模块不处理 {method}");
        }
    }

    /// <summary>团队任务的界面部分：计划卡、开工授权卡、成员进度和看板推给页面（事件 team.*）。</summary>
    private sealed class UiHost(TeamFeature feature, Action<object> post, IConfirmationHandler confirm) : ITeamHost
    {
        public async Task<TeamPlanAnswer> ReviewPlanAsync(TeamPlanReview review, CancellationToken ct)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var pending = new TaskCompletionSource<TeamPlanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            feature._plans[requestId] = pending;
            post(new
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
                    name = feature._experts(i.ExpertId)?.Name ?? i.ExpertId,
                    avatar = feature._experts(i.ExpertId)?.Avatar ?? "",
                    readOnly = feature._experts(i.ExpertId)?.ReadOnly == true,
                    task = i.Task,
                    done = i.Done,
                    dependsOn = i.DependsOn,
                }).ToList(),
                skipped = review.Skipped.Select(id => feature._experts(id)?.Name ?? id).ToList(),
                estimateTokens = review.EstimateTokens,
                budgetTokens = feature.RunTokens,
            });
            await using var registration = ct.Register(() => pending.TrySetResult(new TeamPlanAnswer(TeamPlanAction.Cancel, Array.Empty<string>(), "")));
            try
            {
                return await pending.Task;
            }
            finally
            {
                feature._plans.TryRemove(requestId, out _);
                post(new { type = "team.planClosed", conversationId = review.ConversationId, requestId });
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

        public void PlanStarted(TeamPlanReview review, TeamPlan plan) => post(new
        {
            type = "team.started",
            conversationId = review.ConversationId,
            runId = review.RunId,
            callId = review.CallId,
            round = review.Round,
            items = plan.Items.Select(i => new { expertId = i.ExpertId, task = i.Task, done = i.Done, dependsOn = i.DependsOn }).ToList(),
        });

        public void MemberUpdated(string conversationId, TeamMemberUpdate update) => post(new
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

        public void BoardAppended(string conversationId, string runId, TeamBoardEntry entry) => post(new
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

        public void RoundFinished(string conversationId, string runId, IReadOnlyList<TeamMemberResult> results) => post(new
        {
            type = "team.round",
            conversationId,
            runId,
            results = results.Select(r => new { expertId = r.Item.ExpertId, status = r.Status.ToString().ToLowerInvariant(), delivery = r.Delivery, retries = r.Retries }).ToList(),
        });
    }
}
