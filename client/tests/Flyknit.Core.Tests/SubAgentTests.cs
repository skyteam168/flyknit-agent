using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Agent.SubAgents;
using Flyknit.Core.Chat;
using Flyknit.Core.Experts.Delegation;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>fork 子代理（spawn_agent）：继承上下文、只读限制、结果交回、并行、上限。</summary>
public sealed class SubAgentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-sub").FullName;

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

    /// <summary>按请求内容回话：子代理的请求里有任务说明，主助手的没有。</summary>
    private sealed class Gateway : IChatGateway
    {
        public ConcurrentQueue<ChatRequest> Requests { get; } = new();
        public Queue<ChatTurn> Parent { get; } = new();
        public Func<ChatRequest, int, ChatTurn> Child { get; set; } = (_, _) => new ChatTurn { Content = "子代理的结论：一切正常" };
        public TimeSpan ChildDelay { get; set; } = TimeSpan.Zero;
        private int _childCalls;
        private int _inFlight;

        /// <summary>同时在等模型回复的子代理最多有几个（判断有没有并行，不看总耗时：CI 机器慢时耗时不可靠）。</summary>
        public int MaxChildrenInFlight;

        public static bool IsChild(ChatRequest r) => r.Messages.Any(m => m.Role == ChatRole.User && m.Content.Contains("<子代理任务"));

        public async Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Requests.Enqueue(request);
            if (IsChild(request))
            {
                var now = Interlocked.Increment(ref _inFlight);
                int seen;
                while ((seen = Volatile.Read(ref MaxChildrenInFlight)) < now
                    && Interlocked.CompareExchange(ref MaxChildrenInFlight, now, seen) != seen)
                {
                }
                try
                {
                    if (ChildDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(ChildDelay, ct);
                    }
                    return Child(request, Interlocked.Increment(ref _childCalls));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
            lock (Parent)
            {
                return Parent.Count > 0 ? Parent.Dequeue() : new ChatTurn { Content = "完成" };
            }
        }
    }

    private sealed class Confirm : IConfirmationHandler
    {
        public ConcurrentQueue<ConfirmRequest> Asked { get; } = new();
        public ConfirmChoice Choice { get; set; } = ConfirmChoice.AllowOnce;

        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Asked.Enqueue(request);
            return Task.FromResult(Choice);
        }
    }

    private sealed class Events : IDelegationEvents
    {
        public ConcurrentQueue<string> Seen { get; } = new();
        public void Started(SubtaskRecord record, int seq) => Seen.Enqueue("started:" + record.ExpertName);
        public void Step(SubtaskRecord record, int seq, string kind, string name, string summary, bool? ok) => Seen.Enqueue($"step:{kind}:{name}");
        public void Finished(SubtaskRecord record, int seq) => Seen.Enqueue("finished:" + record.Status);
    }

    private sealed class Store : ISubtaskStore
    {
        public ConcurrentDictionary<string, SubtaskRecord> Records { get; } = new();
        public void SaveSubtask(SubtaskRecord record) => Records[record.Id] = record;
        public SubtaskRecord? GetSubtask(string id) => Records.TryGetValue(id, out var r) ? r : null;
        public IReadOnlyList<SubtaskRecord> SubtasksOf(string conversationId) => Records.Values.Where(r => r.ConversationId == conversationId).ToList();
        public IReadOnlyList<SubtaskRecord> MarkInterruptedSubtasks() => Array.Empty<SubtaskRecord>();
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

    private sealed class NoAnswer : IQuestionHandler
    {
        public Task<IReadOnlyList<AskAnswer>?> AskAsync(AskRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null);
    }

    private readonly Gateway _gateway = new();
    private readonly Confirm _confirm = new();
    private readonly Events _events = new();
    private readonly Store _store = new();

    private ToolContext Ctx(PermissionMode mode = PermissionMode.Workspace) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _dir,
        Permission = mode,
        RunId = "run1",
        Security = new SecuritySettings(new Dictionary<string, SecurityItem> { [SecuritySettings.Sandbox] = new() { Value = false, Locked = true } }),
    };

    private (ToolRegistry Tools, SubAgentRunner Runner) Setup(bool forceReadOnly = false, SubAgentLimits? limits = null)
    {
        var runner = new SubAgentRunner(_gateway, _confirm, () => new AgentOptions(), null, null, _store, _events, limits);
        var tools = ToolRegistry.CreateDefault();
        tools.Add(new AskUserTool(new NoAnswer()));
        tools.Add(new SpawnAgentTool(runner, () => tools, Scenes.Agent, () => null, null, forceReadOnly));
        return (tools, runner);
    }

    private static ChatTurn Spawn(params object[] args) => new()
    {
        ToolCalls = args.Select((a, i) => new ToolCall($"call_{i}", SpawnAgentTool.ToolName, JsonSerializer.Serialize(a))).ToArray(),
    };

    private async Task<AgentRunResult> Run(ToolRegistry tools, ToolContext? ctx = null) =>
        await new AgentLoop(_gateway, tools, _confirm).RunAsync(
            new List<ChatMessage> { ChatMessage.System("系统提示词 ABC"), ChatMessage.User("帮我看看登录流程有没有问题") },
            Scenes.Agent, ctx ?? Ctx(), new Observer(), true, CancellationToken.None);

    private List<ChatRequest> ChildRequests => _gateway.Requests.Where(Gateway.IsChild).ToList();

    [Fact]
    public async Task ForkInheritsTheConversationAndReturnsTheFinalAnswer()
    {
        var (tools, _) = Setup();
        _gateway.Parent.Enqueue(Spawn(new { description = "梳理登录流程", prompt = "读一遍 src/auth 下的代码，说清楚登录的调用链" }));
        _gateway.Parent.Enqueue(new ChatTurn { Content = "根据子代理的结果：登录流程正常" });

        var result = await Run(tools);

        var child = ChildRequests.First();
        Assert.Equal("系统提示词 ABC", child.Messages[0].Content);                       // 和主助手同一个系统提示词（缓存能命中）
        Assert.Contains(child.Messages, m => m.Content == "帮我看看登录流程有没有问题");  // 知道员工要什么
        Assert.DoesNotContain(child.Messages, m => m.ToolCalls.Any(c => c.Name == SpawnAgentTool.ToolName)); // 不含没配齐结果的调用
        var toolMessage = result.NewMessages.Single(m => m.Role == ChatRole.Tool);
        Assert.Contains("子代理的结论：一切正常", toolMessage.Content);
        Assert.Contains("梳理登录流程", toolMessage.Content);
        var record = Assert.Single(_store.Records.Values);
        Assert.Equal("completed", record.Status);
        Assert.Equal("call_0", record.CallId); // 界面卡片靠它对上
        Assert.Contains("started:梳理登录流程", _events.Seen);
        Assert.Contains("finished:completed", _events.Seen);
    }

    [Fact]
    public async Task FreshOnlySeesThePrompt()
    {
        var (tools, _) = Setup();
        _gateway.Parent.Enqueue(Spawn(new { description = "查资料", prompt = "查一下 FastAPI 的依赖注入怎么写", context = "fresh" }));

        await Run(tools);

        var child = ChildRequests.First();
        Assert.Equal(2, child.Messages.Count); // 系统提示词 + 任务说明
        Assert.DoesNotContain(child.Messages, m => m.Content == "帮我看看登录流程有没有问题");
    }

    [Fact]
    public async Task ReadOnlySubAgentGetsNoWriteToolsAndNoBannedTools()
    {
        var (tools, _) = Setup();
        _gateway.Parent.Enqueue(Spawn(new { description = "调研", prompt = "看看工作区里有哪些文件，列出来" }));

        await Run(tools);

        var names = ChildRequests.First().Tools!.Select(t => t!["function"]!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("read_file", names);
        Assert.Contains("run_shell", names); // 能跑只读命令（权限压到「仅可查看」）
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("delete_path", names);
        Assert.DoesNotContain(SpawnAgentTool.ToolName, names); // 只开一层
        Assert.DoesNotContain(AskUserTool.ToolName, names);    // 没人回答
        Assert.DoesNotContain("memory_write", names);
    }

    [Fact]
    public async Task ReadOnlySubAgentCannotModifyEvenViaShell()
    {
        var (tools, _) = Setup();
        var target = Path.Combine(_dir, "x.txt");
        _gateway.Parent.Enqueue(Spawn(new { description = "调研", prompt = "看看工作区，顺便建个文件 x.txt 记一下" }));
        _gateway.Child = (r, n) => n == 1
            ? new ChatTurn { ToolCalls = new[] { new ToolCall("s1", "run_shell", JsonSerializer.Serialize(new { command = $"mkdir out" })) } }
            : new ChatTurn { Content = "没能建文件：只读" };

        await Run(tools);

        Assert.Empty(_confirm.Asked); // 只读子代理不弹确认
        Assert.False(Directory.Exists(Path.Combine(_dir, "out")));
        var toolResult = ChildRequests.Last().Messages.Last(m => m.Role == ChatRole.Tool);
        Assert.False(File.Exists(target));
        Assert.Contains("已被安全策略阻止", toolResult.Content); // 结果说明了为什么没执行
    }

    [Fact]
    public async Task WritableSubAgentAsksTheEmployeeWithItsName()
    {
        var (tools, _) = Setup();
        var file = Path.Combine(_dir, "notes.txt");
        _gateway.Parent.Enqueue(Spawn(new { description = "写说明", prompt = "在工作区写一个 notes.txt，内容是 hello", read_only = false }));
        _gateway.Child = (r, n) => n == 1
            ? new ChatTurn { ToolCalls = new[] { new ToolCall("w1", "write_file", JsonSerializer.Serialize(new { path = file, content = "hello" })) } }
            : new ChatTurn { Content = $"已写好 {file}" };
        var outside = Path.Combine(Path.GetTempPath(), "flyknit-outside-" + Guid.NewGuid().ToString("N") + ".txt");

        await Run(tools);

        Assert.True(File.Exists(file));
        var record = Assert.Single(_store.Records.Values);
        Assert.Equal("completed", record.Status);
        Assert.Contains(file, record.Artifacts);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task ForcedReadOnlyInPlanMode()
    {
        var (tools, _) = Setup(forceReadOnly: true);
        _gateway.Parent.Enqueue(Spawn(new { description = "写", prompt = "在工作区写一个 notes.txt，内容是 hello", read_only = false }));

        await Run(tools);

        var names = ChildRequests.First().Tools!.Select(t => t!["function"]!["name"]!.GetValue<string>()).ToHashSet();
        Assert.DoesNotContain("write_file", names);
    }

    [Fact]
    public async Task SeveralReadOnlySubAgentsRunInParallel()
    {
        var (tools, _) = Setup();
        _gateway.ChildDelay = TimeSpan.FromMilliseconds(400);
        _gateway.Parent.Enqueue(Spawn(
            new { description = "A", prompt = "调研 A 模块的结构和入口" },
            new { description = "B", prompt = "调研 B 模块的结构和入口" },
            new { description = "C", prompt = "调研 C 模块的结构和入口" }));

        var result = await Run(tools);

        Assert.Equal(3, result.NewMessages.Count(m => m.Role == ChatRole.Tool));
        // 每个子代理等模型 400 ms：串行时同一时刻只有 1 个在等，并行时 3 个重叠
        Assert.True(_gateway.MaxChildrenInFlight >= 2, $"同时在跑的子代理最多 {_gateway.MaxChildrenInFlight} 个，没有并行");
        // 结果按调用顺序写回
        var order = result.NewMessages.Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId).ToList();
        Assert.Equal(new[] { "call_0", "call_1", "call_2" }, order);
    }

    [Fact]
    public async Task PerRunLimit()
    {
        var (tools, _) = Setup(limits: new SubAgentLimits { MaxPerRun = 1 });
        _gateway.Parent.Enqueue(Spawn(new { description = "A", prompt = "调研 A 模块的结构和入口" }));
        _gateway.Parent.Enqueue(Spawn(new { description = "B", prompt = "调研 B 模块的结构和入口" }));

        var result = await Run(tools);

        var second = result.NewMessages.Where(m => m.Role == ChatRole.Tool).Last();
        Assert.Contains("上限", second.Content);
        Assert.Single(_store.Records.Values);
    }

    [Fact]
    public async Task EmptyAnswerIsAFailureNotASilentSuccess()
    {
        var (tools, _) = Setup();
        _gateway.Child = (_, _) => new ChatTurn { Content = "" };
        _gateway.Parent.Enqueue(Spawn(new { description = "A", prompt = "调研 A 模块的结构和入口" }));

        await Run(tools);

        Assert.Equal("failed", Assert.Single(_store.Records.Values).Status);
    }

    [Fact]
    public void ForkDropsADanglingToolCall()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.System("s"),
            ChatMessage.User("u"),
            ChatMessage.Assistant("", new[] { new ToolCall("x", "read_file", "{}") }),
        };
        Assert.Equal(2, SubAgentRunner.Sanitize(history).Count);
    }

    [Fact]
    public void OffByDefault()
    {
        Assert.False(SubAgentRunner.Enabled(null, agentMode: true));
        Assert.False(SubAgentRunner.Enabled(new SecuritySettings(new Dictionary<string, SecurityItem>()), agentMode: true));
        var on = new SecuritySettings(new Dictionary<string, SecurityItem> { [SecuritySettings.SubAgents] = new() { Value = true, Locked = true } });
        Assert.True(SubAgentRunner.Enabled(on, agentMode: true));
        Assert.False(SubAgentRunner.Enabled(on, agentMode: false));
    }
}
