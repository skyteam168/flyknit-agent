using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Flyknit.Core.Watchdog;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>看门狗（docs/c3-watchdog.md）：进展信号、暂停计时不误伤长命令、先提醒后停、模型判断、脱敏。</summary>
public sealed class WatchdogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-watchdog").FullName;
    private DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private ProgressSignals Signals() => new("把共享盘里的日报汇总成一张表", () => _now);

    private static ToolCall Call(string name, string args) => new("c" + Guid.NewGuid().ToString("N")[..6], name, args);

    /// <summary>做一步：工具开始 → 过 dt → 做完。</summary>
    private void Step(ProgressSignals s, ToolCall call, string output = "ok", TimeSpan? dt = null, bool ok = true, params string[] outputs)
    {
        s.ToolStarted();
        _now += dt ?? TimeSpan.FromSeconds(5);
        s.ToolFinished(call, new ToolResult(ok, output) { Outputs = outputs });
    }

    // ---------- 进展信号 ----------

    [Fact]
    public void RepeatingTheSameThingForTenMinutesIsSuspicious()
    {
        var s = Signals();
        Step(s, Call("run_shell", """{"command":"dir D:\\日报"}"""));      // 新操作：算进展
        for (var i = 0; i < 9; i++)
        {
            _now += TimeSpan.FromSeconds(70);                                   // 模型想了一会儿（不暂停计时）
            Step(s, Call("run_shell", """{"command":"dir D:\\日报"}"""));  // 做过的操作：不算
        }
        Assert.True(s.IdleFor >= Ten);
        Assert.True(s.StepsSinceProgress >= ProgressSignals.MinSteps);
        Assert.True(s.Suspicious(Ten));
    }

    [Fact]
    public void ALongRunningCommandIsNeverSuspicious()
    {
        // 一条要 40 分钟的编译：工具执行期间暂停计时
        var s = Signals();
        for (var i = 0; i < 9; i++)
        {
            Step(s, Call("run_shell", """{"command":"dotnet build"}"""));
        }
        s.ToolStarted();
        _now += TimeSpan.FromMinutes(40);
        Assert.False(s.Suspicious(Ten));        // 还在执行
        Assert.True(s.IdleFor < Ten);
        s.ToolFinished(Call("run_shell", """{"command":"dotnet build"}"""), new ToolResult(true, "生成成功"));
        Assert.True(s.IdleFor < Ten);            // 执行期间的 40 分钟不算
    }

    [Fact]
    public void WaitingForTheEmployeeIsNotCounted()
    {
        var s = Signals();
        for (var i = 0; i < 9; i++)
        {
            Step(s, Call("list_dir", """{"path":"D:\\a"}"""));
        }
        s.Hold();                                // 等确认 / ask_user / 暂停
        _now += TimeSpan.FromMinutes(30);
        Assert.False(s.Suspicious(Ten));
        s.Release();
        Assert.True(s.IdleFor < Ten);
    }

    [Fact]
    public void SlowButNeverIdleIsNotSuspicious()
    {
        // 十几分钟只走了 3 步（每步都很慢）：不到 8 步，不判可疑
        var s = Signals();
        for (var i = 0; i < 3; i++)
        {
            _now += TimeSpan.FromMinutes(5);
            Step(s, Call("run_shell", """{"command":"echo same"}"""));
        }
        Assert.True(s.IdleFor >= Ten);
        Assert.False(s.Suspicious(Ten));
    }

    [Fact]
    public void NewActionsPlanProgressAndOutputsCountAsProgress()
    {
        var s = Signals();
        for (var i = 0; i < 12; i++)
        {
            _now += TimeSpan.FromMinutes(1);
            Step(s, Call("run_shell", $$"""{"command":"python step{{i}}.py"}"""));  // 每次都是新的操作
        }
        Assert.False(s.Suspicious(Ten));

        var p = Signals();
        for (var i = 0; i < 12; i++)
        {
            _now += TimeSpan.FromMinutes(1);
            Step(p, Call("run_shell", """{"command":"python same.py"}"""), outputs: i == 11 ? new[] { "D:\\out.xlsx" } : Array.Empty<string>());
        }
        Assert.False(p.Suspicious(Ten));          // 最后一步产出了文件

        var plan = Signals();
        for (var i = 0; i < 12; i++)
        {
            _now += TimeSpan.FromMinutes(1);
            Step(plan, Call("run_shell", """{"command":"python same.py"}"""));
        }
        plan.PlanUpdated(new[] { new PlanItem("读", "completed"), new PlanItem("汇总", "in_progress") });
        Assert.False(plan.Suspicious(Ten));        // 计划推进了
        plan.PlanUpdated(new[] { new PlanItem("读", "completed"), new PlanItem("汇总", "in_progress") });
        Assert.Equal(0, plan.StepsSinceProgress);  // 再报一遍同样的计划：不另算，但也不倒退
    }

    [Fact]
    public void ReadingEndlesslyStopsCountingAsProgress()
    {
        // 每次读一个不同的文件：前 15 次算进展，之后只看不做不再算
        var s = Signals();
        for (var i = 0; i < ProgressSignals.ReadOnlyStreakLimit + 10; i++)
        {
            _now += TimeSpan.FromMinutes(1);
            Step(s, Call("read_file", $$"""{"path":"D:\\docs\\{{i}}.txt"}"""));
        }
        Assert.True(s.Suspicious(Ten));

        // 中间做了一次写操作就重新开始算
        Step(s, Call("write_file", """{"path":"D:\\out.txt","content":"x"}"""));
        Step(s, Call("read_file", """{"path":"D:\\docs\\new.txt"}"""));
        Assert.Equal(0, s.StepsSinceProgress);
    }

    [Fact]
    public void BackgroundCommandOutputThatChangesIsProgress()
    {
        var s = Signals();
        for (var i = 0; i < 12; i++)
        {
            _now += TimeSpan.FromMinutes(1);
            Step(s, Call("shell_output", """{"id":"1","wait_seconds":30}"""), output: $"已编译 {i * 10}%");
        }
        Assert.False(s.Suspicious(Ten));
        for (var i = 0; i < 12; i++)
        {
            _now += TimeSpan.FromMinutes(1);
            Step(s, Call("shell_output", """{"id":"1","wait_seconds":30}"""), output: "已编译 100%，卡住了");
        }
        Assert.True(s.Suspicious(Ten));            // 输出不再变化
    }

    [Fact]
    public void AStoppedRunDoesNotLeaveTheTimerPaused()
    {
        var s = Signals();
        for (var i = 0; i < 9; i++)
        {
            Step(s, Call("run_shell", """{"command":"echo same"}"""));
        }
        s.ToolStarted();                           // 工具做到一半被停：没有 ToolFinished
        s.ResetHolds();
        _now += TimeSpan.FromMinutes(12);
        Assert.True(s.Suspicious(Ten));
    }

    // ---------- 先提醒后停 ----------

    [Fact]
    public void FirstRemindThenStopOnlyWithAutoStop()
    {
        var p = new WatchdogPolicy();
        Assert.False(p.ShouldJudge(false, _now));
        Assert.True(p.ShouldJudge(true, _now));
        Assert.Equal(WatchAction.Remind, p.OnVerdict(WatchVerdict.Stalled, autoStop: true, _now));
        Assert.False(p.ShouldJudge(true, _now.AddMinutes(2)));          // 两次判断至少隔 5 分钟
        Assert.True(p.ShouldJudge(true, _now.AddMinutes(10)));
        Assert.Equal(WatchAction.Stop, p.OnVerdict(WatchVerdict.Drifting, autoStop: true, _now.AddMinutes(10)));
        Assert.False(p.ShouldJudge(true, _now.AddMinutes(30)));         // 停了就不再判

        var notStopping = new WatchdogPolicy();
        notStopping.OnVerdict(WatchVerdict.Stalled, autoStop: false, _now);
        Assert.Equal(WatchAction.Notify, notStopping.OnVerdict(WatchVerdict.Stalled, autoStop: false, _now.AddMinutes(10)));
        Assert.Equal(WatchAction.None, notStopping.OnVerdict(WatchVerdict.Stalled, autoStop: false, _now.AddMinutes(20)));
    }

    [Fact]
    public void ProgressingResetsAndModelFailureNeverStops()
    {
        var p = new WatchdogPolicy();
        Assert.Equal(WatchAction.Remind, p.OnVerdict(WatchVerdict.Stalled, true, _now));
        Assert.Equal(WatchAction.None, p.OnVerdict(WatchVerdict.Progressing, true, _now.AddMinutes(10)));
        Assert.Equal(WatchAction.Remind, p.OnVerdict(WatchVerdict.Stalled, true, _now.AddMinutes(20)));   // 缓过来后重新从提醒开始

        var offline = new WatchdogPolicy();
        Assert.Equal(WatchAction.Remind, offline.OnVerdict(null, true, _now));                  // 模型调不通：只按程序信号提醒
        Assert.Equal(WatchAction.Notify, offline.OnVerdict(null, true, _now.AddMinutes(10)));   // 再通知，不停
        Assert.Equal(WatchAction.None, offline.OnVerdict(null, true, _now.AddMinutes(20)));
    }

    [Fact]
    public void DismissedTasksAreLeftAloneAndJudgementsAreCapped()
    {
        var p = new WatchdogPolicy();
        p.OnVerdict(WatchVerdict.Stalled, true, _now);
        p.Dismiss();
        Assert.False(p.ShouldJudge(true, _now.AddHours(1)));
        Assert.Equal(WatchAction.None, p.OnVerdict(WatchVerdict.Stalled, true, _now.AddHours(1)));

        var capped = new WatchdogPolicy();
        for (var i = 0; i < WatchdogPolicy.MaxJudgements; i++)
        {
            Assert.True(capped.ShouldJudge(true, _now.AddMinutes(10 * i)));
            capped.OnVerdict(WatchVerdict.Progressing, true, _now.AddMinutes(10 * i));
        }
        Assert.False(capped.ShouldJudge(true, _now.AddHours(5)));
    }

    // ---------- 模型判断 ----------

    [Theory]
    [InlineData("""{"verdict":"stalled","reason":"一直在列同一个目录","advice":"换个办法"}""", WatchVerdict.Stalled)]
    [InlineData("""好的：{"verdict":"Drifting","reason":"在修格式","advice":"回到汇总"}""", WatchVerdict.Drifting)]
    [InlineData("""{"verdict":"progressing","reason":"","advice":""}""", WatchVerdict.Progressing)]
    [InlineData("看起来卡住了", WatchVerdict.Progressing)]                  // 解析不了：宁可漏判
    [InlineData("""{"verdict":"maybe"}""", WatchVerdict.Progressing)]
    [InlineData("", WatchVerdict.Progressing)]
    public void ParsingFallsBackToProgressing(string reply, WatchVerdict expected)
    {
        Assert.Equal(expected, WatchdogJudge.Parse(reply).Verdict);
    }

    [Fact]
    public void WhatTheModelSeesIsShortAndRedacted()
    {
        var s = new ProgressSignals("登录 ERP，密码是 password=Hunter2Secret!，导出报表", () => _now);
        Step(s, Call("run_shell", """{"command":"curl -H 'Authorization: Bearer sk-abcdefghijklmnopqrstuvwxyz123456' https://erp"}"""),
            output: new string('x', 5000));
        s.PlanUpdated(new[] { new PlanItem("登录", "completed"), new PlanItem("导出", "in_progress") });
        var text = WatchdogJudge.Describe(s, 12_000, _now);
        Assert.DoesNotContain("Hunter2Secret", text);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz123456", text);
        Assert.Contains("[x] 登录", text);
        Assert.Contains("[>] 导出", text);
        Assert.Contains("run_shell", text);
        Assert.True(text.Length < 2000);           // 工具输出只取开头一小段

        var request = WatchdogJudge.Request(text);
        Assert.Equal(Scenes.Title, request.Scene);
        Assert.Equal(Purposes.Watchdog, request.Purpose);
        Assert.Null(request.Tools);
    }

    private sealed class Throwing : IChatGateway
    {
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) =>
            throw new GatewayException("配额用完");
    }

    private sealed class Replying(string content) : IChatGateway
    {
        public ChatRequest? Seen { get; private set; }
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new ChatTurn { Content = content });
        }
    }

    [Fact]
    public async Task AFailedModelCallReturnsNull()
    {
        Assert.Null(await WatchdogJudge.JudgeAsync(new Throwing(), Signals(), 0, CancellationToken.None));
        var gw = new Replying("""{"verdict":"stalled","reason":"重复","advice":"换办法"}""");
        var j = await WatchdogJudge.JudgeAsync(gw, Signals(), 0, CancellationToken.None);
        Assert.Equal(WatchVerdict.Stalled, j!.Verdict);
        Assert.Equal(Purposes.Watchdog, gw.Seen!.Purpose);
        Assert.Contains("换办法", WatchdogJudge.ReminderText(j));
        Assert.Contains("看门狗", WatchdogJudge.ReminderText(null));
    }

    // ---------- AgentLoop 的提醒钩子 ----------

    private sealed class Recording(params ChatTurn[] turns) : IChatGateway
    {
        private readonly Queue<ChatTurn> _turns = new(turns);
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = new();
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Requests.Add(request.Messages.ToList());
            return Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : new ChatTurn { Content = "完成" });
        }
    }

    private sealed class Quiet : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }

    private sealed class Never : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.Reject);
    }

    [Fact]
    public async Task TheAdvisoryIsAppendedToTheNextToolResultOnce()
    {
        var path = System.Text.Json.JsonSerializer.Serialize(_dir);
        var gw = new Recording(
            new ChatTurn { ToolCalls = new[] { Call("list_dir", $$"""{"path":{{path}}}""") } },
            new ChatTurn { ToolCalls = new[] { Call("list_dir", $$"""{"path":{{path}}}""") } },
            new ChatTurn { Content = "完成" });
        var pending = WatchdogJudge.ReminderText(new WatchJudgement(WatchVerdict.Stalled, "一直在列目录", "换个办法"));
        var signals = Signals();
        var ctx = new ToolContext
        {
            Policy = CommandPolicy.Default(),
            ConversationId = "c1",
            Workspace = _dir,
            Permission = PermissionMode.Workspace,
            Security = new SecuritySettings(new Dictionary<string, SecurityItem> { [SecuritySettings.Sandbox] = new() { Value = false, Locked = true } }),
        };
        var loop = new AgentLoop(gw, ToolRegistry.CreateDefault(), new Never())
        {
            Advisory = () => Interlocked.Exchange(ref pending, null),
        };
        await loop.RunAsync(new List<ChatMessage> { ChatMessage.User("看看目录") }, Scenes.Agent, ctx, new WatchdogObserver(new Quiet(), signals), true, CancellationToken.None);

        var second = gw.Requests[1].Last(m => m.Role == ChatRole.Tool);
        Assert.Contains("看门狗", second.Content);
        Assert.Contains("换个办法", second.Content);
        var third = gw.Requests[2].Last(m => m.Role == ChatRole.Tool);
        Assert.DoesNotContain("看门狗", third.Content);   // 只附一次
        Assert.Equal(2, signals.TotalSteps);               // 观察者把每一步都记进了进展信号
        Assert.False(signals.Holding);
    }
}
