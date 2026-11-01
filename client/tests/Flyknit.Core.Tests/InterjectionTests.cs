using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 运行中插话：用户在 AI 干活的半路补充的话，下一次调模型时就接进上下文（在工具结果之后），
/// 模型准备收尾时还有没读的插话就不结束、接着按新要求做。
/// </summary>
public class InterjectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-interject").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Gateway : IChatGateway
    {
        private readonly Func<int, ChatRequest, ChatTurn> _reply;
        public List<List<ChatMessage>> Seen { get; } = new();
        public Gateway(Func<int, ChatRequest, ChatTurn> reply) => _reply = reply;

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Seen.Add(request.Messages.ToList());
            return Task.FromResult(_reply(Seen.Count, request));
        }
    }

    private sealed class Reject : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.Reject);
    }

    private sealed class Observer : IAgentObserver
    {
        public List<string> Interjections { get; } = new();
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
        public void OnInterjection(ChatMessage message) => Interjections.Add(message.Content);
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

    private ChatTurn ListDir() => new()
    {
        ToolCalls = new[] { new ToolCall("call_" + Guid.NewGuid().ToString("N")[..6], "list_dir", $"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(_dir)}}}") },
    };

    [Fact]
    public async Task AnInterjectionReachesTheModelAtTheNextStepAfterTheToolResult()
    {
        var inbox = new Queue<ChatMessage>();
        var gateway = new Gateway((n, _) =>
        {
            if (n == 1)
            {
                inbox.Enqueue(ChatMessage.User("顺便把文件按日期排序")); // 用户在第一步执行时补充
                return ListDir();
            }
            return new ChatTurn { Content = "好的，已按日期排序" };
        });
        var observer = new Observer();
        var loop = new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
        {
            Interjections = () => { var list = inbox.ToList(); inbox.Clear(); return list; },
        };

        var result = await loop.RunAsync(new List<ChatMessage> { ChatMessage.User("列一下文件") }, Scenes.Agent, Context(), observer, true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        var second = gateway.Seen[1];
        Assert.Equal(ChatRole.Tool, second[^2].Role);                     // 工具结果在前
        Assert.Equal("顺便把文件按日期排序", second[^1].Content);         // 插话紧跟其后
        Assert.Equal(ChatRole.User, second[^1].Role);
        Assert.Equal(new[] { "顺便把文件按日期排序" }, observer.Interjections);
        Assert.Contains(result.NewMessages, m => m.Role == ChatRole.User && m.Content == "顺便把文件按日期排序");
        Assert.Contains(result.Trace.Steps, s => s.Kind == "interject");
    }

    [Fact]
    public async Task PausingHoldsTheRunBeforeTheNextStepUntilResumed()
    {
        var gate = new TaskCompletionSource();
        var paused = true;
        var gateway = new Gateway((n, _) => n == 1 ? ListDir() : new ChatTurn { Content = "列好了" });
        var loop = new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
        {
            Pause = ct => paused ? gate.Task.WaitAsync(ct).ContinueWith(_ => { }, TaskScheduler.Default) : Task.CompletedTask,
        };

        var run = loop.RunAsync(new List<ChatMessage> { ChatMessage.User("列一下文件") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);
        await Task.Delay(150);
        Assert.False(run.IsCompleted);
        Assert.Empty(gateway.Seen);          // 暂停中：模型一次都没调

        paused = false;
        gate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.Equal(2, gateway.Seen.Count);
    }

    [Fact]
    public async Task StoppingWhilePausedEndsTheRunCleanly()
    {
        using var cts = new CancellationTokenSource();
        var gateway = new Gateway((_, _) => ListDir());
        var calls = 0;
        var loop = new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
        {
            // 第一步放行；之后（工具执行前）一直暂停，直到被停止
            Pause = ct => ++calls == 1 ? Task.CompletedTask : Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default),
        };

        var run = loop.RunAsync(new List<ChatMessage> { ChatMessage.User("列一下文件") }, Scenes.Agent, Context(), new Observer(), true, cts.Token);
        await Task.Delay(150);
        Assert.False(run.IsCompleted);
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentStopReason.Cancelled, result.StopReason);
        // 发起了的工具调用有结果（没执行），上下文配得上
        var tool = Assert.Single(result.NewMessages, m => m.Role == ChatRole.Tool);
        Assert.Contains("停止", tool.Content);
        Assert.Single(gateway.Seen);
    }

    [Fact]
    public async Task AnInterjectionJustBeforeTheAnswerKeepsTheRunGoing()
    {
        var inbox = new Queue<ChatMessage>();
        var gateway = new Gateway((n, _) =>
        {
            if (n == 1)
            {
                inbox.Enqueue(ChatMessage.User("再加一列合计"));          // 模型正准备收尾
                return new ChatTurn { Content = "表格做好了" };
            }
            return new ChatTurn { Content = "已加上合计列" };
        });
        var loop = new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
        {
            Interjections = () => { var list = inbox.ToList(); inbox.Clear(); return list; },
        };

        var result = await loop.RunAsync(new List<ChatMessage> { ChatMessage.User("做个表") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(2, gateway.Seen.Count);
        Assert.Equal(new[] { "表格做好了", "再加一列合计", "已加上合计列" }, result.NewMessages.Select(m => m.Content));
        Assert.Equal(AgentStopReason.Completed, result.StopReason);
    }

    [Fact]
    public async Task WithoutInterjectionsNothingChanges()
    {
        var gateway = new Gateway((_, _) => new ChatTurn { Content = "好" });
        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
            .RunAsync(new List<ChatMessage> { ChatMessage.User("你好") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);
        Assert.Single(gateway.Seen);
        Assert.Single(result.NewMessages);
    }
}

/// <summary>待发列表：插话下一步取走，排队这一轮结束后一条条发；没读到的插话不丢，能改能撤回。</summary>
public class RunInboxTests
{
    [Fact]
    public void SteerIsTakenEachStepAndQueueWaitsForTheEnd()
    {
        var inbox = new RunInbox();
        inbox.Add("c", "先排一条", RunInbox.Queue);
        inbox.Add("c", "插一句", RunInbox.Steer);

        Assert.Equal(new[] { "插一句" }, inbox.TakeSteer("c").Select(p => p.Text));
        Assert.Empty(inbox.TakeSteer("c"));
        Assert.Equal("先排一条", inbox.TakeNext("c")!.Text);
        Assert.Null(inbox.TakeNext("c"));
    }

    [Fact]
    public void UnreadInterjectionsGoFirstAsOneMessage()
    {
        var inbox = new RunInbox();
        inbox.Add("c", "排队的", RunInbox.Queue);
        inbox.Add("c", "补充一", RunInbox.Steer);
        inbox.Add("c", "补充二", RunInbox.Steer);

        Assert.Equal("补充一\n\n补充二", inbox.TakeNext("c")!.Text);
        Assert.Equal("排队的", inbox.TakeNext("c")!.Text);
    }

    [Fact]
    public void PendingInputCanBeEditedOrWithdrawnUntilTaken()
    {
        var inbox = new RunInbox();
        var changes = 0;
        inbox.Changed += _ => changes++;
        var a = inbox.Add("c", "原来的", RunInbox.Queue);
        var b = inbox.Add("c", "打断优先", RunInbox.Queue, front: true);

        Assert.Equal(new[] { "打断优先", "原来的" }, inbox.List("c").Select(p => p.Text));
        Assert.True(inbox.Update("c", a.Id, "改过的"));
        Assert.True(inbox.Remove("c", b.Id));
        Assert.Equal(new[] { "改过的" }, inbox.List("c").Select(p => p.Text));
        Assert.Equal("改过的", inbox.TakeNext("c")!.Text);
        Assert.False(inbox.Update("c", a.Id, "太晚了"));
        Assert.False(inbox.Remove("c", a.Id));
        Assert.Empty(inbox.List("other"));
        Assert.True(changes >= 5);
    }
}
