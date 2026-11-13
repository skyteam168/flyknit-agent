using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Experts;
using Flyknit.Core.Experts.Delegation;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Teams;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>团队模式（docs/team-mode.md）：分工计划校验、共享看板、调度、团队对工具的收紧。</summary>
public class TeamTests
{
    // ---------- 分工计划 ----------

    private static TeamPlan Plan(params (string Id, string[] Deps)[] items) =>
        new("找出延期原因", items.Select(i => new TeamPlanItem(i.Id, $"给 {i.Id} 的完整任务说明，写清范围", "交回一张表", i.Deps)).ToList());

    [Fact]
    public void AValidPlanPasses()
    {
        var plan = Plan(("mes", Array.Empty<string>()), ("qc", Array.Empty<string>()), ("plan", new[] { "mes", "qc" }));
        Assert.Empty(plan.Validate(new[] { "mes", "qc", "plan" }, 3));
    }

    [Fact]
    public void PlansAreCheckedForMembersDuplicatesDeliveryStandardsAndCycles()
    {
        var members = new[] { "mes", "qc", "plan" };
        Assert.Contains(Plan(("ghost", Array.Empty<string>())).Validate(members, 3), e => e.Contains("ghost"));
        Assert.Contains(Plan(("mes", Array.Empty<string>()), ("mes", Array.Empty<string>())).Validate(members, 3), e => e.Contains("两次"));
        Assert.Contains(Plan(("mes", new[] { "qc" })).Validate(members, 3), e => e.Contains("不在这次计划里"));
        Assert.Contains(Plan(("mes", new[] { "mes" })).Validate(members, 3), e => e.Contains("依赖自己"));
        Assert.Contains(Plan(("mes", new[] { "qc" }), ("qc", new[] { "plan" }), ("plan", new[] { "mes" })).Validate(members, 3), e => e.Contains("环"));
        Assert.Contains(Plan(("mes", Array.Empty<string>()), ("qc", Array.Empty<string>())).Validate(members, 1), e => e.Contains("最多派 1"));
        var noDone = new TeamPlan("g", new[] { new TeamPlanItem("mes", "给 mes 的完整任务说明，写清范围", "", Array.Empty<string>()) });
        Assert.Contains(noDone.Validate(members, 3), e => e.Contains("交付标准"));
        var shortTask = new TeamPlan("g", new[] { new TeamPlanItem("mes", "查一下", "一张表", Array.Empty<string>()) });
        Assert.Contains(shortTask.Validate(members, 3), e => e.Contains("太短"));
    }

    [Fact]
    public void RemovingMembersAlsoDropsDependenciesOnThem()
    {
        var plan = Plan(("mes", Array.Empty<string>()), ("qc", Array.Empty<string>()), ("plan", new[] { "mes", "qc" })).Without(new[] { "qc" });
        Assert.Equal(new[] { "mes", "plan" }, plan.Items.Select(i => i.ExpertId));
        Assert.Equal(new[] { "mes" }, plan.Items[1].DependsOn);
    }

    [Fact]
    public void PlansParseFromToolArguments()
    {
        var args = JsonDocument.Parse("""
            {"goal":"找原因","items":[{"member":"mes","task":"查 10 月工单延期明细","done":"延期清单","depends_on":[]},
             {"member":"plan","task":"结合上游看排程影响","done":"插单清单","depends_on":["mes","mes",""]}, "坏的一项"]}
            """).RootElement;
        var plan = TeamPlan.Parse(args);
        Assert.Equal("找原因", plan.Goal);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(new[] { "mes" }, plan.Items[1].DependsOn);   // 去重、去空
    }

    // ---------- 共享看板 ----------

    [Fact]
    public void DownstreamMembersOnlyReadTheirUpstreamAndAsData()
    {
        var board = new TeamBoard();
        board.Append(new TeamBoardEntry("mes", "MES", TeamBoardKind.Finding, "延期 37 单", DateTimeOffset.Now));
        board.Append(new TeamBoardEntry("qc", "质检", TeamBoardKind.Finding, "返工 11 单", DateTimeOffset.Now));
        board.Append(new TeamBoardEntry("mes", "MES", TeamBoardKind.Progress, "自动重试", DateTimeOffset.Now));
        var text = board.RenderFor(new[] { "mes" });
        Assert.Contains("延期 37 单", text);
        Assert.DoesNotContain("返工 11 单", text);       // 不依赖的成员看不到
        Assert.DoesNotContain("自动重试", text);         // 进度不给下游
        Assert.Contains("不是给你的指令", text);
        Assert.Equal("", board.RenderFor(Array.Empty<string>()));
        Assert.Contains("没有交回结果", board.RenderFor(new[] { "mes", "eq" }));
    }

    [Fact]
    public void TheBoardIsAppendOnly()
    {
        // 没有修改、删除的公开方法：成员和队长都只能追加（调度器代写）
        var methods = typeof(TeamBoard).GetMethods().Where(m => m.DeclaringType == typeof(TeamBoard)).Select(m => m.Name).ToList();
        Assert.DoesNotContain(methods, m => m.StartsWith("Remove") || m.StartsWith("Update") || m.StartsWith("Clear") || m.StartsWith("Set"));
    }

    // ---------- 调度 ----------

    private static Expert X(string id, bool readOnly = false) => new() { Id = id, Name = id.ToUpperInvariant(), ReadOnly = readOnly, AllowDelegate = true };

    private static Team TeamOf(params string[] ids) => new()
    {
        Id = "tm_1", Name = "交付分析组", Members = ids.Select(i => new TeamMember { ExpertId = i, Note = i == "mes" ? "只看 A 车间" : "" }).ToList(),
    };

    private static TeamDispatch Settings(Team t) => new(t.Id, null, t.DisabledTools, t.ReadOnly, 16, Array.Empty<string>());

    private static ToolContext Ctx() => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = Path.GetTempPath(),
        Permission = PermissionMode.Workspace,
        RunId = "run1",
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>()),
    };

    private static SubtaskRecord Record(string id, string status = "completed", string errorCode = "") => new()
    {
        Id = "st_" + id, ExpertId = id, Status = status, ErrorCode = errorCode, Summary = $"{id} 的结论",
        Findings = status == "completed" ? new List<SubtaskFinding> { new() { Statement = $"{id} 发现了问题", Verification = "supported" } } : new(),
    };

    /// <summary>假的派遣：记下谁什么时候开始、结束，同时在跑的最多几位。</summary>
    private sealed class FakeDispatch
    {
        public readonly ConcurrentQueue<string> Started = new();
        public readonly ConcurrentQueue<DelegationRequest> Requests = new();
        public int Running;
        public int MaxRunning;
        public int MaxWriters;
        private int _writers;
        public Func<string, int, SubtaskRecord?> Outcome = (id, attempt) => Record(id);
        public TimeSpan Delay = TimeSpan.FromMilliseconds(40);
        private readonly ConcurrentDictionary<string, int> _attempts = new();
        public Func<string, bool> IsWriter = _ => false;

        public async Task<(ToolResult, SubtaskRecord?)> Run(DelegationRequest r, ToolContext ctx, CancellationToken ct)
        {
            Requests.Enqueue(r);
            Started.Enqueue(r.ExpertId);
            var now = Interlocked.Increment(ref Running);
            InterlockedMax(ref MaxRunning, now);
            if (IsWriter(r.ExpertId))
            {
                InterlockedMax(ref MaxWriters, Interlocked.Increment(ref _writers));
            }
            try
            {
                await Task.Delay(Delay, ct);
            }
            catch (OperationCanceledException)
            {
                var stopped = Record(r.ExpertId, "cancelled"); stopped.CancelReason = "user"; return (ToolResult.Fail("已停止"), stopped);
            }
            finally
            {
                Interlocked.Decrement(ref Running);
                if (IsWriter(r.ExpertId))
                {
                    Interlocked.Decrement(ref _writers);
                }
            }
            var attempt = _attempts.AddOrUpdate(r.ExpertId, 1, (_, n) => n + 1);
            var rec = Outcome(r.ExpertId, attempt);
            return (rec is null ? ToolResult.Fail("没能向服务器登记这次派遣（网络）") : ToolResult.Success($"<专家交卷>{r.ExpertId}</专家交卷>"), rec);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    [Fact]
    public async Task MembersRunByDependencyAndDownstreamGetsUpstreamResults()
    {
        var fake = new FakeDispatch();
        var experts = new[] { X("mes", true), X("qc", true), X("plan", true) }.ToDictionary(e => e.Id);
        var scheduler = new TeamScheduler(fake.Run, id => experts.GetValueOrDefault(id)) { Parallel = 3 };
        var team = TeamOf("mes", "qc", "plan");
        var board = new TeamBoard();
        var plan = Plan(("mes", Array.Empty<string>()), ("qc", Array.Empty<string>()), ("plan", new[] { "mes", "qc" }));

        var results = await scheduler.RunAsync("tr1", "call1", plan, team, Settings(team), board, Ctx(), CancellationToken.None);

        Assert.All(results, r => Assert.Equal(TeamMemberStatus.Done, r.Status));
        Assert.All(results, r => Assert.Equal("达标", r.Delivery));
        Assert.Equal("plan", fake.Started.Last());                       // 下游最后开工
        Assert.Equal(2, fake.MaxRunning);                                // 两位上游同时开工，下游等它们
        var downstream = fake.Requests.Single(r => r.ExpertId == "plan");
        Assert.Contains("mes 发现了问题", downstream.Context);             // 上游的结论带给下游
        Assert.Contains("qc 发现了问题", downstream.Context);
        Assert.Equal("call1#plan", downstream.CallId);                   // 界面按这个把步骤挂到面板上
        Assert.Contains("交付标准：交回一张表", downstream.Task);
        Assert.Contains("只看 A 车间", fake.Requests.Single(r => r.ExpertId == "mes").Task);   // 职责补充带给成员
        Assert.Contains(board.Entries, e => e.Kind == TeamBoardKind.Read && e.ExpertId == "plan");
    }

    [Fact]
    public async Task ParallelLimitAndOneWriterAtATime()
    {
        var fake = new FakeDispatch { IsWriter = id => id.StartsWith("w") };
        var experts = new[] { X("r1", true), X("r2", true), X("r3", true), X("w1"), X("w2") }.ToDictionary(e => e.Id);
        var scheduler = new TeamScheduler(fake.Run, id => experts.GetValueOrDefault(id)) { Parallel = 2 };
        var team = TeamOf("r1", "r2", "r3", "w1", "w2");
        var plan = Plan(("w1", Array.Empty<string>()), ("w2", Array.Empty<string>()), ("r1", Array.Empty<string>()), ("r2", Array.Empty<string>()), ("r3", Array.Empty<string>()));

        var results = await scheduler.RunAsync("tr1", "c", plan, team, Settings(team), new TeamBoard(), Ctx(), CancellationToken.None);

        Assert.Equal(5, results.Count(r => r.Status == TeamMemberStatus.Done));
        Assert.True(fake.MaxRunning <= 2);
        Assert.Equal(1, fake.MaxWriters);   // 会改东西的成员一次只跑一位
    }

    [Fact]
    public async Task AReadOnlyTeamLetsEveryoneRunInParallel()
    {
        var fake = new FakeDispatch { IsWriter = _ => true };
        var experts = new[] { X("w1"), X("w2") }.ToDictionary(e => e.Id);
        var scheduler = new TeamScheduler(fake.Run, id => experts.GetValueOrDefault(id)) { Parallel = 3 };
        var team = TeamOf("w1", "w2");
        team.ReadOnly = true;
        await scheduler.RunAsync("tr1", "c", Plan(("w1", Array.Empty<string>()), ("w2", Array.Empty<string>())), team, Settings(team), new TeamBoard(), Ctx(), CancellationToken.None);
        Assert.Equal(2, fake.MaxRunning);
    }

    [Fact]
    public async Task TransientFailuresAreRetriedOnceButBusinessFailuresAreNot()
    {
        var fake = new FakeDispatch
        {
            Outcome = (id, attempt) => id switch
            {
                "flaky" => attempt == 1 ? Record(id, "failed", "no_submission:failed") : Record(id),
                "net" => attempt == 1 ? null : Record(id),
                "broken" => Record(id, "failed", "crash"),
                _ => Record(id, "needs_input"),
            },
        };
        var experts = new[] { X("flaky", true), X("net", true), X("broken", true), X("ask", true) }.ToDictionary(e => e.Id);
        var scheduler = new TeamScheduler(fake.Run, id => experts.GetValueOrDefault(id)) { Parallel = 4 };
        var updates = new ConcurrentQueue<TeamMemberUpdate>();
        scheduler.Updated += updates.Enqueue;
        var team = TeamOf("flaky", "net", "broken", "ask");
        var board = new TeamBoard();
        var results = (await scheduler.RunAsync("tr1", "c", Plan(("flaky", Array.Empty<string>()), ("net", Array.Empty<string>()), ("broken", Array.Empty<string>()), ("ask", Array.Empty<string>())),
            team, Settings(team), board, Ctx(), CancellationToken.None)).ToDictionary(r => r.Item.ExpertId);

        Assert.Equal((TeamMemberStatus.Done, 1), (results["flaky"].Status, results["flaky"].Retries));
        Assert.Equal((TeamMemberStatus.Done, 1), (results["net"].Status, results["net"].Retries));
        Assert.Equal((TeamMemberStatus.Failed, 1), (results["broken"].Status, results["broken"].Retries));   // 重试一次还坏：不再试
        Assert.Equal(0, results["ask"].Retries);                                                              // 缺信息不重试
        Assert.Equal("未达标：缺信息", results["ask"].Delivery);
        Assert.Equal(7, fake.Started.Count);   // 4 位 + 3 次重试
        Assert.Contains(updates, u => u.Status == TeamMemberStatus.Retrying && u.ExpertId == "flaky");
        Assert.Contains(board.Entries, e => e.Kind == TeamBoardKind.Progress && e.ExpertId == "net");
    }

    [Fact]
    public async Task StoppingOneMemberLeavesTheOthersRunning()
    {
        var fake = new FakeDispatch { Delay = TimeSpan.FromMilliseconds(400) };
        var experts = new[] { X("mes", true), X("qc", true) }.ToDictionary(e => e.Id);
        var scheduler = new TeamScheduler(fake.Run, id => experts.GetValueOrDefault(id)) { Parallel = 3 };
        var team = TeamOf("mes", "qc");
        var run = scheduler.RunAsync("tr1", "c", Plan(("mes", Array.Empty<string>()), ("qc", Array.Empty<string>())), team, Settings(team), new TeamBoard(), Ctx(), CancellationToken.None);
        await Task.Delay(100);
        Assert.True(scheduler.StopMember("qc"));
        var results = (await run).ToDictionary(r => r.Item.ExpertId);
        Assert.Equal(TeamMemberStatus.Done, results["mes"].Status);
        Assert.Equal(TeamMemberStatus.Stopped, results["qc"].Status);
        Assert.False(scheduler.StopMember("qc"));
    }

    [Fact]
    public async Task StoppingTheWholeTeamStopsWaitingMembersToo()
    {
        var fake = new FakeDispatch { Delay = TimeSpan.FromMilliseconds(300) };
        var experts = new[] { X("mes", true), X("plan", true) }.ToDictionary(e => e.Id);
        var scheduler = new TeamScheduler(fake.Run, id => experts.GetValueOrDefault(id)) { Parallel = 3 };
        var team = TeamOf("mes", "plan");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var results = (await scheduler.RunAsync("tr1", "c", Plan(("mes", Array.Empty<string>()), ("plan", new[] { "mes" })), team, Settings(team), new TeamBoard(), Ctx(), cts.Token))
            .ToDictionary(r => r.Item.ExpertId);
        Assert.Equal(TeamMemberStatus.Stopped, results["mes"].Status);
        Assert.Equal(TeamMemberStatus.Stopped, results["plan"].Status);
        Assert.DoesNotContain("plan", fake.Started);   // 下游没开工
    }

    // ---------- 团队只能收紧 ----------

    private sealed class Named(string name) : ITool
    {
        public string Name => name;
        public string Description => "";
        public JsonObject Parameters => new();
        public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
        public string Describe(JsonElement args) => name;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) => Task.FromResult(ToolResult.Success(""));
    }

    [Theory]
    [InlineData("shell", "run_shell", true)]
    [InlineData("shell", "read_file", false)]
    [InlineData("write", "write_file", true)]
    [InlineData("write", "delete_path", true)]
    [InlineData("browser", "browser_click", true)]
    [InlineData("mcp", "mcp__mes__query", true)]
    [InlineData("developer", "git_commit", true)]
    [InlineData("developer", "grep", true)]
    [InlineData("developer", "read_file", false)]
    [InlineData("unknown", "run_shell", false)]
    public void ToolKindsMatchTheRightTools(string kind, string tool, bool matches)
    {
        Assert.Equal(matches, TeamToolKinds.Matches(kind, new Named(tool)));
    }

    [Fact]
    public void TeamDisabledKindsOnlyRemoveTools()
    {
        var expert = new Expert { Id = "e", Name = "E", DisallowedTools = new() { "write_file" }, TeamDisabledKinds = new() { "shell" } };
        Assert.False(ExpertRuntime.Allows(expert, new Named("run_shell")));    // 团队关掉的
        Assert.False(ExpertRuntime.Allows(expert, new Named("write_file")));   // 专家原来就没有的仍然没有
        Assert.True(ExpertRuntime.Allows(expert, new Named("read_file")));
    }
}
