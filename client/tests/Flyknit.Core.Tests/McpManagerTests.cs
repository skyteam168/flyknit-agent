using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>MCP 客户端补全：取消通知、ping 与健康检查、重连策略、单服务器日志、只读并行、工具图片。</summary>
public sealed class McpManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-mcp").FullName;

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

    /// <summary>进程内的假服务：按方法名回话，记下收到的每条消息。</summary>
    private sealed class FakeTransport : McpTransport
    {
        public ConcurrentQueue<JsonObject> Received { get; } = new();
        public bool PingUnsupported { get; set; }

        protected override Task SendAsync(JsonObject message, long? requestId, CancellationToken ct)
        {
            Received.Enqueue((JsonObject)message.DeepClone());
            var method = message["method"]?.ToString();
            if (requestId is not { } id)
            {
                return Task.CompletedTask; // 通知
            }
            JsonObject? reply = method switch
            {
                "ping" when PingUnsupported => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "no" } },
                "ping" => Result(id, new JsonObject()),
                "tools/call" => Call(id, message["params"]!["name"]!.ToString()),
                _ => null,
            };
            if (reply is not null)
            {
                _ = Task.Run(() => Dispatch(reply));
            }
            return Task.CompletedTask;
        }

        private static JsonObject Result(long id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JsonObject? Call(long id, string tool) => tool switch
        {
            "slow" => null, // 一直不回
            "img" => Result(id, new JsonObject
            {
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "截图如下" },
                    new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }) }),
            }),
            _ => Result(id, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }) }),
        };

        public JsonObject? Cancelled(string reason) =>
            Received.FirstOrDefault(m => m["method"]?.ToString() == "notifications/cancelled" && m["params"]?["reason"]?.ToString() == reason);

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    // ---------- 取消 ----------

    [Fact]
    public async Task UserCancelTellsTheServerToStop()
    {
        var transport = new FakeTransport();
        var client = new McpClient(transport, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallToolAsync("slow", new JsonObject(), cts.Token));
        await Eventually(() => transport.Cancelled("cancelled by user") is not null);
        var callId = transport.Received.First(m => m["method"]?.ToString() == "tools/call")["id"]!.GetValue<long>();
        Assert.Equal(callId, transport.Cancelled("cancelled by user")!["params"]!["requestId"]!.GetValue<long>());
    }

    [Fact]
    public async Task TimeoutStillSendsCancelled()
    {
        var transport = new FakeTransport();
        var client = new McpClient(transport, TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAsync<McpException>(() => client.CallToolAsync("slow", new JsonObject(), CancellationToken.None));
        await Eventually(() => transport.Cancelled("timeout") is not null);
    }

    // ---------- ping / 健康检查 ----------

    [Fact]
    public async Task PingMeasuresRoundTripAndAcceptsMethodNotFound()
    {
        var transport = new FakeTransport();
        var client = new McpClient(transport, TimeSpan.FromSeconds(5));
        var latency = await client.PingAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(latency >= TimeSpan.Zero);
        transport.PingUnsupported = true;
        await client.PingAsync(TimeSpan.FromSeconds(2), CancellationToken.None); // 不认 ping 也算活着
    }

    [Fact]
    public async Task MonitorReportsUnhealthyAfterTwoMissedPings()
    {
        var calls = 0;
        string? unhealthy = null;
        await using var monitor = new McpHealthMonitor(_ => { calls++; throw new McpException("ping 超时"); }, () => 0, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        monitor.Unhealthy += r => unhealthy = r;
        monitor.Start();
        await Eventually(() => unhealthy is not null);
        Assert.Equal(McpHealthMonitor.FailuresBeforeUnhealthy, calls);
        Assert.Contains("ping 超时", unhealthy);
    }

    [Fact]
    public async Task MonitorDoesNotPingWhileACallIsRunning()
    {
        var pings = 0;
        var pending = 1;
        string? unhealthy = null;
        await using var monitor = new McpHealthMonitor(_ => { pings++; throw new McpException("x"); }, () => pending, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        monitor.Unhealthy += r => unhealthy = r;
        monitor.Start();
        await Task.Delay(200);
        Assert.Equal(0, pings); // 单线程服务正忙着跑长任务：不去打扰、更不能判成坏了
        Assert.Null(unhealthy);
        pending = 0;
        await Eventually(() => unhealthy is not null);
    }

    [Fact]
    public async Task MonitorReportsLatency()
    {
        TimeSpan? seen = null;
        await using var monitor = new McpHealthMonitor(_ => Task.FromResult(TimeSpan.FromMilliseconds(12)), () => 0, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(1));
        monitor.Latency += l => seen = l;
        monitor.Start();
        await Eventually(() => seen is not null);
        Assert.Equal(12, seen!.Value.TotalMilliseconds);
    }

    // ---------- 重连策略 ----------

    [Fact]
    public void RemoteBacksOffFiveTimes()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), McpReconnectPolicy.Next("http", 0, TimeSpan.Zero, 0));
        Assert.Equal(TimeSpan.FromSeconds(16), McpReconnectPolicy.Next("http", 4, TimeSpan.Zero, 0));
        Assert.Null(McpReconnectPolicy.Next("http", 5, TimeSpan.Zero, 0));
    }

    [Fact]
    public void StdioRestartsOnlyAfterRunningStably()
    {
        // 一启动就崩：配置或环境问题，不反复拉起
        Assert.Null(McpReconnectPolicy.Next("stdio", 0, TimeSpan.FromSeconds(5), 0));
        // 跑稳过才崩：重启，10 分钟内最多 3 次
        Assert.Equal(TimeSpan.FromSeconds(1), McpReconnectPolicy.Next("stdio", 0, TimeSpan.FromMinutes(5), 0));
        Assert.Equal(TimeSpan.FromSeconds(4), McpReconnectPolicy.Next("stdio", 0, TimeSpan.FromMinutes(5), 2));
        Assert.Null(McpReconnectPolicy.Next("stdio", 0, TimeSpan.FromMinutes(5), 3));
    }

    [Fact]
    public void OldRestartsFallOutOfTheWindow()
    {
        var now = DateTimeOffset.Now;
        var restarts = new List<DateTimeOffset> { now.AddMinutes(-30), now.AddMinutes(-11), now.AddMinutes(-2) };
        Assert.Equal(1, McpReconnectPolicy.Recent(restarts, now));
    }

    // ---------- 日志 ----------

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def")]
    [InlineData("curl -H 'Cookie: sid=123456789'", "123456789")]
    [InlineData("api_key=sk-live-1234567890abcdef1234", "1234567890abcdef")]
    [InlineData("{\"password\": \"hunter2\"}", "hunter2")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123", "ghp_abcdefghij")]
    [InlineData("session_id=deadbeefcafe&x=1", "deadbeefcafe")]
    public void LogRedactsCredentials(string line, string secret)
    {
        var redacted = McpServerLog.Redact(line);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void MultilineStderrIsRedactedLineByLine()
    {
        // 报错里带的标准错误输出也会进界面和工具结果（模型看得到），同样要打码，而且只打那一行
        var text = McpServerLog.Redact("starting\nAuthorization: Bearer supersecrettoken123\nlistening on 3000");
        Assert.DoesNotContain("supersecrettoken123", text);
        Assert.Contains("listening on 3000", text);
        Assert.Contains("starting", text);
    }

    [Fact]
    public void LogLeavesOrdinaryTextAlone()
    {
        Assert.Equal("调用 search：成功，120 ms，300 字", McpServerLog.Redact("调用 search：成功，120 ms，300 字"));
    }

    [Fact]
    public void LogRollsOverWhenTooBig()
    {
        var log = new McpServerLog(_dir, "rea");
        File.WriteAllText(log.Path, new string('x', (int)McpServerLog.MaxBytes + 10));
        log.Write("新的一行");
        Assert.True(File.Exists(log.Path + ".1"));
        Assert.Contains("新的一行", File.ReadAllText(log.Path));
        Assert.True(new FileInfo(log.Path).Length < 1000);
    }

    [Fact]
    public async Task CallsAreLoggedWithoutArguments()
    {
        var log = new McpServerLog(_dir, "mes");
        var transport = new FakeTransport { Log = log };
        var client = new McpClient(transport, TimeSpan.FromSeconds(5));
        await client.CallToolAsync("echo", new JsonObject { ["customer"] = "某客户", ["password"] = "p@ss" }, CancellationToken.None);
        var text = File.ReadAllText(log.Path);
        Assert.Contains("调用 echo：成功", text);
        Assert.DoesNotContain("某客户", text);
        Assert.DoesNotContain("p@ss", text);
    }

    // ---------- 只读并行 ----------

    [Fact]
    public void OnlyReadOnlyConnectorToolsRunInParallel()
    {
        McpTool Make(bool readOnly) => new("mes", "MES", new McpToolInfo("q", "", "", new JsonObject { ["type"] = "object" }, readOnly, !readOnly),
            (_, _, _) => Task.FromResult(new McpCallResult(false, "ok")));
        var args = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        Assert.True(Make(true).CanRunInParallel(args));
        Assert.False(Make(false).CanRunInParallel(args));
    }

    // ---------- 图片 ----------

    [Fact]
    public async Task ImagesAreSavedAndPathIsInTheText()
    {
        var transport = new FakeTransport();
        var client = new McpClient(transport, TimeSpan.FromSeconds(5));
        var tool = new McpTool("pw", "Playwright", new McpToolInfo("img", "", "", new JsonObject { ["type"] = "object" }, true, false), client.CallToolAsync);
        var raw = await tool.ExecuteAsync(System.Text.Json.JsonDocument.Parse("{}").RootElement, null!, CancellationToken.None);
        Assert.Single(raw.Images);

        var (result, attachments) = ToolImageStore.Save(raw, _dir, tool.Name, "call1");
        var file = Assert.Single(attachments).LocalPath;
        Assert.True(File.Exists(file));
        Assert.EndsWith(".png", file);
        Assert.Contains($"已保存到 {file}", result.Output);
        Assert.Contains("截图如下", result.Output);
        Assert.Contains(file, result.Outputs); // 界面上有卡片能打开
        Assert.Empty(result.Images);
    }

    private ChatMessage ToolWithImage(string callId, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        var m = ChatMessage.ToolResult(new ToolCall(callId, "mcp__pw__shot", "{}"), "[图片 1]");
        m.Attachments.Add(new Attachment { FileName = name, LocalPath = path, Mime = "image/png", Size = 3 });
        return m;
    }

    [Fact]
    public void ToolImagesGoAfterTheWholeToolRunOnlyWhenAllowed()
    {
        var calls = new[] { new ToolCall("a", "mcp__pw__shot", "{}"), new ToolCall("b", "mcp__pw__shot", "{}") };
        var history = new List<ChatMessage>
        {
            ChatMessage.User("截个图"),
            ChatMessage.Assistant("", calls),
            ToolWithImage("a", "1.png"),
            ToolWithImage("b", "2.png"),
        };

        var plain = OpenAiSerializer.ToMessages(history);
        Assert.Equal(4, plain.Count); // 模型不能识图：只有文字

        var withImages = OpenAiSerializer.ToMessages(history, toolImages: true);
        Assert.Equal(5, withImages.Count);
        Assert.Equal("tool", withImages[2]!["role"]!.GetValue<string>());
        Assert.Equal("tool", withImages[3]!["role"]!.GetValue<string>()); // 两条工具结果之间不插东西
        var extra = withImages[4]!.AsObject();
        Assert.Equal("user", extra["role"]!.GetValue<string>());
        var parts = extra["content"]!.AsArray();
        Assert.Equal(OpenAiSerializer.ToolImagesNote, parts[0]!["text"]!.GetValue<string>());
        Assert.Equal(2, parts.Count(p => p!["type"]!.GetValue<string>() == "image_url"));
    }

    [Fact]
    public void OlderToolImagesAreNotResent()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("第一次"),
            ChatMessage.Assistant("", new[] { new ToolCall("a", "mcp__pw__shot", "{}") }),
            ToolWithImage("a", "old.png"),
            ChatMessage.Assistant("看到了"),
            ChatMessage.User("再来"),
        };
        Assert.Equal(5, OpenAiSerializer.ToMessages(history, toolImages: true).Count);
    }

    [Fact]
    public void AtMostFourToolImagesPerRequest()
    {
        var calls = Enumerable.Range(0, 6).Select(i => new ToolCall("c" + i, "mcp__pw__shot", "{}")).ToArray();
        var history = new List<ChatMessage> { ChatMessage.User("截很多图"), ChatMessage.Assistant("", calls) };
        history.AddRange(calls.Select((c, i) => ToolWithImage(c.Id, $"{i}.png")));
        var messages = OpenAiSerializer.ToMessages(history, toolImages: true);
        var parts = messages[^1]!["content"]!.AsArray();
        Assert.Equal(OpenAiSerializer.MaxToolImages, parts.Count(p => p!["type"]!.GetValue<string>() == "image_url"));
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

    [Fact]
    public async Task AgentLoopAttachesToolImagesAndPassesTheSwitch()
    {
        var transport = new FakeTransport();
        var client = new McpClient(transport, TimeSpan.FromSeconds(5));
        var tools = ToolRegistry.CreateDefault();
        tools.Add(new McpTool("pw", "Playwright", new McpToolInfo("img", "", "截图", new JsonObject { ["type"] = "object" }, true, false), client.CallToolAsync));
        var gateway = new Gateway(
            new ChatTurn { ToolCalls = new[] { new ToolCall("call_1", "mcp__pw__img", "{}") } },
            new ChatTurn { Content = "截图里是登录页" });
        var ctx = new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c1", Workspace = _dir };
        var loop = new AgentLoop(gateway, tools, new Allow(), null, new AgentOptions { ToolOutputDir = _dir, InlineToolImages = true });

        var result = await loop.RunAsync(new List<ChatMessage> { ChatMessage.User("截个图") }, Scenes.Agent, ctx, new NullObserver(), true, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, result.StopReason);
        var toolMessage = result.NewMessages.Single(m => m.Role == ChatRole.Tool);
        var image = Assert.Single(toolMessage.Attachments);
        Assert.True(File.Exists(image.LocalPath));
        Assert.Contains("已保存到", toolMessage.Content);
        Assert.True(gateway.Requests[1].InlineToolImages);
        // 第二次请求真的带上了图片
        var sent = OpenAiSerializer.ToMessages(gateway.Requests[1].Messages, gateway.Requests[1].InlineToolImages);
        Assert.Contains(sent, m => m!["role"]!.GetValue<string>() == "user" && m["content"] is JsonArray);
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
}
