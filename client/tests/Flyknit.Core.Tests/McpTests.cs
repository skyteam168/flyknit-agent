using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

public class McpTests
{
    // ---------- 配置与占位符 ----------

    private static McpVendor Docs() => new()
    {
        Id = "tencent-docs",
        Name = "腾讯文档",
        Transport = "http",
        Url = "https://docs.qq.com/openapi/mcp?region=${REGION:-cn}",
        Headers = new() { ["Authorization"] = "Bearer ${API_KEY}", ["X-Team"] = "${TEAM}" },
        Fields = new()
        {
            new McpField { Key = "API_KEY", Required = true },
            new McpField { Key = "TEAM", Required = false },
        },
    };

    [Fact]
    public void PlaceholdersExpandFromPresetThenUserValues()
    {
        var vendor = Docs();
        vendor.Preset["API_KEY"] = "company-key";
        var config = McpTemplate.Resolve(vendor, null);
        Assert.Equal("https://docs.qq.com/openapi/mcp?region=cn", config.Url);
        Assert.Equal("Bearer company-key", config.Headers["Authorization"]);
        // 没填的可选项整个头不发，而不是发一个空值
        Assert.False(config.Headers.ContainsKey("X-Team"));

        var mine = McpTemplate.Resolve(vendor, new Dictionary<string, string> { ["API_KEY"] = "my-key", ["REGION"] = "vn" });
        Assert.Equal("Bearer my-key", mine.Headers["Authorization"]);
        Assert.Equal("https://docs.qq.com/openapi/mcp?region=vn", mine.Url);
    }

    [Fact]
    public void PlaceholdersNeverReadThisComputersEnvironment()
    {
        // 管理员写 ${PATH} 也拿不到员工电脑上的环境变量，免得把本机密钥发给第三方
        Environment.SetEnvironmentVariable("FLYKNIT_TEST_SECRET", "leak-me");
        Assert.Equal("Bearer ", McpTemplate.Expand("Bearer ${FLYKNIT_TEST_SECRET}", new Dictionary<string, string>()));
    }

    [Fact]
    public void MissingListsRequiredFieldsWithoutValues()
    {
        var vendor = Docs();
        Assert.Equal(new[] { "API_KEY" }, McpTemplate.Missing(vendor, new Dictionary<string, string>()));
        Assert.Empty(McpTemplate.Missing(vendor, new Dictionary<string, string> { ["API_KEY"] = "x" }));
        Assert.Equal(new[] { "API_KEY" }, McpTemplate.Missing(vendor, new Dictionary<string, string> { ["API_KEY"] = "  " }));
    }

    [Fact]
    public void FingerprintChangesWithConnectionSettingsOnly()
    {
        var a = Docs();
        var b = Docs();
        b.Description = "改了介绍";
        b.Icon = "data:image/png;base64,xx";
        Assert.Equal(a.ConnectionFingerprint(), b.ConnectionFingerprint());
        b.Url = "https://docs.qq.com/v2/mcp";
        Assert.NotEqual(a.ConnectionFingerprint(), b.ConnectionFingerprint());
    }

    // ---------- 工具名 ----------

    [Fact]
    public void ToolNamesAreNamespacedSanitisedAndShortEnough()
    {
        Assert.Equal("mcp__tencent-docs__create_sheet", McpNames.ToolName("tencent-docs", "create_sheet"));
        Assert.Equal("mcp__wecom__send_msg_v2", McpNames.ToolName("wecom", "send.msg v2"));
        Assert.True(McpNames.IsMcp("mcp__x__y"));
        Assert.False(McpNames.IsMcp("read_file"));

        var longA = McpNames.ToolName("a-very-long-vendor-identifier", new string('x', 80) + "_a");
        var longB = McpNames.ToolName("a-very-long-vendor-identifier", new string('x', 80) + "_b");
        Assert.True(longA.Length <= McpNames.MaxLength);
        Assert.NotEqual(longA, longB); // 截断后也不撞名
    }

    // ---------- 工具定义与结果 ----------

    [Fact]
    public void ToolSchemasAreFixedUpForOpenAiCompatibleApis()
    {
        var tool = McpClient.Parse(JsonNode.Parse("""{"name":"ping","inputSchema":{}}""")!.AsObject())!;
        Assert.Equal("object", tool.InputSchema["type"]!.ToString());
        Assert.NotNull(tool.InputSchema["properties"]);
        Assert.False(tool.ReadOnly);
        Assert.True(tool.Destructive); // 没声明就按可能有破坏性

        var ro = McpClient.Parse(JsonNode.Parse("""{"name":"read","annotations":{"readOnlyHint":true,"title":"读文档"}}""")!.AsObject())!;
        Assert.True(ro.ReadOnly);
        Assert.Equal("读文档", ro.Title);
        Assert.Null(McpClient.Parse(JsonNode.Parse("""{"description":"no name"}""")!.AsObject()));
    }

    [Fact]
    public void ResultsAreRenderedAsTextTheModelCanRead()
    {
        var r = McpClient.Render(JsonNode.Parse("""
            {"content":[
              {"type":"text","text":"第一段"},
              {"type":"image","mimeType":"image/png","data":"AAAA"},
              {"type":"resource","resource":{"uri":"doc://1","text":"资源正文"}},
              {"type":"resource_link","name":"周报","uri":"https://docs.qq.com/1"}
            ]}
            """)!.AsObject());
        Assert.False(r.IsError);
        Assert.Contains("第一段", r.Text);
        Assert.Contains("[图片：image/png", r.Text);
        Assert.Contains("资源正文", r.Text);
        Assert.Contains("[周报](https://docs.qq.com/1)", r.Text);

        var structured = McpClient.Render(JsonNode.Parse("""{"structuredContent":{"n":3}}""")!.AsObject());
        Assert.Equal("""{"n":3}""", structured.Text);

        var failed = McpClient.Render(JsonNode.Parse("""{"isError":true,"content":[]}""")!.AsObject());
        Assert.True(failed.IsError);
    }

    // ---------- 确认 ----------

    private static McpTool MakeTool(bool readOnly, bool destructive, string name = "create_sheet") =>
        new("tencent-docs", "腾讯文档", new McpToolInfo(name, "新建表格", "新建在线表格", new JsonObject { ["type"] = "object" }, readOnly, destructive),
            (_, _, _) => Task.FromResult(new McpCallResult(false, "ok")));

    private static ToolContext Ctx() => new() { Policy = CommandPolicy.Default(), ConversationId = "c1" };

    [Fact]
    public void ReadOnlyToolsRunDestructiveOnesAlwaysAskOthersCanBeRemembered()
    {
        var args = JsonDocument.Parse("{}").RootElement;
        Assert.Equal(RiskLevel.Auto, MakeTool(true, false).Assess(args, Ctx()).Level);

        var destructive = MakeTool(false, true).Assess(args, Ctx());
        Assert.Equal(RiskLevel.Confirm, destructive.Level);
        Assert.False(destructive.Rememberable);

        var write = MakeTool(false, false).Assess(args, Ctx());
        Assert.Equal(RiskLevel.Confirm, write.Level);
        Assert.True(write.Rememberable);
        Assert.Equal("腾讯文档 · 新建表格", write.Rule!.Display);
    }

    [Fact]
    public void RememberingOneToolDoesNotApproveAnother()
    {
        var store = new ApprovalStore();
        var args = JsonDocument.Parse("{}").RootElement;
        store.Add(MakeTool(false, false).Assess(args, Ctx()).Rule!);

        Assert.True(store.IsAllowed(MakeTool(false, false).Assess(args, Ctx()).Rule));
        Assert.False(store.IsAllowed(MakeTool(false, false, "delete_sheet").Assess(args, Ctx()).Rule));
        // create_sheet 的规则不能放行 create_sheet_v2
        Assert.False(store.IsAllowed(MakeTool(false, false, "create_sheet_v2").Assess(args, Ctx()).Rule));
    }

    [Fact]
    public async Task ToolErrorsComeBackAsFailedResults()
    {
        var tool = new McpTool("x", "X", new McpToolInfo("t", "", "", new JsonObject(), false, false),
            (_, _, _) => throw new McpException("对方挂了"));
        var result = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, Ctx(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("对方挂了", result.Output);
    }

    // ---------- 工具注册表 ----------

    [Fact]
    public void RegistrySwapsMcpToolsWithoutTouchingBuiltIns()
    {
        var registry = ToolRegistry.CreateDefault();
        var builtIns = registry.All.Count;
        registry.Replace(t => t.Name.StartsWith("mcp__tencent-docs__"), new ITool[] { MakeTool(true, false), MakeTool(true, false, "read_doc") });
        Assert.Equal(builtIns + 2, registry.All.Count);
        registry.Replace(t => t.Name.StartsWith("mcp__tencent-docs__"), new ITool[] { MakeTool(true, false) });
        Assert.Equal(builtIns + 1, registry.All.Count);
        Assert.Equal(1, registry.RemoveWhere(t => McpNames.IsMcp(t.Name)));
        Assert.Equal(builtIns, registry.All.Count);
        Assert.NotNull(registry.Get("read_file"));
    }

    // ---------- Streamable HTTP ----------

    /// <summary>一个假的 MCP 服务：initialize 回 JSON 并给会话 id，tools/list 用 SSE 回，中间夹一条通知。</summary>
    private sealed class FakeStreamableServer : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<JsonObject> Bodies { get; } = new();
        public bool RequireAuth { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
            if (RequireAuth && request.Headers.Authorization?.Parameter != "good-token")
            {
                var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                unauthorized.Headers.WwwAuthenticate.ParseAdd("Bearer resource_metadata=\"https://docs.qq.com/.well-known/oauth-protected-resource\"");
                return unauthorized;
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Bodies.Add(body);
            var method = body["method"]!.ToString();
            var id = body["id"];
            if (id is null)
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            switch (method)
            {
                case "initialize":
                    var init = Json(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                        ["result"] = new JsonObject
                        {
                            ["protocolVersion"] = "2025-03-26",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject { ["name"] = "docs", ["version"] = "1.0" },
                            ["instructions"] = "先读后写",
                        },
                    });
                    init.Headers.Add("Mcp-Session-Id", "sess-42");
                    return init;
                case "tools/list":
                    var page = body["params"]?["cursor"]?.ToString();
                    var tools = page is null
                        ? new JsonArray(new JsonObject { ["name"] = "read_doc", ["annotations"] = new JsonObject { ["readOnlyHint"] = true } })
                        : new JsonArray(new JsonObject { ["name"] = "create_sheet", ["inputSchema"] = new JsonObject { ["type"] = "object" } });
                    var result = new JsonObject { ["tools"] = tools };
                    if (page is null)
                    {
                        result["nextCursor"] = "p2";
                    }
                    var sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\n\n"
                              + "data: " + new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString() + "\n\n";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
                case "tools/call":
                    var args = body["params"]!["arguments"]!["title"]!.ToString();
                    return Json(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                        ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "已创建 " + args }) },
                    });
                default:
                    return Json(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                    });
            }
        }

        private static HttpResponseMessage Json(JsonObject o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static McpServerConfig HttpConfig(string transport = "http") => new()
    {
        Id = "tencent-docs",
        Name = "腾讯文档",
        Transport = transport,
        Url = "https://docs.qq.com/openapi/mcp",
        Headers = new Dictionary<string, string> { ["X-Api-Key"] = "k-1" },
        Timeout = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task StreamableHttpHandshakeListsAndCallsTools()
    {
        var server = new FakeStreamableServer();
        await using var conn = await McpConnection.ConnectAsync(HttpConfig(), new HttpClient(server), "1.0", null, null, CancellationToken.None);

        Assert.Equal("http", conn.TransportUsed);
        Assert.Equal("docs", conn.Client.ServerName);
        Assert.Equal("先读后写", conn.Client.Instructions);
        Assert.Equal("2025-03-26", conn.Client.NegotiatedVersion);
        Assert.Equal(new[] { "read_doc", "create_sheet" }, conn.Tools.Select(t => t.Name)); // 翻页拿全

        var result = await conn.CallAsync("create_sheet", JsonNode.Parse("""{"title":"周报"}"""), CancellationToken.None);
        Assert.Equal("已创建 周报", result.Text);

        // 握手之后的请求都带上会话 id 和协商出的协议版本，自定义的头每次都带
        var call = server.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("sess-42", call.Headers.GetValues("Mcp-Session-Id").Single());
        Assert.Equal("2025-03-26", call.Headers.GetValues("MCP-Protocol-Version").Single());
        Assert.Equal("k-1", call.Headers.GetValues("X-Api-Key").Single());
        Assert.Contains(server.Bodies, b => b["method"]!.ToString() == "notifications/initialized");
    }

    [Fact]
    public async Task UnauthorisedConnectionsReportTheOAuthChallenge()
    {
        var server = new FakeStreamableServer { RequireAuth = true };
        var ex = await Assert.ThrowsAsync<McpAuthRequiredException>(() =>
            McpConnection.ConnectAsync(HttpConfig(), new HttpClient(server), "1.0", null, null, CancellationToken.None));
        Assert.Contains("resource_metadata", ex.WwwAuthenticate);

        // 有了令牌就能连上
        await using var conn = await McpConnection.ConnectAsync(HttpConfig(), new HttpClient(server), "1.0",
            _ => Task.FromResult<string?>("good-token"), null, CancellationToken.None);
        Assert.Equal(2, conn.Tools.Count);
    }

    [Fact]
    public async Task RpcErrorsAreRaisedWithTheServersMessage()
    {
        var server = new FakeStreamableServer();
        await using var conn = await McpConnection.ConnectAsync(HttpConfig(), new HttpClient(server), "1.0", null, null, CancellationToken.None);
        var transport = new StreamableHttpTransport(new HttpClient(server), new Uri("https://docs.qq.com/openapi/mcp"),
            _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
        var ex = await Assert.ThrowsAsync<McpRpcException>(() => transport.RequestAsync("resources/list", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(-32601, ex.Code);
    }

    // ---------- 旧版 HTTP+SSE ----------

    /// <summary>旧版服务：POST 到主地址回 405；GET 开一条 SSE 长连接，回应都从那里推回来。</summary>
    private sealed class FakeLegacyServer : HttpMessageHandler
    {
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                await _events.Writer.WriteAsync("event: endpoint\ndata: /messages?session=abc\n\n", ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ChannelStream(_events.Reader)) { Headers = { { "Content-Type", "text/event-stream" } } },
                };
            }
            if (path != "/messages")
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
            Assert.Equal("session=abc", request.RequestUri.Query.TrimStart('?'));
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            if (body["id"] is { } id && body["method"] is { } method)
            {
                JsonObject result = method.ToString() switch
                {
                    "initialize" => new JsonObject { ["protocolVersion"] = "2024-11-05", ["serverInfo"] = new JsonObject { ["name"] = "legacy" } },
                    "tools/list" => new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "send_message" }) },
                    _ => new JsonObject(),
                };
                var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
                await _events.Writer.WriteAsync($"event: message\ndata: {reply.ToJsonString()}\n\n", ct);
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    /// <summary>把 Channel 里一段段文字当成一条不断往外吐的流。</summary>
    private sealed class ChannelStream : Stream
    {
        private readonly ChannelReader<string> _reader;
        private byte[] _buffer = Array.Empty<byte>();
        private int _offset;

        public ChannelStream(ChannelReader<string> reader) => _reader = reader;

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
        {
            if (_offset >= _buffer.Length)
            {
                if (!await _reader.WaitToReadAsync(ct) || !_reader.TryRead(out var text))
                {
                    return 0;
                }
                _buffer = Encoding.UTF8.GetBytes(text);
                _offset = 0;
            }
            var n = Math.Min(destination.Length, _buffer.Length - _offset);
            _buffer.AsMemory(_offset, n).CopyTo(destination);
            _offset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task HttpFallsBackToLegacySseWhenTheServerIsOld()
    {
        var server = new FakeLegacyServer();
        await using var conn = await McpConnection.ConnectAsync(HttpConfig(), new HttpClient(server), "1.0", null, null, CancellationToken.None);
        Assert.Equal("sse", conn.TransportUsed);
        Assert.Equal("legacy", conn.Client.ServerName);
        Assert.Equal("send_message", conn.Tools.Single().Name);
    }

    [Fact]
    public async Task SseParserHandlesCommentsMultilineDataAndCrLf()
    {
        var text = ": keepalive\r\nevent: endpoint\r\ndata: /a\r\n\r\ndata: line1\ndata: line2\n\ndata:x";
        var events = new List<SseReader.SseEvent>();
        await foreach (var e in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None))
        {
            events.Add(e);
        }
        Assert.Equal(new[] { ("endpoint", "/a"), ("message", "line1\nline2"), ("message", "x") }, events.Select(e => (e.Event, e.Data)));
    }

    // ---------- stdio ----------

    [Fact]
    public void WindowsBatchShimsAreRunThroughCmd()
    {
        string? NoExe(string _) => null;
        var (file, args) = StdioTransport.ResolveCommand("npx", new[] { "-y", "pkg" }, windows: true, NoExe);
        Assert.Equal("cmd.exe", file);
        Assert.Equal(new[] { "/d", "/s", "/c", "npx", "-y", "pkg" }, args);

        var (exe, exeArgs) = StdioTransport.ResolveCommand("node", new[] { "srv.js" }, windows: true, f => f == "node.exe" ? @"C:\node\node.exe" : null);
        Assert.Equal(@"C:\node\node.exe", exe);
        Assert.Equal(new[] { "srv.js" }, exeArgs);

        Assert.Equal("npx", StdioTransport.ResolveCommand("npx", Array.Empty<string>(), windows: false, NoExe).File);
    }

    /// <summary>一个用 Python 写的最小 MCP 服务，测真实的进程收发。机器上没有 Python 就跳过。</summary>
    private const string PythonServer = """
        import sys, json
        print("starting up (not json)", flush=True)
        for line in sys.stdin:
            msg = json.loads(line)
            if "id" not in msg:
                continue
            m = msg["method"]
            if m == "initialize":
                r = {"protocolVersion": "2025-06-18", "serverInfo": {"name": "py"}}
            elif m == "tools/list":
                r = {"tools": [{"name": "echo", "inputSchema": {"type": "object"}}]}
            elif m == "tools/call":
                r = {"content": [{"type": "text", "text": "echo:" + json.dumps(msg["params"]["arguments"], ensure_ascii=False) + ":" + __import__("os").environ.get("GREETING", "")}]}
            else:
                r = {}
            sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": r}, ensure_ascii=False) + "\n")
            sys.stdout.flush()
        """;

    private static string? FindPython()
    {
        foreach (var candidate in new[] { "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(candidate, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                p!.WaitForExit(5000);
                if (p.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 试下一个
            }
        }
        return null;
    }

    [Fact]
    public async Task StdioServersTalkOverStandardInputAndOutput()
    {
        if (FindPython() is not { } python)
        {
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "flyknit-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "server.py");
        await File.WriteAllTextAsync(script, PythonServer);
        try
        {
            var config = new McpServerConfig
            {
                Id = "py",
                Name = "Py",
                Transport = "stdio",
                Command = python,
                Args = new[] { "-u", script },
                Env = new Dictionary<string, string> { ["GREETING"] = "你好", ["PYTHONIOENCODING"] = "utf-8" },
                Timeout = TimeSpan.FromSeconds(20),
            };
            await using var conn = await McpConnection.ConnectAsync(config, new HttpClient(), "1.0", null, dir, CancellationToken.None);
            Assert.Equal("stdio", conn.TransportUsed);
            Assert.Equal("echo", conn.Tools.Single().Name);
            var r = await conn.CallAsync("echo", JsonNode.Parse("""{"q":"鞋面"}"""), CancellationToken.None);
            Assert.Equal("""echo:{"q": "鞋面"}:你好""", r.Text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AMissingCommandSaysSoInsteadOfHanging()
    {
        var config = new McpServerConfig
        {
            Id = "x", Name = "X", Transport = "stdio", Command = "definitely-not-a-real-command-" + Guid.NewGuid().ToString("N")[..6],
            Timeout = TimeSpan.FromSeconds(10),
        };
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<McpException>(() => McpConnection.ConnectAsync(config, new HttpClient(), "1.0", null, null, CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(9), "进程起不来应该马上报错，不该等到超时");
    }

    // ---------- OAuth ----------

    [Fact]
    public void PkceMatchesTheRfcExample()
    {
        // RFC 7636 附录 B
        var challenge = McpOAuth.Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")));
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
        var (verifier, c2) = McpOAuth.Pkce();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain('=', c2);
    }

    [Fact]
    public void ResourceIsTheMcpUrlWithoutQueryOrFragment()
    {
        Assert.Equal("https://docs.qq.com/openapi/mcp", McpOAuth.CanonicalResource(new Uri("https://Docs.QQ.com/openapi/mcp?x=1#f")));
        Assert.Equal("https://mcp.example.com", McpOAuth.CanonicalResource(new Uri("https://mcp.example.com/")));
    }

    private sealed class FakeAuthServer : HttpMessageHandler
    {
        public List<string> Hits { get; } = new();
        public Dictionary<string, string> LastForm { get; private set; } = new();
        public int Refreshes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Hits.Add(url);
            switch (url)
            {
                case "https://docs.qq.com/.well-known/oauth-protected-resource":
                    return Json("""{"resource":"https://docs.qq.com/openapi/mcp","authorization_servers":["https://auth.qq.com/tenant"],"scopes_supported":["docs.read","docs.write"]}""");
                case "https://auth.qq.com/.well-known/oauth-authorization-server/tenant":
                    return Json("""{"issuer":"https://auth.qq.com/tenant","authorization_endpoint":"https://auth.qq.com/tenant/authorize","token_endpoint":"https://auth.qq.com/tenant/token","registration_endpoint":"https://auth.qq.com/tenant/register"}""");
                case "https://auth.qq.com/tenant/register":
                    var reg = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                    Assert.Equal("none", reg["token_endpoint_auth_method"]!.ToString());
                    Assert.StartsWith("http://127.0.0.1:", reg["redirect_uris"]![0]!.ToString());
                    return Json("""{"client_id":"dyn-client"}""");
                case "https://auth.qq.com/tenant/token":
                    LastForm = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                        .Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
                    if (LastForm["grant_type"] == "refresh_token")
                    {
                        Refreshes++;
                        return Json("""{"access_token":"at-2","expires_in":3600}""");
                    }
                    return Json("""{"access_token":"at-1","refresh_token":"rt-1","expires_in":60}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task OAuthDiscoversRegistersAuthorisesAndRefreshes()
    {
        var auth = new FakeAuthServer();
        var http = new HttpClient(auth);
        var challenge = "Bearer resource_metadata=\"https://docs.qq.com/.well-known/oauth-protected-resource\"";
        var begin = await McpOAuth.BeginAsync(http, new Uri("https://docs.qq.com/openapi/mcp"), challenge,
            "http://127.0.0.1:5555/callback", configuredClientId: null, configuredScopes: null, CancellationToken.None);

        Assert.Equal("dyn-client", begin.ClientId);
        var q = LoopbackReceiver.ParseQuery(begin.AuthorizeUrl.PathAndQuery);
        Assert.Equal("https://auth.qq.com/tenant/authorize", begin.AuthorizeUrl.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("https://docs.qq.com/openapi/mcp", q["resource"]);
        Assert.Equal("docs.read docs.write", q["scope"]);
        Assert.Equal(begin.State, q["state"]);

        var now = DateTimeOffset.Parse("2026-10-07T08:00:00Z");
        var tokens = await McpOAuth.ExchangeAsync(http, begin, "the-code", now, CancellationToken.None);
        Assert.Equal("at-1", tokens.AccessToken);
        Assert.Equal(begin.CodeVerifier, auth.LastForm["code_verifier"]);
        Assert.Equal("the-code", auth.LastForm["code"]);
        Assert.True(tokens.NeedsRefresh(now.AddSeconds(30)));
        Assert.False(tokens.NeedsRefresh(now.AddSeconds(-60)));

        var fresh = await McpOAuth.RefreshAsync(http, tokens, now, CancellationToken.None);
        Assert.Equal("at-2", fresh.AccessToken);
        Assert.Equal("rt-1", fresh.RefreshToken); // 对方没给新的就沿用旧的
        Assert.Equal("dyn-client", fresh.ClientId);
        Assert.Equal(1, auth.Refreshes);
    }

    [Fact]
    public async Task AConfiguredClientIdSkipsRegistration()
    {
        var auth = new FakeAuthServer();
        var begin = await McpOAuth.BeginAsync(new HttpClient(auth), new Uri("https://docs.qq.com/openapi/mcp"),
            "Bearer resource_metadata=\"https://docs.qq.com/.well-known/oauth-protected-resource\"",
            "http://127.0.0.1:5555/callback", "it-client", "docs.read", CancellationToken.None);
        Assert.Equal("it-client", begin.ClientId);
        Assert.DoesNotContain(auth.Hits, h => h.EndsWith("/register"));
        Assert.Equal("docs.read", LoopbackReceiver.ParseQuery(begin.AuthorizeUrl.PathAndQuery)["scope"]);
    }

    [Fact]
    public async Task ExpiredLoginWithoutRefreshTokenAsksToReconnect()
    {
        await Assert.ThrowsAsync<McpAuthRequiredException>(() =>
            McpOAuth.RefreshAsync(new HttpClient(), new McpTokens { AccessToken = "x" }, DateTimeOffset.Now, CancellationToken.None));
    }

    [Fact]
    public async Task LoopbackReceiverIgnoresForgedCallbacks()
    {
        using var receiver = new LoopbackReceiver();
        Assert.StartsWith("http://127.0.0.1:", receiver.RedirectUri);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wait = receiver.WaitAsync("good-state", "已连接", "失败", cts.Token);

        async Task<string> Hit(string path)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, receiver.Port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        Assert.Contains("失败", await Hit("/callback?code=evil&state=bad-state"));
        Assert.False(wait.IsCompleted);
        Assert.Contains("已连接", await Hit("/callback?code=abc%2B1&state=good-state"));
        var query = await wait;
        Assert.Equal("abc+1", query["code"]);
    }
}

public class McpPromptTests
{
    [Fact]
    public void ConnectedServicesAreListedWithTheirPrefixAndInstructions()
    {
        var section = Flyknit.Core.Agent.PromptBuilder.BuildMcpSection(new[]
        {
            new Flyknit.Core.Agent.McpPromptInfo("tencent-docs", "腾讯文档", 12, "先用 search_docs 找到文档 id\n再读取"),
        });
        Assert.Contains("腾讯文档（工具前缀 mcp__tencent-docs__，12 个工具）", section);
        Assert.Contains("  再读取", section);
        Assert.Equal("", Flyknit.Core.Agent.PromptBuilder.BuildMcpSection(Array.Empty<Flyknit.Core.Agent.McpPromptInfo>()));
    }

    [Fact]
    public void OnlyAgentModeMentionsConnectedServices()
    {
        var servers = new[] { new Flyknit.Core.Agent.McpPromptInfo("wecom", "企业微信", 3, "") };
        var agent = new Flyknit.Core.Agent.PromptBuilder(null, null).Build(new Flyknit.Core.Agent.PromptContext
        {
            Mode = Flyknit.Core.Agent.ConversationMode.Agent, McpServers = servers,
        });
        var chat = new Flyknit.Core.Agent.PromptBuilder(null, null).Build(new Flyknit.Core.Agent.PromptContext
        {
            Mode = Flyknit.Core.Agent.ConversationMode.Chat, McpServers = servers,
        });
        Assert.Contains("企业微信", agent);
        Assert.DoesNotContain("企业微信", chat);
    }
}

public class McpDescribeTests
{
    private static McpTool Tool(string title = "") =>
        new("figma", "Figma", new McpToolInfo("get_file", title, "", new JsonObject(), true, false),
            (_, _, _) => Task.FromResult(new McpCallResult(false, "")));

    [Fact]
    public void TheCardLineShowsTheFirstFewSimpleArguments()
    {
        var args = JsonDocument.Parse("""{"fileKey":"abc123","depth":2,"ids":["1","2"],"opts":{"x":1},"extra":"ignored"}""").RootElement;
        Assert.Equal("fileKey：abc123，depth：2，ids：[2 项]", Tool().Describe(args));
    }

    [Fact]
    public void NoArgumentsFallsBackToTheToolTitle()
    {
        var empty = JsonDocument.Parse("{}").RootElement;
        Assert.Equal("读取设计稿", Tool("读取设计稿").Describe(empty));
        Assert.Equal("get_file", Tool().Describe(empty));
    }
}

public class McpRedirectTests
{
    /// <summary>典型的 Starlette / FastMCP 部署：/mcp 307 到 /mcp/，而 /mcp/ 要带令牌。</summary>
    private sealed class SlashRedirectServer : HttpMessageHandler
    {
        public List<(string Url, string? Auth, string Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            var url = request.RequestUri!.ToString();
            if (url == "https://mcp.example.com/mcp")
            {
                var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                r.Headers.Location = new Uri("/mcp/", UriKind.Relative);
                return r;
            }
            if (url == "https://mcp.example.com/elsewhere")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://other.example.org/mcp");
                return r;
            }
            if (request.Headers.Authorization?.Parameter != "tok")
            {
                var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                unauthorized.Headers.WwwAuthenticate.ParseAdd("Bearer error=\"invalid_token\", error_description=\"audience mismatch\"");
                return unauthorized;
            }
            var id = JsonNode.Parse(body)?["id"];
            if (id is null)
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            var method = JsonNode.Parse(body)!["method"]!.ToString();
            JsonObject result = method == "initialize"
                ? new JsonObject { ["protocolVersion"] = "2025-06-18", ["serverInfo"] = new JsonObject { ["name"] = "slash" } }
                : new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "ping" }) };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task ATokenSurvivesASameSiteRedirect()
    {
        var server = new SlashRedirectServer();
        var http = new HttpClient(new McpRedirectHandler(server));
        var config = new McpServerConfig { Id = "x", Name = "X", Url = "https://mcp.example.com/mcp", Timeout = TimeSpan.FromSeconds(10) };

        await using var conn = await McpConnection.ConnectAsync(config, http, "1.0", _ => Task.FromResult<string?>("tok"), null, CancellationToken.None);

        Assert.Equal("ping", conn.Tools.Single().Name);
        // 跳转后的请求还带着令牌和原来的正文
        var followed = server.Seen.First(s => s.Url == "https://mcp.example.com/mcp/");
        Assert.Equal("Bearer tok", followed.Auth);
        Assert.Contains("\"initialize\"", followed.Body);
    }

    [Fact]
    public async Task ATokenIsNotForwardedToAnotherSite()
    {
        var server = new SlashRedirectServer();
        var http = new HttpClient(new McpRedirectHandler(server));
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://mcp.example.com/elsewhere") { Content = new StringContent("{}") };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer tok");
        using var resp = await http.SendAsync(req);

        var crossed = server.Seen.Single(s => s.Url == "https://other.example.org/mcp");
        Assert.Null(crossed.Auth);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task TheServersReasonIsInTheError()
    {
        var server = new SlashRedirectServer();
        var config = new McpServerConfig { Id = "x", Name = "X", Url = "https://mcp.example.com/mcp/", Timeout = TimeSpan.FromSeconds(10) };
        var ex = await Assert.ThrowsAsync<McpAuthRequiredException>(() =>
            McpConnection.ConnectAsync(config, new HttpClient(server), "1.0", _ => Task.FromResult<string?>("wrong"), null, CancellationToken.None));
        Assert.Equal("audience mismatch", ex.Reason);
        Assert.Contains("audience mismatch", ex.Message);
    }

    [Theory]
    [InlineData("Bearer resource_metadata=\"https://a/.well-known/x\", scope=\"files:read files:write\"", "scope", "files:read files:write")]
    [InlineData("Bearer error=invalid_token, scope=read", "scope", "read")]
    [InlineData("Bearer error=\"insufficient_scope\"", "error", "insufficient_scope")]
    [InlineData("Bearer realm=\"x\"", "scope", null)]
    [InlineData(null, "scope", null)]
    public void ChallengeParametersAreRead(string? challenge, string name, string? expected)
    {
        Assert.Equal(expected, McpAuthRequiredException.ChallengeParam(challenge, name));
    }

    private sealed class ScopedAuthServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            string? json = url switch
            {
                "https://mcp.example.com/.well-known/oauth-protected-resource/mcp" =>
                    """{"resource":"https://mcp.example.com/mcp/","authorization_servers":["https://auth.example.com"],"scopes_supported":["a","b","c"]}""",
                "https://auth.example.com/.well-known/oauth-authorization-server" =>
                    """{"authorization_endpoint":"https://auth.example.com/authorize","token_endpoint":"https://auth.example.com/token"}""",
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task AuthorizationUsesTheScopeTheServerAskedForAndTheResourceItDeclared()
    {
        var begin = await McpOAuth.BeginAsync(new HttpClient(new ScopedAuthServer()), new Uri("https://mcp.example.com/mcp"),
            "Bearer scope=\"b\"", "http://127.0.0.1:1/callback", "client-1", null, CancellationToken.None);
        var q = LoopbackReceiver.ParseQuery(begin.AuthorizeUrl.PathAndQuery);
        Assert.Equal("b", q["scope"]);
        Assert.Equal("https://mcp.example.com/mcp/", q["resource"]);
        Assert.Equal("https://mcp.example.com/mcp/", begin.Resource);
    }
}
