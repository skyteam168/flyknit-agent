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

    private ToolContext Context(PermissionMode permission = PermissionMode.Workspace) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _dir,
        Permission = permission,
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
    public async Task CommandNeedsConfirmationAndRejectionIsRespected()
    {
        var gateway = new ScriptedGateway(
            Call("run_shell", "{\"command\":\"echo hi > out.txt\"}", "我将创建 out.txt"),
            new ChatTurn { Content = "好的，未执行" });
        var confirm = new FixedConfirm(ConfirmChoice.Reject);
        var audit = new Audit();
        var observer = new Observer();

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm, audit)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("写文件") }, Scenes.Agent, Context(), observer, true, CancellationToken.None);

        var request = Assert.Single(confirm.Requests);
        Assert.Equal("我将创建 out.txt", request.Rationale);
        Assert.False(File.Exists(Path.Combine(_dir, "out.txt")));
        Assert.Equal("rejected", Assert.Single(audit.Entries).Decision);
        Assert.Contains("finish:run_shell:rejected", observer.Events);
    }

    [Fact]
    public async Task WriteInsideWorkspaceRunsWithoutConfirmation()
    {
        var target = Path.Combine(_dir, "sub", "out.txt");
        var gateway = new ScriptedGateway(
            Call("write_file", "{\"path\":\"sub/out.txt\",\"content\":\"Xin chào\"}"),
            new ChatTurn { Content = "已完成" });
        var confirm = new FixedConfirm(ConfirmChoice.Reject);
        var audit = new Audit();

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm, audit)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("写文件") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Empty(confirm.Requests);
        Assert.Equal("Xin chào", File.ReadAllText(target)); // 相对路径写到工作区
        Assert.Equal("auto", audit.Entries[0].Decision);
    }

    [Fact]
    public async Task WriteOutsideWorkspaceIsBlocked()
    {
        var outside = Directory.CreateTempSubdirectory("flyknit-outside").FullName;
        try
        {
            var target = Path.Combine(outside, "x.txt");
            var gateway = new ScriptedGateway(Call("write_file", $"{{\"path\":{Json(target)},\"content\":\"x\"}}"), new ChatTurn { Content = "无法写入" });
            var confirm = new FixedConfirm(ConfirmChoice.AllowOnce);

            var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm)
                .RunAsync(new List<ChatMessage> { ChatMessage.User("写") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

            Assert.Empty(confirm.Requests);
            Assert.False(File.Exists(target));
            Assert.Contains("工作区内修改", result.NewMessages[1].Content);

            // 完全权限下可以写工作区外的文件，且无需确认
            var full = new ScriptedGateway(Call("write_file", $"{{\"path\":{Json(target)},\"content\":\"x\"}}"), new ChatTurn { Content = "ok" });
            await new AgentLoop(full, ToolRegistry.CreateDefault(), confirm)
                .RunAsync(new List<ChatMessage> { ChatMessage.User("写") }, Scenes.Agent, Context(PermissionMode.Full), new Observer(), true, CancellationToken.None);
            Assert.Empty(confirm.Requests);
            Assert.True(File.Exists(target));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task ReadOnlyBlocksWritesAndCommandsButAllowsReads()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hello");
        var gateway = new ScriptedGateway(
            Call("read_file", "{\"path\":\"a.txt\"}"),
            Call("write_file", "{\"path\":\"b.txt\",\"content\":\"x\"}"),
            Call("run_shell", "{\"command\":\"echo hi > c.txt\"}"),
            new ChatTurn { Content = "ok" });
        var confirm = new FixedConfirm(ConfirmChoice.AllowOnce);
        var audit = new Audit();

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm, audit)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, Context(PermissionMode.ReadOnly), new Observer(), true, CancellationToken.None);

        Assert.Empty(confirm.Requests);
        Assert.Equal(new[] { "auto", "blocked", "blocked" }, audit.Entries.Select(e => e.Decision));
        Assert.False(File.Exists(Path.Combine(_dir, "b.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "c.txt")));
    }

    [Fact]
    public async Task FullPermissionRunsCommandsWithoutAskingButStillBlocksDangerous()
    {
        var gateway = new ScriptedGateway(
            Call("run_shell", "{\"command\":\"echo hi > c.txt\"}"),
            Call("run_shell", "{\"command\":\"rm -rf /\"}"),
            new ChatTurn { Content = "ok" });
        var confirm = new FixedConfirm(ConfirmChoice.AllowOnce);
        var audit = new Audit();

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm, audit)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("x") }, Scenes.Agent, Context(PermissionMode.Full), new Observer(), true, CancellationToken.None);

        Assert.Empty(confirm.Requests);
        Assert.Equal(new[] { "auto", "blocked" }, audit.Entries.Select(e => e.Decision));
        Assert.True(File.Exists(Path.Combine(_dir, "c.txt")));
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
    public async Task ApprovedCommandIsRememberedAcrossRuns()
    {
        var approvals = new ApprovalStore();
        var confirm = new FixedConfirm(ConfirmChoice.AllowAlways);
        var observer = new Observer();

        async Task Run(params ChatTurn[] turns) =>
            await new AgentLoop(new ScriptedGateway(turns), ToolRegistry.CreateDefault(), confirm, approvals: approvals)
                .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), observer, true, CancellationToken.None);

        await Run(Call("run_shell", "{\"command\":\"echo hi > a.txt\"}"), new ChatTurn { Content = "ok" });
        // 新的一次运行（相当于新对话），同样的命令（多余空格、大小写不同）不再询问
        await Run(Call("run_shell", "{\"command\":\"echo  HI > a.txt\"}"), new ChatTurn { Content = "ok" });
        // 不同的命令仍要确认
        await Run(Call("run_shell", "{\"command\":\"echo bye > a.txt\"}"), new ChatTurn { Content = "ok" });

        Assert.Equal(2, confirm.Requests.Count);
        Assert.Contains("finish:run_shell:remembered", observer.Events);
        Assert.Equal(2, approvals.List().Count);
    }

    [Fact]
    public async Task AllowOnceIsNotRememberedAndDeletesAlwaysAsk()
    {
        var approvals = new ApprovalStore();
        var confirm = new FixedConfirm(ConfirmChoice.AllowAlways);
        File.WriteAllText(Path.Combine(_dir, "1.txt"), "1");
        File.WriteAllText(Path.Combine(_dir, "2.txt"), "2");
        var gateway = new ScriptedGateway(
            Call("delete_path", "{\"path\":\"1.txt\"}"),
            Call("delete_path", "{\"path\":\"1.txt\"}"),
            new ChatTurn { Content = "ok" });

        await new AgentLoop(gateway, ToolRegistry.CreateDefault(), confirm, approvals: approvals)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(2, confirm.Requests.Count); // 删除不能被记住
        Assert.False(confirm.Requests[0].Decision.Rememberable);
        Assert.Empty(approvals.List());

        var once = new FixedConfirm(ConfirmChoice.AllowOnce);
        for (var i = 0; i < 2; i++)
        {
            await new AgentLoop(new ScriptedGateway(Call("run_shell", "{\"command\":\"echo x > b.txt\"}"), new ChatTurn { Content = "ok" }), ToolRegistry.CreateDefault(), once, approvals: approvals)
                .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);
        }
        Assert.Equal(2, once.Requests.Count);
    }

    [Fact]
    public void ApprovalStorePersistsToFile()
    {
        var file = Path.Combine(_dir, "approvals.json");
        var args = System.Text.Json.JsonDocument.Parse("{\"command\":\"npm install\"}").RootElement;
        var key = ApprovalStore.KeyFor("run_shell", args);
        new ApprovalStore(file).Approve(key, "run_shell", "npm install");

        var reloaded = new ApprovalStore(file);
        Assert.True(reloaded.IsApproved(key));
        reloaded.Revoke(key);
        Assert.False(new ApprovalStore(file).IsApproved(key));
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
