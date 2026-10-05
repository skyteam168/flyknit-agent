using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

public class AgentLoopTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-agent").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>按顺序返回预设回合的假模型。</summary>
    private sealed class ScriptedGateway : IChatGateway
    {
        private readonly Queue<ChatTurn> _turns;
        public List<int> MessageCounts { get; } = new();
        public List<int?> ModelIds { get; } = new();

        public ScriptedGateway(params ChatTurn[] turns) => _turns = new Queue<ChatTurn>(turns);

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            MessageCounts.Add(request.Messages.Count);
            ModelIds.Add(request.ModelId);
            var turn = _turns.Count > 0 ? _turns.Dequeue() : new ChatTurn { Content = "完成" };
            if (turn.Content.Length > 0)
            {
                sink?.OnContent(turn.Content);
            }
            return Task.FromResult(turn);
        }
    }

    private sealed class FixedConfirm : IConfirmationHandler
    {
        private readonly ConfirmChoice _choice;
        public List<ConfirmRequest> Requests { get; } = new();
        public FixedConfirm(ConfirmChoice choice) => _choice = choice;

        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_choice);
        }
    }

    private sealed class Audit : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = new();
        public void Record(AuditEntry entry) => Entries.Add(entry);
    }

    private sealed class Observer : IAgentObserver
    {
        public List<string> Events { get; } = new();
        public List<IReadOnlyList<PlanItem>> Plans { get; } = new();
        public void OnContent(string delta) => Events.Add("content");
        public void OnReasoning(string delta) => Events.Add("reasoning");
        public void OnAssistantMessage(ChatMessage message) => Events.Add("assistant");
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) => Events.Add($"start:{call.Name}:{decision.Level}");
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) => Events.Add($"finish:{call.Name}:{decision}");
        public void OnToolMessage(ChatMessage message) => Events.Add("tool");
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) => Plans.Add(plan);
    }

    private ToolContext Context() => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        WorkingDirectory = _dir,
    };

    private static ChatTurn Call(string name, string args, string content = "") => new()
    {
        Content = content,
        ToolCalls = new[] { new ToolCall("call_" + Guid.NewGuid().ToString("N")[..6], name, args) },
    };

    private static string Json(string path) => System.Text.Json.JsonSerializer.Serialize(path);

    [Fact]
    public async Task ReadsFileAutomaticallyAndFinishes()
    {
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "hello");
        var gateway = new ScriptedGateway(Call("read_file", $"{{\"path\":{Json(file)}}}"), new ChatTurn { Content = "文件内容是 hello" });
        var confirm = new FixedConfirm(ConfirmChoice.Reject);
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("读一下 a.txt") };

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm)
            .RunAsync(history, Scenes.Agent, Context(), new Observer(), useTools: true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.Empty(confirm.Requests);
        Assert.Equal(3, result.NewMessages.Count); // assistant(tool call) + tool + assistant
        Assert.Contains("hello", result.NewMessages[1].Content);
        Assert.Equal(new[] { 2, 4 }, gateway.MessageCounts);
    }

    [Fact]
    public async Task WriteNeedsConfirmationAndRejectionIsRespected()
    {
        var target = Path.Combine(_dir, "out.txt");
        var gateway = new ScriptedGateway(
            Call("write_file", $"{{\"path\":{Json(target)},\"content\":\"x\"}}", "我将创建 out.txt"),
            new ChatTurn { Content = "好的，未创建" });
        var confirm = new FixedConfirm(ConfirmChoice.Reject);
        var audit = new Audit();
        var observer = new Observer();

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm, audit)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("写文件") }, Scenes.Agent, Context(), observer, true, CancellationToken.None);

        var request = Assert.Single(confirm.Requests);
        Assert.Equal("我将创建 out.txt", request.Rationale);
        Assert.False(File.Exists(target));
        Assert.Equal("rejected", Assert.Single(audit.Entries).Decision);
        Assert.Contains("finish:write_file:rejected", observer.Events);
    }

    [Fact]
    public async Task ApprovedWriteIsExecuted()
    {
        var target = Path.Combine(_dir, "sub", "out.txt");
        var gateway = new ScriptedGateway(Call("write_file", $"{{\"path\":{Json(target)},\"content\":\"Xin chào\"}}"), new ChatTurn { Content = "已完成" });
        var audit = new Audit();

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.AllowOnce), audit)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("写文件") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal("Xin chào", File.ReadAllText(target));
        Assert.Equal("approved", audit.Entries[0].Decision);
        Assert.Equal("ok", audit.Entries[0].Status);
    }

    [Fact]
    public async Task DangerousCommandIsBlockedWithoutAskingUser()
    {
        var gateway = new ScriptedGateway(Call("run_shell", "{\"command\":\"rm -rf /\"}"), new ChatTurn { Content = "该命令被阻止" });
        var confirm = new FixedConfirm(ConfirmChoice.AllowOnce);
        var audit = new Audit();

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm, audit)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("清理") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Empty(confirm.Requests);
        Assert.Equal("blocked", audit.Entries[0].Decision);
        Assert.Contains("已被安全策略阻止", result.NewMessages[1].Content);
    }

    [Fact]
    public async Task AllowForConversationSkipsLaterConfirmationsExceptShell()
    {
        var gateway = new ScriptedGateway(
            Call("write_file", $"{{\"path\":{Json(Path.Combine(_dir, "1.txt"))},\"content\":\"1\"}}"),
            Call("write_file", $"{{\"path\":{Json(Path.Combine(_dir, "2.txt"))},\"content\":\"2\"}}"),
            Call("run_shell", "{\"command\":\"echo hi\"}"),
            Call("run_shell", "{\"command\":\"echo again\"}"),
            new ChatTurn { Content = "完成" });
        var confirm = new FixedConfirm(ConfirmChoice.AllowForConversation);

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        // write_file 只问一次；run_shell 每次都问
        Assert.Equal(new[] { "write_file", "run_shell", "run_shell" }, confirm.Requests.Select(r => r.Call.Name));
    }

    [Fact]
    public async Task UnknownToolAndBadJsonAreReportedToModel()
    {
        var gateway = new ScriptedGateway(
            Call("no_such_tool", "{}"),
            Call("read_file", "{not json"),
            new ChatTurn { Content = "抱歉" });

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.Reject))
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.Contains("不存在名为 no_such_tool", result.NewMessages[1].Content);
        Assert.Contains("不是合法的 JSON", result.NewMessages[3].Content);
    }

    [Fact]
    public async Task SelectedModelIsSentOnEveryTurn()
    {
        var gateway = new ScriptedGateway(Call("list_dir", $"{{\"path\":{Json(_dir)}}}"), new ChatTurn { Content = "ok" });
        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.Reject))
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None, modelId: 7);
        Assert.Equal(new int?[] { 7, 7 }, gateway.ModelIds);
    }

    [Fact]
    public async Task StopsAfterMaxSteps()
    {
        var turns = Enumerable.Range(0, 10).Select(_ => Call("list_dir", $"{{\"path\":{Json(_dir)}}}")).ToArray();
        var result = await new AgentLoop(new ScriptedGateway(turns), ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.Reject), options: new AgentOptions { MaxSteps = 3 })
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);
        Assert.Equal(AgentStopReason.MaxSteps, result.StopReason);
    }

    [Fact]
    public async Task StopsAfterRepeatedFailures()
    {
        var missing = Path.Combine(_dir, "missing.txt");
        var turns = Enumerable.Range(0, 10).Select(_ => Call("read_file", $"{{\"path\":{Json(missing)}}}")).ToArray();
        var result = await new AgentLoop(new ScriptedGateway(turns), ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.Reject), options: new AgentOptions { MaxConsecutiveFailures = 2 })
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);
        Assert.Equal(AgentStopReason.TooManyFailures, result.StopReason);
    }

    [Fact]
    public async Task PlanUpdatesAreForwarded()
    {
        var gateway = new ScriptedGateway(
            Call("update_plan", "{\"steps\":[{\"step\":\"读取数据\",\"status\":\"completed\"},{\"step\":\"生成报告\",\"status\":\"in_progress\"}]}"),
            new ChatTurn { Content = "ok" });
        var observer = new Observer();

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.Reject))
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, Context(), observer, true, CancellationToken.None);

        var plan = Assert.Single(observer.Plans);
        Assert.Equal(2, plan.Count);
        Assert.Equal("in_progress", plan[1].Status);
    }

    [Fact]
    public async Task CancellationStillAnswersPendingToolCalls()
    {
        using var cts = new CancellationTokenSource();
        var gateway = new ScriptedGateway(new ChatTurn
        {
            ToolCalls = new[]
            {
                new ToolCall("a", "list_dir", $"{{\"path\":{Json(_dir)}}}"),
                new ToolCall("b", "list_dir", $"{{\"path\":{Json(_dir)}}}"),
            },
        });
        var observer = new CancelOnFirstTool(cts);

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.Reject))
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, Context(), observer, true, cts.Token);

        Assert.Equal(AgentStopReason.Cancelled, result.StopReason);
        // 两个工具调用都有对应的 tool 消息，保证下次继续对话时上下文合法
        Assert.Equal(2, result.NewMessages.Count(m => m.Role == ChatRole.Tool));
    }

    private sealed class CancelOnFirstTool : IAgentObserver
    {
        private readonly CancellationTokenSource _cts;
        public CancelOnFirstTool(CancellationTokenSource cts) => _cts = cts;
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) => _cts.Cancel();
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }
}
