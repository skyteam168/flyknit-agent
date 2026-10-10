using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 防 token 浪费的几道闸：LoopGuard 认得的近似重复、全局预算（累计 token、连续无进展）、
/// 上下文大小的估算（算上工具定义、按真实用量校准）和每次调用的输入构成。
/// </summary>
public class RunBudgetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-budget").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Gateway : IChatGateway
    {
        private readonly Func<int, ChatRequest, ChatTurn> _reply;
        public List<ChatRequest> Requests { get; } = new();
        public Gateway(Func<int, ChatRequest, ChatTurn> reply) => _reply = reply;

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_reply(Requests.Count, request));
        }
    }

    private sealed class Reject : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.Reject);
    }

    private sealed class Observer : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }

    private ToolContext Context() => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _dir,
        Permission = PermissionMode.Workspace,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.Sandbox] = new() { Value = false, Locked = true },
        }),
    };

    private static ToolCall Call(string name, string args) => new("call_" + Guid.NewGuid().ToString("N")[..6], name, args);

    private string ListDir(string sub) =>
        $"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_dir, sub))}}}";

    private static ChatTurn Turn(string name, string args, int tokens = 0) => new()
    {
        ToolCalls = new[] { Call(name, args) },
        Usage = tokens > 0 ? new TokenUsage(tokens, 0) : null,
    };

    // ---------- LoopGuard：近似重复 ----------

    [Fact]
    public void RewritingTheSameFileWithDifferentContentIsCaught()
    {
        var guard = new LoopGuard();
        var verdicts = Enumerable.Range(0, 8)
            .Select(i => guard.Observe(Call("write_file", $"{{\"path\":\"D:/out/report.md\",\"content\":\"第 {i} 版\"}}"), "已写入"))
            .ToList();
        Assert.Equal(LoopVerdict.Nudge, verdicts[3]);
        Assert.True(verdicts.Take(3).All(v => v == LoopVerdict.Ok));
        Assert.Equal(LoopVerdict.Stop, verdicts[7]);
        Assert.Contains("重写同一个文件", guard.NudgeFor("write_file"));

        // 写不同的文件是正常推进
        var progress = new LoopGuard();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(LoopVerdict.Ok, progress.Observe(Call("write_file", $"{{\"path\":\"D:/out/{i}.md\",\"content\":\"x\"}}"), "已写入"));
        }
    }

    [Fact]
    public void ChangingOnlyTheTimeoutOrSpacingIsStillARepeat()
    {
        var guard = new LoopGuard();
        guard.Observe(Call("run_shell", "{\"command\":\"Get-Process  excel\",\"timeout_seconds\":60}"), "没有找到");
        guard.Observe(Call("run_shell", "{\"command\":\"Get-Process excel\",\"timeout_seconds\":120}"), "没有找到");
        Assert.Equal(LoopVerdict.Nudge,
            guard.Observe(Call("run_shell", "{\"command\":\"Get-Process\\nexcel\",\"timeout_seconds\":300}"), "没有找到"));
        Assert.False(guard.LastWasNew);
    }

    // ---------- 全局预算 ----------

    [Fact]
    public async Task TokenBudgetRemindsThenStopsWithASummary()
    {
        for (var i = 0; i < 20; i++) Directory.CreateDirectory(Path.Combine(_dir, $"d{i}"));
        var gateway = new Gateway((n, req) => req.Tools is null
            ? new ChatTurn { Content = "已经看了 4 个目录，还差 16 个。" }
            : Turn("list_dir", ListDir($"d{n}"), tokens: 300));

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject(), options: new AgentOptions { MaxRunTokens = 1000 })
            .RunAsync(new List<ChatMessage> { ChatMessage.User("看看每个目录") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Budget, result.StopReason);
        // 300 × 3 = 900 ≥ 70% 提醒；300 × 4 = 1200 ≥ 1000 停下，再要一次不带工具的总结
        Assert.Equal(5, gateway.Requests.Count);
        Assert.Null(gateway.Requests[^1].Tools);
        var tools = result.NewMessages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Contains("请抓紧收尾", tools[2].Content);
        Assert.Contains("到达单次任务的上限", tools[^1].Content);
        Assert.Contains("还差 16 个", result.NewMessages[^1].Content);
        var guards = result.Trace!.Steps.Where(s => s.Kind == "guard").ToList();
        Assert.Contains(guards, s => s.Status == "warning");
        Assert.Contains(guards, s => s.Status == "stopped");
    }

    [Fact]
    public async Task GoingRoundInCirclesIsStoppedEvenWhenEveryStepLooksDifferent()
    {
        // 轮流看 7 个目录、看完再从头看：每一步都和上一步不一样，LoopGuard 的窗口认不出来，
        // 但第二圈开始就全是做过的操作——没有新进展
        for (var i = 0; i < 7; i++) Directory.CreateDirectory(Path.Combine(_dir, $"d{i}"));
        var gateway = new Gateway((n, req) => req.Tools is null
            ? new ChatTurn { Content = "反复检查了同样的目录，需要你确认要找什么。" }
            : Turn("list_dir", ListDir($"d{(n - 1) % 7}")));

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
            .RunAsync(new List<ChatMessage> { ChatMessage.User("找文件") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Budget, result.StopReason);
        // 7 轮有进展 + 8 轮没进展就停 + 1 次总结，而不是跑满 100 轮
        Assert.Equal(16, gateway.Requests.Count);
        Assert.Contains(result.NewMessages, m => m.Role == ChatRole.Tool && m.Content.Contains("已经连续 4 轮没有新的进展"));
        Assert.Equal(8, result.MaxNoProgressStreak);
    }

    [Fact]
    public async Task RealProgressIsNeverStoppedByTheProgressCheck()
    {
        for (var i = 0; i < 30; i++) Directory.CreateDirectory(Path.Combine(_dir, $"d{i}"));
        var gateway = new Gateway((n, req) => n <= 30 ? Turn("list_dir", ListDir($"d{n - 1}")) : new ChatTurn { Content = "全部看完了" });

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
            .RunAsync(new List<ChatMessage> { ChatMessage.User("看看每个目录") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.Equal(0, result.GuardNudges);
        Assert.DoesNotContain(result.Trace!.Steps, s => s.Kind == "guard");
    }

    [Fact]
    public void ReplanningOverAndOverGetsOneReminder()
    {
        var budget = new RunBudget { ReplanNudgeAt = 2 };
        budget.ObserveReplan();
        Assert.Equal(BudgetVerdict.Ok, budget.Check(out _));
        budget.ObserveReplan();
        Assert.Equal(BudgetVerdict.Nudge, budget.Check(out var note));
        Assert.Contains("计划已经改了 2 次", note);
        budget.ObserveReplan();
        Assert.Equal(BudgetVerdict.Ok, budget.Check(out _));
    }

    // ---------- 上下文：估算和构成 ----------

    [Fact]
    public void ToolDefinitionsAndRealUsageCountTowardsTheContextSize()
    {
        var manager = new ContextManager(new Gateway((_, _) => new ChatTurn()), "系统提示", contextLength: 131072);
        var history = new List<ChatMessage> { ChatMessage.System("系统提示"), ChatMessage.User("你好") };
        var bare = manager.Measure(history);

        manager.ToolTokens = 30_000;
        Assert.Equal(bare + 30_000, manager.Measure(history));

        // 模型说实际发了估算的两倍：之后的估算也按两倍算（压缩不会再一直「跳过」）
        manager.PrepareAsync(history, CancellationToken.None).GetAwaiter().GetResult();
        manager.Observe(new ChatTurn { Usage = new TokenUsage((bare + 30_000) * 2, 10) });
        Assert.InRange(manager.Measure(history), (bare + 30_000) * 2 - 1, (bare + 30_000) * 2 + 1);

        // 估多了不往回调：顶多早一点裁剪
        manager.PrepareAsync(history, CancellationToken.None).GetAwaiter().GetResult();
        manager.Observe(new ChatTurn { Usage = new TokenUsage(10, 10) });
        Assert.Equal(bare + 30_000, manager.Measure(history));
    }

    [Fact]
    public void BreakdownShowsWhereTheInputGoes()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.System(new string('系', 400)),
            ChatMessage.User("整理一下报表"),
            ChatMessage.ToolResult(Call("read_file", "{}"), new string('x', 3000)),
        };
        var b = PromptBreakdown.Of(history, toolTokens: 1000);
        Assert.Equal(1000, b.Tools);
        Assert.True(b.ToolOutputs > b.Conversation);
        var text = b.Describe(4000);
        Assert.StartsWith("输入 4,000：", text);
        Assert.Contains("工具定义", text);
        Assert.Contains("工具输出", text);
    }

    [Fact]
    public async Task EveryModelCallInTheTraceSaysWhereItsInputWent()
    {
        var gateway = new Gateway((n, req) => n == 1
            ? Turn("list_dir", ListDir("."), tokens: 5000)
            : new ChatTurn { Content = "好了", Usage = new TokenUsage(5200, 20) });

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
            .RunAsync(new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("看看") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        var models = result.Trace!.Steps.Where(s => s.Kind == "model").ToList();
        Assert.Equal(2, models.Count);
        Assert.StartsWith("输入 5,000：", models[0].Detail);
        Assert.Contains("工具定义", models[0].Detail);
    }
}
