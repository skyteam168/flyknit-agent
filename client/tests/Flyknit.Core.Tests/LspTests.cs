using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Hosting;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Flyknit.Core.Lsp;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 语言服务：JSON-RPC 收发、初始化、文档同步、诊断、请求转发（白名单、地址换成相对路径）、重命名写盘、信任、找命令。
/// 大部分用进程内的假语言服务；装了真的 typescript-language-server / pyright 时再跑一遍真的（CI 上没装就跳过）。
/// </summary>
public sealed class LspTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("fk-lsp-").FullName;

    public LspTests()
    {
        File.WriteAllText(Path.Combine(_ws, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_ws, "tsconfig.json"), "{\"compilerOptions\":{\"strict\":true},\"include\":[\"src\"]}");
        Directory.CreateDirectory(Path.Combine(_ws, "src"));
        File.WriteAllText(Path.Combine(_ws, "src", "a.ts"), "export const answer = 42\nconsole.log(answer)\n");
        File.WriteAllText(Path.Combine(_ws, "src", "b.ts"), "import { answer } from './a'\nconst x = answer + 1\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_ws, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>进程内的假语言服务：一对匿名管道，另一头也是 JsonRpcConnection。</summary>
    private sealed class FakeServer : IDisposable
    {
        public readonly AnonymousPipeServerStream ToClient = new(PipeDirection.Out);
        public readonly AnonymousPipeServerStream ToServer = new(PipeDirection.Out);
        public readonly AnonymousPipeClientStream ClientReads;
        public readonly AnonymousPipeClientStream ServerReads;
        public readonly JsonRpcConnection Rpc;
        public readonly List<(string Method, JsonNode? Params)> Notes = new();

        public FakeServer()
        {
            ClientReads = new AnonymousPipeClientStream(PipeDirection.In, ToClient.ClientSafePipeHandle);
            ServerReads = new AnonymousPipeClientStream(PipeDirection.In, ToServer.ClientSafePipeHandle);
            Rpc = new JsonRpcConnection(ServerReads, ToClient);
            Rpc.OnRequest = (method, p) => Task.FromResult<JsonNode?>(method switch
            {
                "initialize" => new JsonObject { ["capabilities"] = new JsonObject { ["hoverProvider"] = true } },
                "textDocument/hover" => new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = "const answer: 42" } },
                "textDocument/definition" => new JsonArray(new JsonObject
                {
                    ["uri"] = p!["textDocument"]!["uri"]!.GetValue<string>().Replace("b.ts", "a.ts"),
                    ["range"] = new JsonObject { ["start"] = Pos(0, 13), ["end"] = Pos(0, 19) },
                }),
                "shutdown" => null,
                _ => throw new InvalidOperationException("没实现：" + method),
            });
            Rpc.OnNotification = (method, p) =>
            {
                lock (Notes)
                {
                    Notes.Add((method, p));
                }
                if (method is "textDocument/didOpen" or "textDocument/didChange")
                {
                    var uri = p!["textDocument"]!["uri"]!.GetValue<string>();
                    var text = method == "textDocument/didOpen" ? p["textDocument"]!["text"]!.GetValue<string>() : p["contentChanges"]![0]!["text"]!.GetValue<string>();
                    // 内容里有 ERROR 就报一个错
                    var diags = text.Contains("ERROR")
                        ? new JsonArray(new JsonObject { ["range"] = new JsonObject { ["start"] = Pos(0, 0), ["end"] = Pos(0, 5) }, ["severity"] = 1, ["message"] = "找不到名称 ERROR" })
                        : new JsonArray();
                    _ = Rpc.NotifyAsync("textDocument/publishDiagnostics", new JsonObject { ["uri"] = uri, ["diagnostics"] = diags });
                }
            };
            Rpc.Start();
        }

        public void Dispose()
        {
            Rpc.Dispose();
            ToClient.Dispose();
            ToServer.Dispose();
            ClientReads.Dispose();
            ServerReads.Dispose();
        }
    }

    private static JsonObject Pos(int line, int ch) => new() { ["line"] = line, ["character"] = ch };

    private readonly List<FakeServer> _fakes = new();

    private LspHub Hub(bool trust = true, Func<string, string?>? env = null)
    {
        var machine = Directory.CreateDirectory(Path.Combine(_ws, ".machine")).FullName;
        // 假装 typescript-language-server 装好了
        File.WriteAllText(Path.Combine(machine, "typescript-language-server"), "");
        File.WriteAllText(Path.Combine(machine, "typescript-language-server.cmd"), "");
        var trustStore = new LspTrust(Path.Combine(_ws, ".data", "trust.json"));
        if (trust)
        {
            trustStore.Set(_ws, true);
        }
        return new LspHub(new LspCatalog(machine, env ?? (_ => "")), trustStore, Path.Combine(_ws, ".data"), _ => { })
        {
            Starter = async (spec, cmd, exe, root, data, ct) =>
            {
                var fake = new FakeServer();
                _fakes.Add(fake);
                return await LspClient.ConnectAsync(spec, root, fake.ClientReads, fake.ToServer, ct);
            },
        };
    }

    private LspFeature Feature(LspHub hub, bool enabled = true) => new(hub, id => id == "c1" ? _ws : null, () => enabled, _ => { });

    private static async Task<JsonElement> Call(LspFeature f, string method, object args)
    {
        var r = await f.HandleAsync(method, new BridgeArgs(JsonSerializer.SerializeToElement(args)), CancellationToken.None);
        return JsonSerializer.SerializeToElement(r);
    }

    [Fact]
    public async Task SyncsDocumentsAndCollectsDiagnostics()
    {
        await using var hub = Hub();
        var f = Feature(hub);
        Assert.True((await Call(f, "lsp.sync", new { conversationId = "c1", path = "src/a.ts", text = "ERROR here" })).GetProperty("ok").GetBoolean());
        var diags = await Call(f, "lsp.diagnostics", new { conversationId = "c1", path = "src/a.ts", wait = true });
        Assert.Equal("找不到名称 ERROR", diags.GetProperty("diagnostics")[0].GetProperty("message").GetString());
        // 改好了：诊断清空；同样的内容不重复发
        await Call(f, "lsp.sync", new { conversationId = "c1", path = "src/a.ts", text = "fixed" });
        await Call(f, "lsp.sync", new { conversationId = "c1", path = "src/a.ts", text = "fixed" });
        Assert.Equal(0, (await Call(f, "lsp.diagnostics", new { conversationId = "c1", path = "src/a.ts", wait = true })).GetProperty("diagnostics").GetArrayLength());
        var fake = Assert.Single(_fakes);
        lock (fake.Notes)
        {
            Assert.Equal(new[] { "initialized", "textDocument/didOpen", "textDocument/didChange" }, fake.Notes.Select(n => n.Method));
        }
    }

    [Fact]
    public async Task RequestsAreAllowlistedAndPathsComeBackRelative()
    {
        await using var hub = Hub();
        var f = Feature(hub);
        var hover = await Call(f, "lsp.request", new { conversationId = "c1", path = "src/a.ts", method = "textDocument/hover", @params = new { position = new { line = 0, character = 14 } } });
        Assert.Equal("const answer: 42", hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString());

        var def = await Call(f, "lsp.request", new { conversationId = "c1", path = "src/b.ts", method = "textDocument/definition", @params = new { position = new { line = 1, character = 11 } } });
        Assert.Equal(Path.Combine("src", "a.ts"), def.GetProperty("result")[0].GetProperty("path").GetString());

        // 不在白名单里的不转发；工作区外的文件不认
        Assert.Equal("notAllowed", (await Call(f, "lsp.request", new { conversationId = "c1", path = "src/a.ts", method = "workspace/executeCommand" })).GetProperty("error").GetString());
        Assert.Equal("badPath", (await Call(f, "lsp.request", new { conversationId = "c1", path = "../x.ts", method = "textDocument/hover" })).GetProperty("error").GetString());
        // 一个项目一种语言只起一个
        await Call(f, "lsp.request", new { conversationId = "c1", path = "src/b.ts", method = "textDocument/hover", @params = new { position = new { line = 0, character = 0 } } });
        Assert.Single(_fakes);
    }

    [Fact]
    public async Task UntrustedProjectsAndDisabledOrMissingServersDoNotStart()
    {
        await using (var hub = Hub(trust: false))
        {
            var f = Feature(hub);
            Assert.Equal("untrusted", (await Call(f, "lsp.sync", new { conversationId = "c1", path = "src/a.ts", text = "x" })).GetProperty("error").GetString());
            Assert.False((await Call(f, "lsp.status", new { conversationId = "c1" })).GetProperty("trusted").GetBoolean());
            await Call(f, "lsp.trust", new { conversationId = "c1", trust = true });
            Assert.True((await Call(f, "lsp.sync", new { conversationId = "c1", path = "src/a.ts", text = "x" })).GetProperty("ok").GetBoolean());
        }
        await using (var hub = Hub())
        {
            // 开发者工具没开
            Assert.Equal("disabled", (await Call(Feature(hub, enabled: false), "lsp.sync", new { conversationId = "c1", path = "src/a.ts", text = "x" })).GetProperty("error").GetString());
            // Python 没装
            File.WriteAllText(Path.Combine(_ws, "main.py"), "print(1)\n");
            Assert.Equal("missing", (await Call(Feature(hub), "lsp.sync", new { conversationId = "c1", path = "main.py", text = "x" })).GetProperty("error").GetString());
            // 不支持的文件
            Assert.Equal("unsupported", (await Call(Feature(hub), "lsp.sync", new { conversationId = "c1", path = "package.json", text = "{}" })).GetProperty("error").GetString());
            var status = await Call(Feature(hub), "lsp.status", new { conversationId = "c1" });
            var servers = status.GetProperty("servers").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("available").GetBoolean());
            Assert.True(servers["typescript"]);
            Assert.False(servers["python"]);
        }
    }

    [Fact]
    public void ItConfigCanOverrideOrDisableServers()
    {
        var machine = Directory.CreateDirectory(Path.Combine(_ws, ".it")).FullName;
        var tool = Path.Combine(machine, "tools", "my-pyright.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllText(tool, "");
        File.WriteAllText(Path.Combine(machine, "lsp.json"), JsonSerializer.Serialize(new
        {
            servers = new Dictionary<string, object> { ["python"] = new { command = tool, args = new[] { "--stdio" } } },
            disabled = new[] { "java" },
        }));
        var catalog = new LspCatalog(machine, _ => "");
        var py = catalog.Resolve(LspCatalog.Defaults.Single(s => s.Id == "python"));
        Assert.Equal(tool, py.ResolvedExe);
        Assert.Equal(new[] { "--stdio" }, py.Command!.Args);
        Assert.True(catalog.Resolve(LspCatalog.Defaults.Single(s => s.Id == "java")).Disabled);
        Assert.Null(catalog.Resolve(LspCatalog.Defaults.Single(s => s.Id == "go")).Command);
    }

    [Fact]
    public void SpecsAndRootsByFile()
    {
        var catalog = new LspCatalog(_ws, _ => "");
        Assert.Equal("typescript", catalog.ForFile("x/App.tsx")!.Id);
        Assert.Equal("vue", catalog.ForFile("App.vue")!.Id);
        Assert.Equal("csharp", catalog.ForFile("Program.cs")!.Id);
        Assert.Null(catalog.ForFile("README.md"));
        var web = Directory.CreateDirectory(Path.Combine(_ws, "apps", "web")).FullName;
        File.WriteAllText(Path.Combine(web, "tsconfig.json"), "{}");
        Directory.CreateDirectory(Path.Combine(web, "src"));
        var ts = LspCatalog.Defaults.Single(s => s.Id == "typescript");
        Assert.Equal(web, LspCatalog.RootFor(ts, Path.Combine(web, "src", "main.ts"), _ws));          // 最近的 tsconfig
        Assert.Equal(_ws, LspCatalog.RootFor(ts, Path.Combine(_ws, "src", "a.ts"), _ws));              // 工作区根的 package.json
        Assert.Equal("typescriptreact", ts.LanguageIdFor(".tsx"));
    }

    [Fact]
    public async Task RenameEditsAreWrittenOnlyInsideTheWorkspace()
    {
        await using var hub = Hub();
        var f = Feature(hub);
        var edit = new JsonArray(new JsonObject
        {
            ["path"] = Path.Combine("src", "b.ts"),
            ["edits"] = new JsonArray(
                new JsonObject { ["range"] = new JsonObject { ["start"] = Pos(0, 9), ["end"] = Pos(0, 15) }, ["newText"] = "result" },
                new JsonObject { ["range"] = new JsonObject { ["start"] = Pos(1, 10), ["end"] = Pos(1, 16) }, ["newText"] = "result" }),
        });
        var r = await Call(f, "lsp.applyEdit", new { conversationId = "c1", edits = edit });
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("import { result } from './a'\nconst x = result + 1\n", File.ReadAllText(Path.Combine(_ws, "src", "b.ts")));
        var outside = new JsonArray(new JsonObject { ["path"] = "../evil.ts", ["edits"] = new JsonArray() });
        Assert.False((await Call(f, "lsp.applyEdit", new { conversationId = "c1", edits = outside })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void EditsUseUtf16ColumnsAndKeepCrlf()
    {
        var edits = new JsonArray(new JsonObject { ["range"] = new JsonObject { ["start"] = Pos(1, 2), ["end"] = Pos(1, 4) }, ["newText"] = "YY" });
        Assert.Equal("ab\r\n中文XX\r\n", LspEdits.Apply("ab\r\n中文XX\r\n", new JsonArray(new JsonObject { ["range"] = new JsonObject { ["start"] = Pos(1, 2), ["end"] = Pos(1, 4) }, ["newText"] = "XX" })));
        Assert.Equal("ab\r\n中文YY\r\n", LspEdits.Apply("ab\r\n中文XX\r\n", edits));
        Assert.Throws<ArgumentException>(() => LspEdits.Apply("abc", new JsonArray(
            new JsonObject { ["range"] = new JsonObject { ["start"] = Pos(0, 0), ["end"] = Pos(0, 2) }, ["newText"] = "" },
            new JsonObject { ["range"] = new JsonObject { ["start"] = Pos(0, 1), ["end"] = Pos(0, 3) }, ["newText"] = "" })));
    }

    [Fact]
    public void UrisRoundTrip()
    {
        var p = Path.Combine(_ws, "src", "a b#c.ts");
        Assert.Equal(Path.GetFullPath(p), LspPaths.FromUri(LspPaths.ToUri(p)));
        Assert.Null(LspPaths.FromUri("untitled:Untitled-1"));
    }

    /// <summary>真的 typescript-language-server（装了才跑；CI 上没装直接过）。</summary>
    [Fact]
    public async Task RealTypeScriptServerWhenInstalled()
    {
        var catalog = new LspCatalog(Path.Combine(_ws, ".none"));
        var ts = catalog.Resolve(LspCatalog.Defaults.Single(s => s.Id == "typescript"));
        if (ts.Command is null)
        {
            return;
        }
        var trust = new LspTrust(Path.Combine(_ws, ".data", "trust.json"));
        trust.Set(_ws, true);
        await using var hub = new LspHub(catalog, trust, Path.Combine(_ws, ".data"), _ => { });
        var f = Feature(hub);
        var def = await Call(f, "lsp.request", new { conversationId = "c1", path = "src/b.ts", method = "textDocument/definition", @params = new { position = new { line = 1, character = 12 } } });
        Assert.True(def.GetProperty("ok").GetBoolean(), def.ToString());
        // Location（path）或 LocationLink（targetPath），看语言服务
        var target = def.GetProperty("result")[0];
        Assert.Equal(Path.Combine("src", "a.ts"), (target.TryGetProperty("targetPath", out var tp) ? tp : target.GetProperty("path")).GetString());
        await Call(f, "lsp.sync", new { conversationId = "c1", path = "src/b.ts", text = "const y: number = 'oops'\n" });
        var diags = await Call(f, "lsp.diagnostics", new { conversationId = "c1", path = "src/b.ts", wait = true });
        for (var i = 0; i < 10 && diags.GetProperty("diagnostics").GetArrayLength() == 0; i++)
        {
            diags = await Call(f, "lsp.diagnostics", new { conversationId = "c1", path = "src/b.ts", wait = true }); // 第一次检查要等项目加载完
        }
        Assert.True(diags.GetProperty("diagnostics").GetArrayLength() > 0, diags.ToString());
        Assert.Contains("string", diags.GetProperty("diagnostics")[0].GetProperty("message").GetString());
    }

    /// <summary>真的 pyright（装了才跑）：推送式诊断、跳到另一个文件的定义。</summary>
    [Fact]
    public async Task RealPythonServerWhenInstalled()
    {
        var catalog = new LspCatalog(Path.Combine(_ws, ".none"));
        if (catalog.Resolve(LspCatalog.Defaults.Single(s => s.Id == "python")).Command is null)
        {
            return;
        }
        File.WriteAllText(Path.Combine(_ws, "util.py"), "def add(a: int, b: int) -> int:\n    return a + b\n");
        File.WriteAllText(Path.Combine(_ws, "main.py"), "from util import add\nprint(add(1, 2))\n");
        var trust = new LspTrust(Path.Combine(_ws, ".data", "trust.json"));
        trust.Set(_ws, true);
        await using var hub = new LspHub(catalog, trust, Path.Combine(_ws, ".data"), _ => { });
        var f = Feature(hub);
        var def = await Call(f, "lsp.request", new { conversationId = "c1", path = "main.py", method = "textDocument/definition", @params = new { position = new { line = 1, character = 7 } } });
        Assert.True(def.GetProperty("ok").GetBoolean(), def.ToString());
        var target = def.GetProperty("result")[0];
        Assert.Equal("util.py", (target.TryGetProperty("targetPath", out var tp) ? tp : target.GetProperty("path")).GetString());
        await Call(f, "lsp.sync", new { conversationId = "c1", path = "main.py", text = "from util import add\nadd('x', 2)\n" });
        var diags = await Call(f, "lsp.diagnostics", new { conversationId = "c1", path = "main.py", wait = true });
        for (var i = 0; i < 10 && diags.GetProperty("diagnostics").GetArrayLength() == 0; i++)
        {
            diags = await Call(f, "lsp.diagnostics", new { conversationId = "c1", path = "main.py", wait = true });
        }
        Assert.True(diags.GetProperty("diagnostics").GetArrayLength() > 0, diags.ToString());
    }

    // ---------- 给 AI 的工具 ----------

    private ToolContext Ctx() => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _ws,
        Permission = PermissionMode.Workspace,
        RunId = "r1",
    };

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task DiagnosticsToolFormatsErrorsForTheAi()
    {
        await using var hub = Hub();
        File.WriteAllText(Path.Combine(_ws, "src", "a.ts"), "ERROR here\n");
        var r = await new CodeDiagnosticsTool(hub).ExecuteAsync(Args(new { path = "src/a.ts" }), Ctx(), CancellationToken.None);
        Assert.True(r.Ok, r.Output);
        Assert.Contains("1 个错误", r.Output);
        Assert.Contains("1:1 错误 找不到名称 ERROR", r.Output);
        // 工作区外的文件不认
        Assert.False((await new CodeDiagnosticsTool(hub).ExecuteAsync(Args(new { path = "../x.ts" }), Ctx(), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task DefinitionToolFindsTheColumnFromASymbol()
    {
        await using var hub = Hub();
        var r = await new GoToDefinitionTool(hub).ExecuteAsync(Args(new { path = "src/b.ts", line = 2, symbol = "answer" }), Ctx(), CancellationToken.None);
        Assert.True(r.Ok, r.Output);
        Assert.Contains("src/a.ts:1:14  export const answer = 42", r.Output);
        var bad = await new GoToDefinitionTool(hub).ExecuteAsync(Args(new { path = "src/b.ts", line = 2, symbol = "nothere" }), Ctx(), CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.Contains("没有「nothere」", bad.Output);
    }

    [Fact]
    public async Task ToolsExplainWhyTheyCannotRunAndOnlyAppearWhenUsable()
    {
        await using var untrusted = Hub(trust: false);
        var r = await new FindReferencesTool(untrusted).ExecuteAsync(Args(new { path = "src/a.ts", line = 1, column = 14 }), Ctx(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("信任", r.Output);

        RunScope Scope(string? ws) => new()
        {
            Conversation = new Conversation { Id = "c1", Workspace = ws, Mode = ConversationMode.Agent },
            Query = "q", RunId = "r", Scene = "code", NewUserMessage = true, Confirm = new AllowAll(),
        };
        Assert.Null(await new LspRunExtension(untrusted, () => true).PrepareAsync(Scope(_ws), CancellationToken.None)); // 没信任
        await using var trusted = Hub();
        Assert.Null(await new LspRunExtension(trusted, () => false).PrepareAsync(Scope(_ws), CancellationToken.None));  // 开发者工具没开
        var tools = (await new LspRunExtension(trusted, () => true).PrepareAsync(Scope(_ws), CancellationToken.None))!.Tools.Select(t => t.Name);
        Assert.Equal(new[] { "code_diagnostics", "go_to_definition", "find_references", "document_symbols" }, tools);
    }

    private sealed class AllowAll : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.AllowOnce);
    }

    /// <summary>真的 TS 语言服务 + AI 工具（装了才跑）：引用、大纲、改错后报错。</summary>
    [Fact]
    public async Task RealToolsWhenInstalled()
    {
        var catalog = new LspCatalog(Path.Combine(_ws, ".none"));
        if (catalog.Resolve(LspCatalog.Defaults.Single(s => s.Id == "typescript")).Command is null)
        {
            return;
        }
        var trust = new LspTrust(Path.Combine(_ws, ".data", "trust.json"));
        trust.Set(_ws, true);
        await using var hub = new LspHub(catalog, trust, Path.Combine(_ws, ".data"), _ => { });
        var refs = await new FindReferencesTool(hub).ExecuteAsync(Args(new { path = "src/a.ts", line = 1, symbol = "answer" }), Ctx(), CancellationToken.None);
        Assert.True(refs.Ok, refs.Output);
        Assert.Contains("src/b.ts:", refs.Output);
        var outline = await new DocumentSymbolsTool(hub).ExecuteAsync(Args(new { path = "src/a.ts" }), Ctx(), CancellationToken.None);
        Assert.Contains("answer", outline.Output);
        File.WriteAllText(Path.Combine(_ws, "src", "b.ts"), "const y: number = 'oops'\n");
        ToolResult diag = null!;
        for (var i = 0; i < 10; i++)
        {
            diag = await new CodeDiagnosticsTool(hub).ExecuteAsync(Args(new { path = "src/b.ts" }), Ctx(), CancellationToken.None);
            if (diag.Output.Contains("错误"))
            {
                break;
            }
        }
        Assert.Contains("1:7 错误", diag.Output);
    }

    [Fact]
    public void OldTscIsNotTakenForTheNativeLanguageServer()
    {
        var machine = Directory.CreateDirectory(Path.Combine(_ws, ".it2")).FullName;
        File.WriteAllText(Path.Combine(machine, "tsc"), "");
        File.WriteAllText(Path.Combine(machine, "tsc.cmd"), "");
        var ts = LspCatalog.Defaults.Single(s => s.Id == "typescript");
        // 只有旧的 tsc（TypeScript 6）：不算装了语言服务
        Assert.Empty(new LspCatalog(machine, _ => "") { VersionProbe = _ => 6 }.Candidates(ts));
        Assert.Null(new LspCatalog(machine, _ => "") { VersionProbe = _ => 6 }.Resolve(ts).Command);
        // TypeScript 7：tsc --lsp
        var seven = new LspCatalog(machine, _ => "") { VersionProbe = _ => 7 }.Candidates(ts);
        Assert.Equal(new[] { "--lsp", "--stdio" }, Assert.Single(seven).Command.Args);
    }
}
