using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Skills;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>外部来源的工具结果（连接器、浏览器、命令输出）里的 Cookie / 令牌 / 密码，进模型上下文之前打码。</summary>
public sealed class SecretRedactionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-redact").FullName;

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

    private sealed class Gateway : IChatGateway
    {
        private readonly Queue<ChatTurn> _turns;
        public List<ChatRequest> Requests { get; } = new();
        public Gateway(params ChatTurn[] turns) => _turns = new Queue<ChatTurn>(turns);
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : new ChatTurn { Content = "完成" });
        }
    }

    private sealed class Allow : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.AllowOnce);
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

    private const string Captured = """
        GET /api/2.0/folders/0 HTTP/1.1
        Cookie: z=abc123secretcookie; session_id=deadbeef42
        Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N
        {"type":"folder","id":"0","name":"All Files","password":"hunter2"}
        """;

    [Fact]
    public async Task ConnectorResultsAreRedactedBeforeTheModelSeesThem()
    {
        var tools = ToolRegistry.CreateDefault();
        tools.Add(new McpTool("pw", "Playwright", new McpToolInfo("network", "", "", new JsonObject { ["type"] = "object" }, true, false),
            (_, _, _) => Task.FromResult(new McpCallResult(false, Captured))));
        var gateway = new Gateway(new ChatTurn { ToolCalls = new[] { new ToolCall("c1", "mcp__pw__network", "{}") } }, new ChatTurn { Content = "看到了" });

        var result = await new AgentLoop(gateway, tools, new Allow()).RunAsync(
            new List<ChatMessage> { ChatMessage.User("看请求") }, Scenes.Agent,
            new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c1", Workspace = _dir }, new NullObserver(), true, CancellationToken.None);

        var sent = string.Join("\n", gateway.Requests[1].Messages.Select(m => m.Content));
        Assert.DoesNotContain("abc123secretcookie", sent);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", sent);
        Assert.DoesNotContain("hunter2", sent);
        Assert.Contains("/api/2.0/folders/0", sent);       // 结构还在
        Assert.Contains("\"name\":\"All Files\"", sent);
        Assert.DoesNotContain("hunter2", result.NewMessages.Single(m => m.Role == ChatRole.Tool).Content); // 存进对话的也打过码
    }

    [Fact]
    public async Task ReadingYourOwnFilesIsNotRedacted()
    {
        // 改配置要看到原文才能精确替换：读工作区的文件不打码
        var file = Path.Combine(_dir, "app.env");
        File.WriteAllText(file, "API_TOKEN=local-dev-123\n");
        var gateway = new Gateway(new ChatTurn { ToolCalls = new[] { new ToolCall("c1", "read_file", JsonSerializer.Serialize(new { path = file })) } });

        var result = await new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Allow()).RunAsync(
            new List<ChatMessage> { ChatMessage.User("看配置") }, Scenes.Agent,
            new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c1", Workspace = _dir }, new NullObserver(), true, CancellationToken.None);

        Assert.Contains("local-dev-123", result.NewMessages.Single(m => m.Role == ChatRole.Tool).Content);
    }

    [Theory]
    [InlineData("mcp__pw__network", true)]
    [InlineData("browser_read", true)]
    [InlineData("run_shell", true)]
    [InlineData("shell_output", true)]
    [InlineData("read_file", false)]
    [InlineData("grep", false)]
    public void WhichToolsAreRedacted(string tool, bool expected) => Assert.Equal(expected, SecretRedactor.AppliesTo(tool));

    [Fact]
    public void TheReverseRebuildSkillPackageIsValid()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../docs/skills/software-reverse-rebuild"));
        Assert.True(Directory.Exists(dir), dir);
        var inspection = SkillPackage.Inspect(dir, CommandPolicy.Default());
        Assert.True(inspection.Ok, inspection.Error);
        Assert.Equal("software-reverse-rebuild", inspection.Name);
        var skill = SkillCatalog.TryParse(Path.Combine(dir, "SKILL.md"), SkillSource.Organization)!;
        Assert.Equal(new[] { "playwright", "rea", "craft" }, skill.OptionalMcp); // 注释行没被当成字段
        Assert.Empty(skill.RequiredMcp);
    }
}
