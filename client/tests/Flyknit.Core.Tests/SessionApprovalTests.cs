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
}
