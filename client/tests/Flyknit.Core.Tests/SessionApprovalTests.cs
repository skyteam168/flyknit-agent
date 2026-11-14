using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>「本任务内不再询问」：同类操作这一轮不再问，但高危操作每次都要问。</summary>
public class SessionApprovalTests
{
    /// <summary>每次都要确认的命令工具；effect 由参数决定，不真的执行。</summary>
    private sealed class FakeShell : ITool
    {
        public List<string> Ran { get; } = new();
        public string Name => "run_shell";
        public string Description => "";
        public JsonObject Parameters => new();
        public PolicyDecision Assess(JsonElement args, ToolContext ctx) =>
            new(RiskLevel.Confirm, "需要确认") { Effect = Enum.Parse<CommandEffect>(args.Str("effect")) };
        public string Describe(JsonElement args) => args.Str("command");
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Ran.Add(args.Str("command"));
            return Task.FromResult(ToolResult.Success("ok"));
        }
    }

    private sealed class Answer(ConfirmChoice choice) : IConfirmationHandler
    {
        public int Asked { get; private set; }
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(choice);
        }
    }

    private sealed class Script(params (string Command, string Effect)[] commands) : IChatGateway
    {
        private int _n;
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            if (_n >= commands.Length)
            {
                return Task.FromResult(new ChatTurn { Content = "完成" });
            }
            var (command, effect) = commands[_n++];
            return Task.FromResult(new ChatTurn
            {
                ToolCalls = new[] { new ToolCall($"call_{_n}", "run_shell", JsonSerializer.Serialize(new { command, effect })) },
            });
        }
    }

    private static async Task<(FakeShell Shell, Answer Confirm)> Run(ConfirmChoice choice, params (string, string)[] commands)
    {
        var shell = new FakeShell();
        var confirm = new Answer(choice);
        var loop = new AgentLoop(new Script(commands), new ToolRegistry().Add(shell), confirm);
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("跑一下脚本") };
        var ctx = new ToolContext
        {
            Policy = CommandPolicy.Default(),
            ConversationId = "c1",
            Workspace = Path.GetTempPath(),
            Permission = PermissionMode.Workspace,
            Security = new SecuritySettings(new Dictionary<string, SecurityItem>()),
        };
        await loop.RunAsync(history, Scenes.Agent, ctx, new NullObserver(), useTools: true, CancellationToken.None);
        return (shell, confirm);
    }

    private sealed class NullObserver : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }

    [Fact]
    public async Task SameKindOfCommandIsAskedOnlyOnce()
    {
        var (shell, confirm) = await Run(ConfirmChoice.AllowForSession,
            ("python a.py", "Unknown"), ("python b.py", "Unknown"), ("npm install lodash", "Write"), ("npm install axios", "Write"));
        Assert.Equal(4, shell.Ran.Count);   // 都执行了（以前 allowForSession 会落成拒绝）
        Assert.Equal(2, confirm.Asked);     // python 问一次、npm install 问一次
    }

    [Fact]
    public async Task DestructiveCommandsAreAlwaysAsked()
    {
        var (shell, confirm) = await Run(ConfirmChoice.AllowForSession,
            ("del a.txt", "Destructive"), ("del /s /q D:\\数据", "Destructive"));
        Assert.Equal(2, confirm.Asked);
        Assert.Equal(2, shell.Ran.Count);
    }

    [Fact]
    public async Task ADestructiveCommandIsNotCoveredByAnEarlierSafeOneFromTheSameProgram()
    {
        // 先在本任务内放行了 python，再来一条同一个程序、但被判为高危的命令：照样要问
        var (_, confirm) = await Run(ConfirmChoice.AllowForSession,
            ("python a.py", "Unknown"), ("python a.py", "Destructive"));
        Assert.Equal(2, confirm.Asked);
    }

    // ---------- docs/approval-p0.md 第 3、4 节：按规则记、同一个任务里共用 ----------

    /// <summary>像 MCP 查询那样的工具：摘要是「参数名：参数值」，每次都不一样；规则候选按工具名记。</summary>
    private sealed class FakeQuery(string name, bool destructive = false, bool alwaysAsk = false) : ITool
    {
        public int Ran { get; private set; }
        public string Name => name;
        public string Description => "";
        public JsonObject Parameters => new();
        public PolicyDecision Assess(JsonElement args, ToolContext ctx) => new(RiskLevel.Confirm, "会在对方系统里生效")
        {
            Effect = destructive ? CommandEffect.Destructive : CommandEffect.Write,
            Rule = destructive ? null : new ApprovalCandidate(name, "*", name, name, "*", name),
            AlwaysAsk = alwaysAsk,
        };
        public string Describe(JsonElement args) => $"sql：{args.Str("sql")}";
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Ran++;
            return Task.FromResult(ToolResult.Success("ok"));
        }
    }

    private sealed class Calls(params (string Tool, string Sql)[] calls) : IChatGateway
    {
        private int _n;
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            if (_n >= calls.Length)
            {
                return Task.FromResult(new ChatTurn { Content = "完成" });
            }
            var (tool, sql) = calls[_n++];
            return Task.FromResult(new ChatTurn
            {
                ToolCalls = new[] { new ToolCall($"q_{_n}", tool, JsonSerializer.Serialize(new { sql })) },
            });
        }
    }

    private static ToolContext NewContext(SessionApprovals? shared = null) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = Path.GetTempPath(),
        Permission = PermissionMode.Workspace,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>()),
        SessionApprovals = shared,
    };

    private static async Task<Answer> RunQueries(ToolContext ctx, ConfirmChoice choice, ToolRegistry tools, params (string, string)[] calls)
    {
        var confirm = new Answer(choice);
        var loop = new AgentLoop(new Calls(calls), tools, confirm);
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("查一下") };
        await loop.RunAsync(history, Scenes.Agent, ctx, new NullObserver(), useTools: true, CancellationToken.None);
        return confirm;
    }

    [Fact]
    public async Task AQueryToolIsAskedOnceEvenThoughEveryCallHasDifferentArguments()
    {
        // 以前按摘要第一个词记：「sql：select a」「sql：select b」是两个键，点了「本任务内不再询问」照样每次弹
        var orders = new FakeQuery("mcp__mes__query_orders");
        var confirm = await RunQueries(NewContext(), ConfirmChoice.AllowForSession, new ToolRegistry().Add(orders),
            ("mcp__mes__query_orders", "select * from orders where line='A'"),
            ("mcp__mes__query_orders", "select count(*) from orders"),
            ("mcp__mes__query_orders", "select * from orders where id=1001"));
        Assert.Equal(1, confirm.Asked);
        Assert.Equal(3, orders.Ran);
    }

    [Fact]
    public async Task AnotherToolIsStillAsked()
    {
        var orders = new FakeQuery("mcp__mes__query_orders");
        var stock = new FakeQuery("mcp__erp__query_stock");
        var confirm = await RunQueries(NewContext(), ConfirmChoice.AllowForSession, new ToolRegistry().Add(orders).Add(stock),
            ("mcp__mes__query_orders", "a"), ("mcp__erp__query_stock", "b"), ("mcp__mes__query_orders", "c"), ("mcp__erp__query_stock", "d"));
        Assert.Equal(2, confirm.Asked);   // 每个工具问一次
    }

    [Fact]
    public async Task DestructiveAndAlwaysAskCallsAreStillAskedEveryTime()
    {
        var drop = new FakeQuery("mcp__mes__delete_order", destructive: true);
        var guarded = new FakeQuery("mcp__mes__query_salary", alwaysAsk: true);   // 污染模式 / IT 的 confirm 规则
        var confirm = await RunQueries(NewContext(), ConfirmChoice.AllowForSession, new ToolRegistry().Add(drop).Add(guarded),
            ("mcp__mes__delete_order", "1"), ("mcp__mes__delete_order", "2"), ("mcp__mes__query_salary", "a"), ("mcp__mes__query_salary", "b"));
        Assert.Equal(4, confirm.Asked);
    }

    [Fact]
    public void WithoutARuleTheKeyIsTheSameAsBefore()
    {
        // 没有规则候选（解释器、看不出网址的联网命令）：照旧按摘要里的程序名记
        Assert.Equal(SessionApprovals.KeyFor("run_shell", "python a.py", null), SessionApprovals.KeyFor("run_shell", "python b.py", null));
        Assert.Equal("run_shell:npm install", SessionApprovals.KeyFor("run_shell", "npm install lodash", null));
        Assert.NotEqual(SessionApprovals.KeyFor("run_shell", "npm install x", null), SessionApprovals.KeyFor("run_shell", "npm run x", null));
        // 有规则候选：范围也算进去，同一个前缀在另一个工作区要重新问
        var a = new ApprovalCandidate("run_shell", "powershell", "mkdir x", "mkdir", @"D:\a", "");
        var b = a with { Scope = @"D:\b" };
        Assert.NotEqual(SessionApprovals.KeyFor("run_shell", "", a), SessionApprovals.KeyFor("run_shell", "", b));
    }

    [Fact]
    public async Task ApprovalsAreSharedWithinOneTaskButNotAcrossTasks()
    {
        var tools = new ToolRegistry().Add(new FakeQuery("mcp__mes__query_orders"));
        var task = new SessionApprovals();

        // 主助手这边点了「本任务内不再询问」
        var parent = NewContext(task);
        Assert.Equal(1, (await RunQueries(parent, ConfirmChoice.AllowForSession, tools, ("mcp__mes__query_orders", "a"))).Asked);

        // 同一个任务里派出去的子代理（ForChild 带上同一份）：不再问
        var child = parent.ForChild("sub1", PermissionMode.Workspace);
        Assert.Same(task, child.SessionApprovals);
        Assert.Equal(0, (await RunQueries(child, ConfirmChoice.Reject, tools, ("mcp__mes__query_orders", "b"))).Asked);

        // 另一个任务（拆开的另一件、下一句话）：照问
        Assert.Equal(1, (await RunQueries(NewContext(), ConfirmChoice.AllowOnce, tools, ("mcp__mes__query_orders", "c"))).Asked);
    }

    [Fact]
    public async Task AnApprovalGivenOnAChildCardCountsForTheWholeTask()
    {
        var tools = new ToolRegistry().Add(new FakeQuery("mcp__mes__query_orders"));
        var parent = NewContext(new SessionApprovals());
        var child = parent.ForChild("sub1", PermissionMode.Workspace);
        Assert.Equal(1, (await RunQueries(child, ConfirmChoice.AllowForSession, tools, ("mcp__mes__query_orders", "a"))).Asked);
        Assert.Equal(0, (await RunQueries(parent, ConfirmChoice.Reject, tools, ("mcp__mes__query_orders", "b"))).Asked);
    }

    [Fact]
    public async Task ALoopWithoutASharedSetFillsInItsOwnForItsChildren()
    {
        var ctx = NewContext();
        await RunQueries(ctx, ConfirmChoice.AllowOnce, new ToolRegistry().Add(new FakeQuery("q")), ("q", "a"));
        Assert.NotNull(ctx.SessionApprovals);   // 之后 ForChild / 派遣专家拿到的就是这一份
    }

    [Fact]
    public void AChildLayerSeesTheTaskButDoesNotLeakUpwards()
    {
        var task = new SessionApprovals();
        var dispatch = new SessionApprovals(task);
        task.Add("a");
        dispatch.AddHere("b");                  // 派遣时的一次授权：只在这次派遣里
        dispatch.Add("c");                      // 专家卡片上点的「本任务内不再询问」：记到整个任务
        Assert.True(dispatch.Contains("a"));
        Assert.True(dispatch.Contains("b"));
        Assert.False(task.Contains("b"));
        Assert.True(task.Contains("c"));
    }
}
