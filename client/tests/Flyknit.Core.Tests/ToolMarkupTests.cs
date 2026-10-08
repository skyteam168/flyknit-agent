using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 模型把工具调用写进正文（上游没解析成 tool_calls）：认得出的拿回来执行，认不出的去掉再要一次，
/// 无论如何不把 &lt;｜DSML｜invoke&gt; 这类标记留给用户看。
/// </summary>
public class ToolMarkupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-markup").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // 用户截图里的原样输出（DeepSeek-V3.2 DSML，全角竖线）
    private const string Dsml = """
        我先看看这两个目录里有什么。

        <｜DSML｜calls>
        <｜DSML｜invoke name="bash">
        <｜DSML｜parameter name="command" string="true">dir D:\报表 2>nul & echo --- & dir E:\共享</｜DSML｜parameter>
        </｜DSML｜invoke>
        </｜DSML｜calls>
        """;

    // ---------- 识别 ----------

    [Fact]
    public void DsmlCallsAreParsedAndStripped()
    {
        Assert.True(ToolMarkup.Contains(Dsml));
        var (text, calls) = ToolMarkup.Extract(Dsml);
        Assert.Equal("我先看看这两个目录里有什么。", text);
        var call = Assert.Single(calls);
        Assert.Equal("bash", call.Name);
        Assert.Equal(@"dir D:\报表 2>nul & echo --- & dir E:\共享", (string?)call.Arguments["command"]);
    }

    [Fact]
    public void DsmlWithAsciiBarsFunctionCallsAndTypedParameters()
    {
        const string text = """
            <| DSML |function_calls>
            <| DSML |invoke name="read_file"><| DSML |parameter name="path" string="true">a.txt</| DSML |parameter><| DSML |parameter name="max_lines" string="false">20</| DSML |parameter></| DSML |invoke>
            <| DSML |invoke name="list_dir"><| DSML |parameter name="path" string="true">.</| DSML |parameter></| DSML |invoke>
            </| DSML |function_calls>
            """;
        var (clean, calls) = ToolMarkup.Extract(text);
        Assert.Equal("", clean);
        Assert.Equal(new[] { "read_file", "list_dir" }, calls.Select(c => c.Name));
        Assert.Equal(20, (int)calls[0].Arguments["max_lines"]!);       // string="false" 按 JSON 解析
        Assert.Equal("a.txt", (string?)calls[0].Arguments["path"]);
    }

    [Fact]
    public void DeepSeekV3SpecialTokens()
    {
        const string text = "好的。<｜tool▁calls▁begin｜><｜tool▁call▁begin｜>function<｜tool▁sep｜>read_file\n```json\n{\"path\": \"报表.xlsx\"}\n```<｜tool▁call▁end｜><｜tool▁calls▁end｜>";
        var (clean, calls) = ToolMarkup.Extract(text);
        Assert.Equal("好的。", clean);
        var call = Assert.Single(calls);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("报表.xlsx", (string?)call.Arguments["path"]);
    }

    [Fact]
    public void HermesStyleToolCall()
    {
        const string text = "查一下：\n<tool_call>\n{\"name\": \"list_dir\", \"arguments\": {\"path\": \"D:\\\\报表\"}}\n</tool_call>";
        var (clean, calls) = ToolMarkup.Extract(text);
        Assert.Equal("查一下：", clean);
        Assert.Equal("list_dir", Assert.Single(calls).Name);
        Assert.Equal(@"D:\报表", (string?)calls[0].Arguments["path"]);

        // arguments 也可能是一段 JSON 字符串
        var (_, again) = ToolMarkup.Extract("<tool_call>{\"name\": \"read_file\", \"arguments\": \"{\\\"path\\\": \\\"a.txt\\\"}\"}</tool_call>");
        Assert.Equal("a.txt", (string?)Assert.Single(again).Arguments["path"]);
    }

    [Fact]
    public void TruncatedOrBrokenMarkupIsCutOff()
    {
        // 流式输出到一半被截断：块没闭合
        var (clean, calls) = ToolMarkup.Extract("结论如下。\n<｜DSML｜calls>\n<｜DSML｜invoke name=\"bash\">\n<｜DSML｜parameter name=\"comm");
        Assert.Equal("结论如下。", clean);
        Assert.Empty(calls);

        // 零散的记号
        Assert.Equal("答案是 42。", ToolMarkup.Extract("答案是 42。<｜DSML｜").Text);
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        const string text = "管道符 a | b，表格 | 列 |，还有 <tool> 标签和 tool_calls 这个词。";
        Assert.False(ToolMarkup.Contains(text));
        Assert.Equal(text, ToolMarkup.Extract(text).Text);
        Assert.False(ToolMarkup.Contains(null));
    }

    // ---------- 名字映射 ----------

    [Fact]
    public void CommonToolNamesMapToOurTools()
    {
        var registry = ToolRegistry.CreateDefault();
        JsonElement Args(ToolCall c) => JsonDocument.Parse(c.ArgumentsJson).RootElement;

        // 截图里那条：bash 名义下的 cmd 语法 → run_shell + cmd
        var shell = AgentLoop.MapLeaked(ToolMarkup.Extract(Dsml).Calls[0], registry)!;
        Assert.Equal("run_shell", shell.Name);
        Assert.Equal("cmd", Args(shell).GetProperty("shell").GetString());
        Assert.StartsWith("dir D:\\报表", Args(shell).GetProperty("command").GetString());

        var ps = AgentLoop.MapLeaked(new LeakedToolCall("powershell", new() { ["command"] = "Get-ChildItem" }), registry)!;
        Assert.Equal("powershell", Args(ps).GetProperty("shell").GetString());
        var plain = AgentLoop.MapLeaked(new LeakedToolCall("bash", new() { ["command"] = "Get-Date" }), registry)!;
        Assert.False(Args(plain).TryGetProperty("shell", out _));      // 交给默认的 PowerShell

        Assert.Equal("read_file", AgentLoop.MapLeaked(new LeakedToolCall("cat", new() { ["file_path"] = "a.txt" }), registry)!.Name);
        Assert.Equal("list_dir", AgentLoop.MapLeaked(new LeakedToolCall("ls", new() { ["path"] = "." }), registry)!.Name);
        Assert.Equal("read_file", AgentLoop.MapLeaked(new LeakedToolCall("read_file", new() { ["path"] = "a.txt" }), registry)!.Name);

        Assert.Null(AgentLoop.MapLeaked(new LeakedToolCall("web_browser", new()), registry));
        Assert.Null(AgentLoop.MapLeaked(new LeakedToolCall("bash", new()), registry));   // 没有命令
    }

    // ---------- 接进 Agent 循环 ----------

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

    private sealed class Allow : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.AllowOnce);
    }

    private sealed class Observer : IAgentObserver
    {
        public List<string> Notices { get; } = new();
        public List<ChatMessage> Answers { get; } = new();
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) => Answers.Add(message);
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
        public void OnNotice(string text) => Notices.Add(text);
    }

    private async Task<(AgentRunResult Result, Observer Observer)> Run(Gateway gateway, bool useTools)
    {
        var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("看看工作区里有什么") };
        var ctx = new ToolContext
        {
            Policy = CommandPolicy.Default(),
            ConversationId = "c1",
            Workspace = _dir,
            Permission = PermissionMode.Workspace,
            Security = new SecuritySettings(new Dictionary<string, SecurityItem>()),
        };
        var observer = new Observer();
        var loop = new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Allow(), options: new AgentOptions());
        var result = await loop.RunAsync(history, Scenes.Agent, ctx, observer, useTools, CancellationToken.None);
        return (result, observer);
    }

    private static TokenUsage Used => new(100, 10);

    [Fact]
    public async Task LeakedCallsInAgentModeAreExecuted()
    {
        File.WriteAllText(Path.Combine(_dir, "周报.csv"), "a\n");
        var gateway = new Gateway((n, _) => n == 1
            ? new ChatTurn { Content = $"先看一下。\n<｜DSML｜function_calls>\n<｜DSML｜invoke name=\"ls\">\n<｜DSML｜parameter name=\"path\" string=\"true\">{_dir}</｜DSML｜parameter>\n</｜DSML｜invoke>\n</｜DSML｜function_calls>", Usage = Used }
            : new ChatTurn { Content = "工作区里有一个周报.csv。", Usage = Used });

        var (result, observer) = await Run(gateway, useTools: true);

        Assert.Equal(1, result.ToolCalls);
        Assert.Equal(2, gateway.Requests.Count);                           // 没有额外重试
        Assert.Contains("周报.csv", result.NewMessages.Single(m => m.Role == ChatRole.Tool).Content);
        Assert.All(result.NewMessages, m => Assert.False(ToolMarkup.Contains(m.Content)));
        Assert.Equal("先看一下。", result.NewMessages.First(m => m.Role == ChatRole.Assistant).Content);
        Assert.Empty(observer.Notices);
        Assert.Contains(result.Trace!.Steps, s => s.Kind == "recover");
        Assert.Equal(new TokenUsage(200, 20), result.Usage);
    }

    [Fact]
    public async Task ChatModeAsksOnceAgainAndUsesTheNewAnswer()
    {
        var gateway = new Gateway((n, _) => n == 1
            ? new ChatTurn { Content = Dsml, Usage = Used }
            : new ChatTurn { Content = "对话模式下我不能直接查看你的电脑，可以切换到“办事”模式让我查。", Usage = Used });

        var (result, observer) = await Run(gateway, useTools: false);

        Assert.Equal(2, gateway.Requests.Count);
        Assert.Null(gateway.Requests[1].Tools);
        Assert.Contains("对话模式", gateway.Requests[1].Messages[^1].Content);      // 提醒了模型
        Assert.Equal(new[] { AgentLoop.LeakRetryNotice }, observer.Notices);
        var answer = Assert.Single(observer.Answers);
        Assert.StartsWith("对话模式下我不能", answer.Content);
        Assert.Equal(0, result.ToolCalls);
        Assert.Equal(new TokenUsage(200, 20), result.Usage);                    // 两次请求都算上，不重复
        // 提醒那句只用于重试，不留在对话历史里
        Assert.DoesNotContain(result.NewMessages, m => m.Content.Contains("（系统提示）"));
    }

    [Fact]
    public async Task IfTheModelKeepsDoingItTheMarkupIsStrippedAndThereIsOnlyOneRetry()
    {
        const string onlyMarkup = "<｜DSML｜calls><｜DSML｜invoke name=\"bash\"><｜DSML｜parameter name=\"command\" string=\"true\">dir</｜DSML｜parameter></｜DSML｜invoke></｜DSML｜calls>";
        var gateway = new Gateway((_, _) => new ChatTurn { Content = onlyMarkup });

        var (result, observer) = await Run(gateway, useTools: false);

        Assert.Equal(2, gateway.Requests.Count);
        Assert.Single(observer.Notices);
        Assert.Equal(AgentLoop.ChatModeFallback, Assert.Single(observer.Answers).Content);
        Assert.Equal(AgentStopReason.Completed, result.StopReason);
    }

    [Fact]
    public async Task UnknownToolsInAgentModeAreNotGuessed()
    {
        var gateway = new Gateway((n, _) => n == 1
            ? new ChatTurn { Content = "<tool_call>{\"name\": \"web_browser\", \"arguments\": {\"url\": \"http://x\"}}</tool_call>" }
            : new ChatTurn { Content = "这个问题不需要上网，答案是……" });

        var (result, observer) = await Run(gateway, useTools: true);

        Assert.Equal(0, result.ToolCalls);
        Assert.NotNull(gateway.Requests[1].Tools);                              // 办事模式重试时照样带上工具
        Assert.Contains("web_browser", gateway.Requests[1].Messages[^1].Content);
        Assert.Single(observer.Notices);
        Assert.StartsWith("这个问题不需要上网", Assert.Single(observer.Answers).Content);
    }
}
