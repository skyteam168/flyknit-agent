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

    /// <summary>在指定的第几次调用上抛错，用来验证模型服务出问题时整轮任务不会报废。</summary>
    private sealed class FailingGateway : IChatGateway
    {
        private readonly Queue<ChatTurn> _turns;
        private readonly HashSet<int> _failAt;
        private readonly string _error;
        public int Calls { get; private set; }
        public List<List<ChatMessage>> Sent { get; } = new();

        public FailingGateway(string error, int[] failAt, params ChatTurn[] turns)
        {
            _error = error;
            _failAt = new HashSet<int>(failAt);
            _turns = new Queue<ChatTurn>(turns);
        }

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Calls++;
            Sent.Add(request.Messages.ToList());
            if (_failAt.Contains(Calls))
            {
                throw new GatewayException(_error);
            }
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

    /// <summary>
    /// 这组用例测的是权限模式本身，所以显式关掉工作区隔离——隔离是 IT 在上面加的一道封顶
    /// （开着时「完全权限」按「工作区内修改」算），它有自己的测试，不该在这里混进来。
    /// </summary>
    private ToolContext Context(PermissionMode permission = PermissionMode.Workspace) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _dir,
        Permission = permission,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.Sandbox] = new() { Value = false, Locked = true },
        }),
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
            Call("run_shell", "{\"command\":\"mytool export --to out.txt\"}", "我将创建 out.txt"),
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
    public async Task ModerationBlockRedactsTheToolOutputAndCarriesOn()
    {
        File.WriteAllText(Path.Combine(_dir, "质检.txt"), new string('甲', 800));
        // 第 2 次调用（读完文件之后那次）被内容审核拦截
        var gateway = new FailingGateway(
            "Output data may contain inappropriate content.",
            new[] { 2 },
            Call("read_file", "{\"path\":\"质检.txt\"}"),
            new ChatTurn { Content = "文件已读完，共 800 个字符。" });
        var observer = new Observer();

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.AllowOnce))
            .RunAsync(new List<ChatMessage> { ChatMessage.User("读一下质检.txt") }, Scenes.Agent, Context(), observer, true, CancellationToken.None);

        // 整轮没有报废，而是省略那段内容后继续跑完
        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        Assert.Equal(3, gateway.Calls);                       // 第 3 次是省略后的重试
        Assert.Contains("文件已读完", result.NewMessages[^1].Content);

        // 重试时送出去的上下文里，大段文件内容已被占位符替换
        var retried = gateway.Sent[2];
        Assert.Contains(retried, m => m.Role == ChatRole.Tool && m.Content == ContentFilter.Placeholder);
        Assert.DoesNotContain(retried, m => m.Content.Contains(new string('甲', 100)));
    }

    [Fact]
    public async Task WhenRedactingDoesNotHelpTheRunStopsWithAnExplanationInsteadOfDying()
    {
        // 每次都被拦截：换完也没用，应该停下来并把原因留在对话里
        var gateway = new FailingGateway("Output data may contain inappropriate content.", new[] { 1, 2, 3, 4 });

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.AllowOnce))
            .RunAsync(new List<ChatMessage> { ChatMessage.User("随便问点什么") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Failed, result.StopReason);
        var answer = Assert.Single(result.NewMessages);      // 失败说明也作为一条回答保存下来
        Assert.Equal(ChatRole.Assistant, answer.Role);
        Assert.Contains("内容审核", answer.Content);
    }

    [Fact]
    public async Task OtherGatewayErrorsAlsoKeepTheWorkDoneSoFar()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "一些内容");
        var gateway = new FailingGateway(
            "HTTP 503 服务暂时不可用",
            new[] { 2 },
            Call("read_file", "{\"path\":\"a.txt\"}"));

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new FixedConfirm(ConfirmChoice.AllowOnce))
            .RunAsync(new List<ChatMessage> { ChatMessage.User("读一下") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Failed, result.StopReason);
        // 读文件那一步的结果没有丢，下次用户接着聊时上下文还在
        Assert.Contains(result.NewMessages, m => m.Role == ChatRole.Tool && m.Content.Contains("一些内容"));
        Assert.Contains("503", result.NewMessages[^1].Content);
    }

    [Fact]
    public async Task WritingInsideTheWorkspaceNoLongerAsks()
    {
        var confirm = new FixedConfirm(ConfirmChoice.AllowOnce);

        await new AgentLoop(new ScriptedGateway(
                Call("run_shell", "{\"command\":\"echo hi > a.txt\"}"),
                Call("run_shell", "{\"command\":\"mkdir out\"}"),
                Call("run_shell", "{\"command\":\"npm run build\"}"),
                new ChatTurn { Content = "ok" }),
                ToolRegistry.CreateDefault(), confirm)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Empty(confirm.Requests); // 以前这三条每条都要点一次
    }

    [Fact]
    public async Task DestructiveCommandsAlwaysAskAndNeverBecomeRules()
    {
        var approvals = new ApprovalStore();
        var confirm = new FixedConfirm(ConfirmChoice.AllowAlways);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "1");

        async Task Run(string command) =>
            await new AgentLoop(new ScriptedGateway(
                    Call("run_shell", "{\"command\":" + Json(command) + "}"), new ChatTurn { Content = "ok" }),
                    ToolRegistry.CreateDefault(), confirm, approvals: approvals)
                .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        await Run("del a.txt");
        await Run("del a.txt");     // 上一次选了「以后自动」也没用，还是要问
        await Run("taskkill /F /IM notepad.exe");

        Assert.Equal(3, confirm.Requests.Count);
        Assert.All(confirm.Requests, r => Assert.False(r.Decision.Rememberable));
        Assert.Empty(approvals.List());
    }

    [Fact]
    public async Task UnknownCommandBecomesAPrefixRule()
    {
        var approvals = new ApprovalStore();
        var confirm = new FixedConfirm(ConfirmChoice.AllowAlways);
        var observer = new Observer();

        async Task Run(string command) =>
            await new AgentLoop(new ScriptedGateway(
                    Call("run_shell", "{\"command\":" + Json(command) + "}"), new ChatTurn { Content = "ok" }),
                    ToolRegistry.CreateDefault(), confirm, approvals: approvals)
                .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), observer, true, CancellationToken.None);

        await Run("mytool report --out x.csv");
        Assert.Single(confirm.Requests);
        Assert.True(confirm.Requests[0].Decision.Rememberable);
        Assert.Single(approvals.List());
        Assert.Equal("mytool report", approvals.List()[0].Prefix);

        // 同前缀、不同参数：不再问
        await Run("mytool report --out y.csv");
        Assert.Single(confirm.Requests);
        Assert.Contains("finish:run_shell:remembered", observer.Events);

        // 换了子命令就不在规则范围内
        await Run("mytool upload --to server");
        Assert.Equal(2, confirm.Requests.Count);
    }

    [Fact]
    public async Task ScriptInterpretersNeverGetARule()
    {
        var approvals = new ApprovalStore();
        var confirm = new FixedConfirm(ConfirmChoice.AllowAlways);

        async Task Run(string command) =>
            await new AgentLoop(new ScriptedGateway(
                    Call("run_shell", "{\"command\":" + Json(command) + "}"), new ChatTurn { Content = "ok" }),
                    ToolRegistry.CreateDefault(), confirm, approvals: approvals)
                .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        // 同一句 python build.py，脚本内容随时可能变，所以永远不给规则
        await Run("python build.py");
        await Run("python build.py");

        Assert.Equal(2, confirm.Requests.Count);
        Assert.Empty(approvals.List());
    }

    [Fact]
    public async Task DeletingAFileStillAsksEveryTime()
    {
        var approvals = new ApprovalStore();
        var confirm = new FixedConfirm(ConfirmChoice.AllowAlways);
        File.WriteAllText(Path.Combine(_dir, "1.txt"), "1");

        await new AgentLoop(new ScriptedGateway(
                Call("delete_path", "{\"path\":\"1.txt\"}"),
                Call("delete_path", "{\"path\":\"1.txt\"}"),
                new ChatTurn { Content = "ok" }),
                ToolRegistry.CreateDefault(), confirm, approvals: approvals)
            .RunAsync(new List<ChatMessage> { ChatMessage.User("go") }, Scenes.Agent, Context(), new Observer(), true, CancellationToken.None);

        Assert.Equal(2, confirm.Requests.Count);
        Assert.False(confirm.Requests[0].Decision.Rememberable);
        Assert.Empty(approvals.List());
    }

    [Fact]
    public void ApprovalRulesPersistToFile()
    {
        var file = Path.Combine(_dir, "approval-rules.json");
        var candidate = new ApprovalCandidate("run_shell", "powershell", "npm run build", "npm run", _dir, "npm run *");
        new ApprovalStore(file).Add(candidate);

        var reloaded = new ApprovalStore(file);
        Assert.True(reloaded.IsAllowed(candidate with { Command = "npm run test" }));
        Assert.False(reloaded.IsAllowed(candidate with { Command = "npm runaway" }));      // 必须落在词边界上
        Assert.False(reloaded.IsAllowed(candidate with { Command = "npm uninstall x" }));
        Assert.False(reloaded.IsAllowed(candidate with { Scope = "D:\\别的工作区" }));      // 换个工作区不生效

        reloaded.Revoke(reloaded.List()[0].Id);
        Assert.False(new ApprovalStore(file).IsAllowed(candidate));
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
