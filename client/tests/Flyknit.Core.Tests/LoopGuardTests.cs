using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>空转检测、长任务的自查提醒和暂停前总结。</summary>
public class LoopGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-loop").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>按脚本回复的假模型，记下每次请求有没有带工具。</summary>
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

    private static ChatTurn Turn(string name, string args) => new() { ToolCalls = new[] { Call(name, args) } };

    private string ListDir(string? sub = null) =>
        $"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(sub is null ? _dir : Path.Combine(_dir, sub))}}}";

    // ---------- LoopGuard 本身 ----------

    [Fact]
    public void IdenticalCallsAndResultsAreNudgedThenStopped()
    {
        var guard = new LoopGuard();
        var verdicts = Enumerable.Range(0, 5).Select(_ => guard.Observe(Call("run_shell", "{\"command\":\"Test-Path a.txt\"}"), "False")).ToList();
        Assert.Equal(new[] { LoopVerdict.Ok, LoopVerdict.Ok, LoopVerdict.Nudge, LoopVerdict.Ok, LoopVerdict.Stop }, verdicts);
        Assert.Equal("run_shell", guard.LastTool);
    }

    [Fact]
    public void ArgumentOrderAndWhitespaceDoNotHideRepeats()
    {
        var guard = new LoopGuard();
        guard.Observe(Call("read_file", "{\"path\":\"a.txt\",\"offset\":0}"), "x");
        guard.Observe(Call("read_file", "{ \"offset\": 0, \"path\": \"a.txt\" }"), "x");
        Assert.Equal(LoopVerdict.Nudge, guard.Observe(Call("read_file", "{\"offset\":0,\"path\":\"a.txt \"}"), "x"));
    }

    [Fact]
    public void PollingWithChangingResultsGetsMoreRoom()
    {
        var guard = new LoopGuard();
        var verdicts = Enumerable.Range(0, 8).Select(i => guard.Observe(Call("screenshot", "{}"), $"画面 {i}")).ToList();
        Assert.Equal(LoopVerdict.Nudge, verdicts[3]);
        Assert.All(verdicts.Take(3), v => Assert.Equal(LoopVerdict.Ok, v));
        Assert.All(verdicts.Skip(4).Take(3), v => Assert.Equal(LoopVerdict.Ok, v));
        Assert.Equal(LoopVerdict.Stop, verdicts[7]);
    }

    [Fact]
    public void AlternatingCallsAreCaughtButRealProgressIsNot()
    {
        var guard = new LoopGuard();
        var verdicts = new List<LoopVerdict>();
        for (var i = 0; i < 3; i++)
        {
            verdicts.Add(guard.Observe(Call("read_file", "{\"path\":\"a\"}"), "A"));
            verdicts.Add(guard.Observe(Call("read_file", "{\"path\":\"b\"}"), "B"));
        }
        Assert.Contains(LoopVerdict.Nudge, verdicts);

        var progress = new LoopGuard();
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(LoopVerdict.Ok, progress.Observe(Call("read_file", $"{{\"path\":\"file{i}.txt\"}}"), $"内容 {i}"));
            Assert.Equal(LoopVerdict.Ok, progress.Observe(Call("update_plan", "{\"steps\":[]}"), "ok"));
        }
    }

    // ---------- 接进 AgentLoop ----------

    [Fact]
    public async Task SpinningRunIsStoppedEarlyWithAnExplanation()
    {
        var gateway = new Gateway((n, req) => req.Tools is null
            ? new ChatTurn { Content = "一直在列同一个目录，没有新进展，需要你确认要找哪个文件。" }
            : Turn("list_dir", ListDir()));
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("找文件") };

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
            .RunAsync(history, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Stuck, result.StopReason);
        // 5 次重复就停（而不是跑满 100 步），再加 1 次不带工具的总结
        Assert.Equal(6, gateway.Requests.Count);
        Assert.Null(gateway.Requests[^1].Tools);
        Assert.Contains(result.NewMessages, m => m.Role == ChatRole.Tool && m.Content.Contains("【系统提醒】你已经多次用相同的参数调用 list_dir"));
        var last = result.NewMessages[^1];
        Assert.Equal(ChatRole.Assistant, last.Role);
        Assert.Contains("需要你确认", last.Content);
        Assert.Empty(last.ToolCalls);
    }

    [Fact]
    public async Task NudgedModelThatChangesCourseKeepsGoing()
    {
        var gateway = new Gateway((n, req) => n switch
        {
            <= 3 => Turn("list_dir", ListDir()),
            4 => Turn("list_dir", ListDir("sub")),
            _ => new ChatTurn { Content = "找到了" },
        });
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
            .RunAsync(new List<ChatMessage> { ChatMessage.User("找文件") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.Equal("找到了", result.NewMessages[^1].Content);
    }

    [Fact]
    public async Task LongRunGetsCheckpointsAndSummarizesWhenOutOfSteps()
    {
        for (var i = 0; i < 10; i++) Directory.CreateDirectory(Path.Combine(_dir, $"d{i}"));
        var gateway = new Gateway((n, req) => req.Tools is null
            ? new ChatTurn { Content = "已经看完 6 个目录，还差 4 个，回复“继续”接着看。" }
            : Turn("list_dir", ListDir($"d{n}")));

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject(), options: new AgentOptions { MaxSteps = 6, CheckpointInterval = 3 })
            .RunAsync(new List<ChatMessage> { ChatMessage.User("看看每个目录") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.MaxSteps, result.StopReason);
        var tools = result.NewMessages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Equal(6, tools.Count);
        Assert.Contains("已经执行了 3 步", tools[2].Content);
        Assert.Contains("到达上限", tools[5].Content);
        Assert.DoesNotContain(tools, t => t.Content.Contains("相同的参数"));
        Assert.Equal(7, gateway.Requests.Count);
        Assert.Null(gateway.Requests[^1].Tools);
        Assert.Contains("还差 4 个", result.NewMessages[^1].Content);
    }

    [Fact]
    public void DefaultBudgetIsGenerous()
    {
        var options = new AgentOptions();
        Assert.Equal(100, options.MaxSteps);
        Assert.Equal(25, options.CheckpointInterval);
    }
}
