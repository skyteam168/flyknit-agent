using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Headless;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 第四批：命令行（flyknit -p）和 ACP 服务。重点是安全：总开关默认关、命令行默认不放行任何需要确认的操作、
/// --allow-writes 和 IDE 的「总是允许」都放不行高危操作、blocked_patterns 照拦、审计标来源。
/// </summary>
public sealed class HeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flyknit-headless-" + Guid.NewGuid().ToString("N")[..8]);

    public HeadlessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------- 测试替身 ----------

    /// <summary>按顺序回放模型的回合；回合用完回答「完成」。</summary>
    private sealed class Scripted : IChatGateway
    {
        private readonly Queue<Func<ChatTurn>> _turns;
        public List<ChatRequest> Requests { get; } = new();

        public Scripted(params ChatTurn[] turns) => _turns = new(turns.Select(t => (Func<ChatTurn>)(() => t)));

        public Func<CancellationToken, Task>? Delay { get; init; }

        public async Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Requests.Add(request);
            if (Delay is not null)
            {
                await Delay(ct);
            }
            var turn = _turns.Count > 0 ? _turns.Dequeue()() : new ChatTurn { Content = "完成" };
            if (turn.Content.Length > 0)
            {
                sink?.OnContent(turn.Content);
            }
            return turn;
        }
    }

    /// <summary>像真的模型一样一个字一个字地吐：先思考、再回答。</summary>
    private sealed class Dribbling : IChatGateway
    {
        public const int Pieces = 200;

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            for (var i = 0; i < Pieces; i++)
            {
                sink?.OnReasoning("想");
            }
            for (var i = 0; i < Pieces; i++)
            {
                sink?.OnContent("答");
            }
            return Task.FromResult(new ChatTurn { Content = new string('答', Pieces), Reasoning = new string('想', Pieces) });
        }
    }

    private sealed class Throwing : IChatGateway
    {
        private readonly GatewayException _error;
        public Throwing(GatewayException error) => _error = error;
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) => throw _error;
    }

    private sealed class ListAudit : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = new();
        public void Record(AuditEntry entry) => Entries.Add(entry);
    }

    private static ChatTurn Call(string name, object args, string id = "") => new()
    {
        ToolCalls = new[] { new ToolCall(id.Length > 0 ? id : "call_" + Guid.NewGuid().ToString("N")[..6], name, JsonSerializer.Serialize(args)) },
    };

    private static SecuritySettings Security(bool cli = true, bool dev = false) => new(new Dictionary<string, SecurityItem>
    {
        [SecuritySettings.CliAccess] = new() { Value = cli },
        [SecuritySettings.DeveloperTools] = new() { Value = dev },
        // 测试里不走回收站（Linux 没有），直接删
        [SecuritySettings.DeleteProtection] = new() { Value = false },
    });

    private HeadlessEnvironment Env(IChatGateway gateway, IAuditSink? audit = null, string actor = "cli", SecuritySettings? security = null) => new()
    {
        Gateway = gateway,
        Security = security ?? Security(),
        Audit = audit ?? new NullAuditSink(),
        Actor = actor,
        Approvals = new ApprovalStore(Path.Combine(_dir, "approval-rules.json")),
    };

    private async Task<(int Code, JsonObject Output)> Print(IChatGateway gateway, string[] args, IAuditSink? audit = null, string workspace = "")
    {
        var (options, error) = CliOptions.Parse(args.Concat(new[] { "-w", workspace.Length > 0 ? workspace : _dir }).ToArray());
        Assert.Null(error);
        var recording = new RecordingGateway(gateway);
        var output = new StringWriter();
        var code = await PrintCommand.RunAsync(options!, Env(recording, audit), recording, output,
            PrintCommand.ResolvePermission(options!.Permission, "workspace"), CancellationToken.None);
        return (code, JsonNode.Parse(output.ToString())!.AsObject());
    }

    [Fact]
    public async Task PrintShowsProgressOnTheTerminalWithoutChangingTheOutput()
    {
        // 在终端里直接跑 -p：进度打到标准错误（每一步操作、页脚），回答和 JSON 照常最后写到标准输出
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        var (options, _) = CliOptions.Parse(new[] { "-p", "看看目录", "-w", _dir });
        var recording = new RecordingGateway(new Scripted(Call("list_dir", new { path = "." }), new ChatTurn { Content = "目录里只有 a.txt" }));
        var stdout = new StringWriter();
        var progress = new StringWriter();
        var code = await PrintCommand.RunAsync(options!, Env(recording), recording, stdout, PermissionMode.Workspace, CancellationToken.None,
            progress: progress, style: ConsoleStyle.Plain);

        Assert.Equal(ExitCodes.Ok, code);
        var json = JsonNode.Parse(stdout.ToString())!.AsObject();
        Assert.Equal("目录里只有 a.txt", json["answer"]!.GetValue<string>());
        var shown = progress.ToString();
        Assert.Contains("▸", shown);
        Assert.Contains("✓", shown);
        Assert.Contains("1 次工具", shown);
        Assert.DoesNotContain("目录里只有 a.txt", shown);   // 回答不重复打在进度里
    }

    [Fact]
    public async Task PrintExplainsDeclinedConfirmationsOnTheTerminal()
    {
        // 截图里的情况：-p 里要运行脚本，没人可问被拒绝了。终端上要说清楚为什么、怎么办，不用去 JSON 里找
        var (options, _) = CliOptions.Parse(new[] { "-p", "跑脚本", "-w", _dir });
        var recording = new RecordingGateway(new Scripted(Call("run_shell", new { command = "touch made.txt" }), new ChatTurn { Content = "没跑成" }));
        var stdout = new StringWriter();
        var progress = new StringWriter();
        await PrintCommand.RunAsync(options!, Env(recording), recording, stdout, PermissionMode.Workspace, CancellationToken.None,
            progress: progress, style: ConsoleStyle.Plain);

        var shown = progress.ToString();
        Assert.Contains("有 1 个操作需要确认", shown);
        Assert.Contains("touch made.txt", shown);
        Assert.Contains("直接输入 flyknit", shown);
        Assert.Contains("--allow-writes", shown);   // touch 是普通写入，加 --allow-writes 真能放行
        Assert.Single(JsonNode.Parse(stdout.ToString())!["declined"]!.AsArray());  // JSON 不变
    }

    [Fact]
    public void DeclinedHintOnlyMentionsAllowWritesWhenItWouldHelp()
    {
        var notHelpful = new List<(string, string, string)>
        {
            ("run_shell", "python merge.py", "这类操作（运行脚本、访问网络等）不能自动放行，只能由员工确认：在员工端或 flyknit 对话模式里做"),
        };
        var hint = PrintCommand.DeclinedHint(notHelpful, allowWrites: false, ConsoleStyle.Plain);
        Assert.Contains("python merge.py", hint);
        Assert.Contains("直接输入 flyknit", hint);
        Assert.DoesNotContain("--allow-writes", hint);
        Assert.Equal("", PrintCommand.DeclinedHint(new List<(string, string, string)>(), false, ConsoleStyle.Plain));
    }

    // ---------- 参数 ----------

    [Theory]
    [InlineData(new[] { "-p", "做事" }, "print", "做事", false)]
    [InlineData(new[] { "-p", "-" }, "print", "", true)]
    [InlineData(new[] { "--print" }, "print", "", true)]
    [InlineData(new[] { "acp" }, "acp", "", false)]
    [InlineData(new[] { "--version" }, "version", "", false)]
    [InlineData(new string[0], "chat", "", false)]
    [InlineData(new[] { "--help" }, "help", "", false)]
    [InlineData(new[] { "帮我看看这个目录" }, "chat", "帮我看看这个目录", false)]
    public void ParsesCommands(string[] args, string command, string prompt, bool stdin)
    {
        var (o, error) = CliOptions.Parse(args);
        Assert.Null(error);
        Assert.Equal(command, o!.Command);
        Assert.Equal(prompt, o.Prompt);
        Assert.Equal(stdin, o.PromptFromStdin);
    }

    [Theory]
    [InlineData("--permission", "full")]       // 命令行不给完全权限
    [InlineData("--format", "xml")]
    [InlineData("--timeout", "-3")]
    [InlineData("--bogus", "")]
    public void RejectsBadOptions(string flag, string value)
    {
        var args = new List<string> { "-p", "x", flag };
        if (value.Length > 0)
        {
            args.Add(value);
        }
        var (o, error) = CliOptions.Parse(args);
        Assert.Null(o);
        Assert.NotNull(error);
    }

    [Fact]
    public void ThinkingIsOffUnlessAsked()
    {
        // 不跟员工端的「深度思考」走：命令行 / IDE 一个任务几十步，每步先想一大段太慢
        Assert.False(CliOptions.Parse(new[] { "-p", "x" }).Options!.Think);
        Assert.True(CliOptions.Parse(new[] { "-p", "x", "--think" }).Options!.Think);
        Assert.False(CliOptions.Parse(new[] { "acp" }).Options!.Think);
        Assert.True(CliOptions.Parse(new[] { "acp", "--think" }).Options!.Think);
        Assert.NotNull(CliOptions.Parse(new[] { "acp", "--think", "--bogus" }).Error);
    }

    [Fact]
    public void PromptIsRequired()
    {
        Assert.NotNull(CliOptions.Parse(new[] { "--format", "text" }).Error);
        Assert.NotNull(CliOptions.Parse(new[] { "-p", "  " }).Error);
        Assert.NotNull(CliOptions.Parse(new[] { "acp", "extra" }).Error);
    }

    [Fact]
    public void FullPermissionIsNeverUsed()
    {
        Assert.Throws<ArgumentException>(() => new HeadlessSession(Env(new Scripted()), _dir, PermissionMode.Full));
        Assert.Equal(PermissionMode.ReadOnly, PrintCommand.ResolvePermission("", "readonly"));
        Assert.Equal(PermissionMode.Workspace, PrintCommand.ResolvePermission("", "full"));
    }

    // ---------- 总开关 ----------

    [Fact]
    public void AccessIsOffByDefault()
    {
        Assert.NotNull(HeadlessAccess.Problem(new SecuritySettings()));
        Assert.NotNull(HeadlessAccess.Problem(Security(cli: false)));
        Assert.Null(HeadlessAccess.Problem(Security(cli: true)));
    }

    [Fact]
    public async Task NotSignedInFailsBeforeTouchingTheNetwork()
    {
        var c = await CliHost.ConnectAsync(new LocalProfile { Root = _dir }, "cli", handler: null, warn: null, CancellationToken.None);
        Assert.False(c.Ok);
        Assert.Equal(ExitCodes.NotSignedIn, c.ExitCode);
    }

    [Fact]
    public async Task SwitchOffOnTheServerStopsTheCli()
    {
        var handler = new ConfigHandler("""{"security":{"cli_access":{"value":false,"locked":true}}}""");
        var c = await CliHost.ConnectAsync(new LocalProfile { Root = _dir, ServerUrl = "http://flyknit.test", DeviceToken = "t" }, "cli", handler, null, CancellationToken.None);
        Assert.False(c.Ok);
        Assert.Equal(ExitCodes.AccessDisabled, c.ExitCode);
        Assert.Contains("命令行 / IDE 接入", c.Error);

        // 员工自己在设置里打开也没用：锁住的以服务端为准
        var local = new LocalProfile
        {
            Root = _dir, ServerUrl = "http://flyknit.test", DeviceToken = "t",
            SecurityChoices = new() { [SecuritySettings.CliAccess] = true },
        };
        c = await CliHost.ConnectAsync(local, "cli", new ConfigHandler("""{"security":{"cli_access":{"value":false,"locked":true}}}"""), null, CancellationToken.None);
        Assert.Equal(ExitCodes.AccessDisabled, c.ExitCode);
    }

    [Fact]
    public async Task ConnectsWithTheServersSettings()
    {
        var handler = new ConfigHandler("""
            {"security":{"cli_access":{"value":true,"locked":true},"developer_tools":{"value":true,"locked":true}},
             "scenes":[{"scene":"agent","available":true,"context_length":64000}]}
            """);
        var c = await CliHost.ConnectAsync(new LocalProfile { Root = _dir, ServerUrl = "http://flyknit.test", DeviceToken = "t" }, "ide", handler, null, CancellationToken.None);
        Assert.True(c.Ok, c.Error);
        Assert.Equal("ide", c.Environment!.Actor);
        Assert.Equal(64000, c.Environment.ContextLength);
        Assert.True(c.Environment.Security.On(SecuritySettings.DeveloperTools));
        Assert.Equal("Bearer t", handler.Authorization);
    }

    [Theory]
    [InlineData(401, ExitCodes.NotSignedIn)]
    [InlineData(403, ExitCodes.NotSignedIn)]
    [InlineData(500, ExitCodes.Unreachable)]
    public async Task ServerErrorsMapToExitCodes(int status, int expected)
    {
        var c = await CliHost.ConnectAsync(new LocalProfile { Root = _dir, ServerUrl = "http://flyknit.test", DeviceToken = "t" }, "cli",
            new ConfigHandler("{}", status), null, CancellationToken.None);
        Assert.Equal(expected, c.ExitCode);
    }

    private sealed class ConfigHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly int _status;
        public string Authorization { get; private set; } = "";

        public ConfigHandler(string body, int status = 200)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void ReadsTheEmployeesSettingsFile()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            {"serverUrl":"http://srv:8000","deviceToken":"plain-token","uiLanguage":"vi-VN","defaultPermission":"readonly",
             "deepThinking":true,"disabledSkills":["old"],"securityChoices":{"plan_guidance":false}}
            """);
        var p = LocalProfile.Load(_dir);
        Assert.True(p.IsRegistered);
        Assert.Equal("http://srv:8000", p.ServerUrl);
        Assert.Equal("plain-token", p.DeviceToken);
        Assert.Equal("vi-VN", p.UiLanguage);
        Assert.Equal("readonly", p.DefaultPermission);
        Assert.True(p.DeepThinking);
        Assert.Contains("old", p.DisabledSkills);
        Assert.Equal(false, p.SecurityChoices["plan_guidance"]);
        Assert.Equal(Path.Combine(_dir, "approval-rules.json"), p.ApprovalRulesFile);

        // DPAPI 包过的令牌：非 Windows 解不开就当没登录，不会拿密文去请求服务端
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"serverUrl":"http://srv","deviceToken":"dpapi:AAAA"}""");
        var wrapped = LocalProfile.Load(_dir);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(wrapped.IsRegistered);
        }
        Assert.False(LocalProfile.Load(Path.Combine(_dir, "nothing-here")).IsRegistered);
    }

    // ---------- 确认 ----------

    private static PolicyDecision Decision(CommandEffect effect, bool rule = true, bool alwaysAsk = false) =>
        PolicyDecision.Confirm("x") with
        {
            Effect = effect,
            Rule = rule ? new ApprovalCandidate("run_shell", "bash", "npm install", "npm", "", "npm") : null,
            AlwaysAsk = alwaysAsk,
        };

    [Fact]
    public void AllowWritesOnlyCoversWhatARuleCouldCover()
    {
        Assert.True(NonInteractiveConfirm.CanAutoApprove(Decision(CommandEffect.Write)));
        Assert.True(NonInteractiveConfirm.CanAutoApprove(Decision(CommandEffect.Unknown)));
        Assert.False(NonInteractiveConfirm.CanAutoApprove(Decision(CommandEffect.Destructive)));
        Assert.False(NonInteractiveConfirm.CanAutoApprove(Decision(CommandEffect.Write, rule: false)));
        Assert.False(NonInteractiveConfirm.CanAutoApprove(Decision(CommandEffect.Write, alwaysAsk: true)));
    }

    [Fact]
    public async Task WithoutAllowWritesEverythingIsDeclined()
    {
        var confirm = new NonInteractiveConfirm(allowWrites: false);
        var request = new ConfirmRequest { ConversationId = "c", Call = new ToolCall("1", "run_shell", "{}"), Summary = "npm install", Decision = Decision(CommandEffect.Write) };
        Assert.Equal(ConfirmChoice.Reject, await confirm.ConfirmAsync(request, CancellationToken.None));
        Assert.Single(confirm.Declined);

        var allowing = new NonInteractiveConfirm(allowWrites: true);
        Assert.Equal(ConfirmChoice.AllowOnce, await allowing.ConfirmAsync(request, CancellationToken.None));
        var destructive = new ConfirmRequest { ConversationId = "c", Call = new ToolCall("2", "run_shell", "{}"), Summary = "del a", Decision = Decision(CommandEffect.Destructive) };
        Assert.Equal(ConfirmChoice.Reject, await allowing.ConfirmAsync(destructive, CancellationToken.None));
        Assert.Contains("高危", allowing.Declined.Single().Reason);
    }

    // ---------- flyknit -p 端到端（模型是替身，工具和闸门都是真的） ----------

    [Fact]
    public async Task PrintRunsATaskAndReportsJson()
    {
        var gateway = new Scripted(
            Call("write_file", new { path = "out.txt", content = "你好" }),
            new ChatTurn { Content = "写好了 out.txt", ModelName = "m1", Usage = new TokenUsage(10, 5) });
        var audit = new ListAudit();
        var (code, json) = await Print(gateway, new[] { "-p", "写个文件" }, audit);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("completed", json["stop_reason"]!.GetValue<string>());
        Assert.Equal("写好了 out.txt", json["answer"]!.GetValue<string>());
        Assert.Equal("你好", File.ReadAllText(Path.Combine(_dir, "out.txt")));
        var call = json["tool_calls"]!.AsArray().Single()!;
        Assert.Equal("write_file", call["name"]!.GetValue<string>());
        Assert.True(call["ok"]!.GetValue<bool>());
        Assert.Empty(json["declined"]!.AsArray());
        Assert.Equal("workspace", json["permission"]!.GetValue<string>());

        // 审计标的是 cli，不是聊天窗口的 main
        Assert.NotEmpty(audit.Entries);
        Assert.All(audit.Entries, e => Assert.Equal("cli", e.Actor));
        // 用的是和员工端同一套提示词（有工作区），再加上无界面调用的说明
        var system = gateway.Requests[0].Messages[0].Content;
        Assert.Contains("无界面调用", system);
        Assert.Contains(_dir, system);
        Assert.Equal(Scenes.Agent, gateway.Requests[0].Scene);
    }

    [Fact]
    public async Task DestructiveCommandsAreNeverRunFromTheCli()
    {
        var victim = Path.Combine(_dir, "keep.txt");
        File.WriteAllText(victim, "x");
        var gateway = new Scripted(
            Call("run_shell", new { command = "del keep.txt" }),
            Call("delete_path", new { path = "keep.txt" }),
            new ChatTurn { Content = "删不了，需要员工确认" });
        var (code, json) = await Print(gateway, new[] { "-p", "删掉 keep.txt", "--allow-writes" });

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(File.Exists(victim));
        var declined = json["declined"]!.AsArray();
        Assert.Equal(2, declined.Count);
        Assert.All(declined, d => Assert.Contains("员工端", d!["reason"]!.GetValue<string>()));
        Assert.All(json["tool_calls"]!.AsArray(), c => Assert.Equal("rejected", c!["decision"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AllowWritesApprovesRememberableCommands()
    {
        // 默认：要确认的一律拒绝
        var (_, json) = await Print(new Scripted(Call("run_shell", new { command = "touch made.txt" }), new ChatTurn { Content = "好" }), new[] { "-p", "x" });
        Assert.Equal("rejected", json["tool_calls"]!.AsArray().Single()!["decision"]!.GetValue<string>());
        Assert.Single(json["declined"]!.AsArray());

        // --allow-writes：员工端能设成「以后自动执行」的放行一次，但不写规则
        (_, json) = await Print(new Scripted(Call("run_shell", new { command = "touch made.txt" }), new ChatTurn { Content = "好" }), new[] { "-p", "x", "--allow-writes" });
        Assert.Equal("approved", json["tool_calls"]!.AsArray().Single()!["decision"]!.GetValue<string>());
        Assert.Empty(json["declined"]!.AsArray());
        Assert.False(File.Exists(Path.Combine(_dir, "approval-rules.json")) && File.ReadAllText(Path.Combine(_dir, "approval-rules.json")).Contains("touch"));
    }

    [Fact]
    public async Task BlockedPatternsStillBlock()
    {
        var gateway = new Scripted(Call("run_shell", new { command = "format c: /q" }), new ChatTurn { Content = "被拦了" });
        var (_, json) = await Print(gateway, new[] { "-p", "格式化", "--allow-writes" });
        var call = json["tool_calls"]!.AsArray().Single()!;
        Assert.Equal("blocked", call["decision"]!.GetValue<string>());
        Assert.False(call["ok"]!.GetValue<bool>());
        Assert.Empty(json["declined"]!.AsArray()); // 拦截在确认之前，根本走不到确认
    }

    [Fact]
    public async Task ReadOnlyPermissionCannotWrite()
    {
        var gateway = new Scripted(Call("write_file", new { path = "x.txt", content = "1" }), new ChatTurn { Content = "只读，写不了" });
        var (_, json) = await Print(gateway, new[] { "-p", "写", "--permission", "readonly" });
        Assert.False(File.Exists(Path.Combine(_dir, "x.txt")));
        Assert.Equal("readonly", json["permission"]!.GetValue<string>());
        Assert.False(json["tool_calls"]!.AsArray().Single()!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WritingOutsideTheWorkspaceIsDeclined()
    {
        var outside = Path.Combine(Path.GetTempPath(), "flyknit-outside-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var gateway = new Scripted(Call("write_file", new { path = outside, content = "1" }), new ChatTurn { Content = "工作区外写不了" });
        var (_, json) = await Print(gateway, new[] { "-p", "写", "--allow-writes" });
        Assert.False(File.Exists(outside));
        Assert.False(json["tool_calls"]!.AsArray().Single()!["ok"]!.GetValue<bool>());
    }

    /// <summary>请求发出时就把消息序列化下来（之后历史列表还会继续变）。</summary>
    private sealed class Snapshotting : IChatGateway
    {
        public List<List<string>> Sent { get; } = new();

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Sent.Add(OpenAiSerializer.ToMessages(request.Messages).Select(m => m!.ToJsonString()).ToList());
            return Task.FromResult(new ChatTurn { Content = "好的" });
        }
    }

    [Fact]
    public async Task SecondTurnStartsWithExactlyWhatTheFirstTurnSent()
    {
        // 提示词缓存按前缀命中：第二轮请求的开头必须和第一轮发过的一字不差（系统提示词、第一句话连同它的参考资料）
        var gateway = new Snapshotting();
        var session = new HeadlessSession(new HeadlessEnvironment { Gateway = gateway, Security = Security(dev: false) }, _dir, PermissionMode.Workspace);
        await session.PromptAsync("先看看目录", new NonInteractiveConfirm(false), new NullObserver(), CancellationToken.None);
        await session.PromptAsync("再说说 README 写了什么，完全不同的问题", new NonInteractiveConfirm(false), new NullObserver(), CancellationToken.None);

        var first = gateway.Sent[0];
        var second = gateway.Sent[1];
        Assert.Equal(first, second.Take(first.Count));
        static string Text(string json) => JsonNode.Parse(json)!["content"]!.GetValue<string>();
        Assert.DoesNotContain("当前时间", Text(first[0]));            // 系统提示词里没有每轮变的东西
        Assert.Contains("<turn-context", Text(first[1]));              // 参考资料跟着第一句话
        Assert.Contains("<turn-context", Text(second[^1]));            // 第二句话有自己的
        Assert.Equal(2, session.History.Count(m => m.Role == ChatRole.User && m.TurnContext is not null));
    }

    [Fact]
    public async Task DeveloperToolsSwitchTheScene()
    {
        var gateway = new Scripted(new ChatTurn { Content = "好" });
        var recording = new RecordingGateway(gateway);
        var env = new HeadlessEnvironment { Gateway = recording, Security = Security(dev: true) };
        var session = new HeadlessSession(env, _dir, PermissionMode.Workspace);
        Assert.Contains(session.BuildTools().All, t => t.Name == "git_status");
        Assert.DoesNotContain(session.BuildTools().All, t => HeadlessSession.Excluded.Contains(t.Name));
        await session.PromptAsync("看看", new NonInteractiveConfirm(false), new NullObserver(), CancellationToken.None);
        Assert.Equal(Scenes.Code, gateway.Requests[0].Scene);

        var plain = new HeadlessSession(new HeadlessEnvironment { Gateway = gateway, Security = Security(dev: false) }, _dir, PermissionMode.Workspace);
        Assert.DoesNotContain(plain.BuildTools().All, t => t.Name == "git_status");
    }

    [Theory]
    [InlineData(429, ExitCodes.QuotaExceeded)]
    [InlineData(401, ExitCodes.NotSignedIn)]
    [InlineData(500, ExitCodes.NotCompleted)]
    public async Task ModelErrorsMapToExitCodes(int status, int expected)
    {
        var (code, json) = await Print(new Throwing(new GatewayException("出错了", status)), new[] { "-p", "x" });
        Assert.Equal(expected, code);
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Equal("failed", json["stop_reason"]!.GetValue<string>());
        Assert.Contains("出错了", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task NetworkErrorsMeanUnreachable()
    {
        var (code, _) = await Print(new Throwing(new GatewayException("连不上") { IsNetworkError = true }), new[] { "-p", "x" });
        Assert.Equal(ExitCodes.Unreachable, code);
    }

    [Fact]
    public async Task TimeoutStopsTheRun()
    {
        var gateway = new Scripted(new ChatTurn { Content = "慢" }) { Delay = ct => Task.Delay(TimeSpan.FromSeconds(30), ct) };
        var (code, json) = await Print(gateway, new[] { "-p", "x", "--timeout", "1" });
        Assert.Equal(ExitCodes.Timeout, code);
        Assert.False(json["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TextAndStreamFormats()
    {
        var output = new StringWriter();
        var (o, _) = CliOptions.Parse(new[] { "-p", "x", "--format", "stream-json", "-w", _dir });
        var gateway = new Scripted(Call("list_dir", new { path = "." }), new ChatTurn { Content = "列好了" });
        await PrintCommand.RunAsync(o!, Env(gateway), null, output, PermissionMode.Workspace, CancellationToken.None);
        var events = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!["type"]!.GetValue<string>()).ToList();
        Assert.Contains("tool_started", events);
        Assert.Contains("tool_finished", events);
        Assert.Contains("text", events);
        Assert.Equal("result", events[^1]);

        output = new StringWriter();
        (o, _) = CliOptions.Parse(new[] { "-p", "x", "--format", "text", "-w", _dir });
        await PrintCommand.RunAsync(o!, Env(new Scripted(new ChatTurn { Content = "只要回答" })), null, output, PermissionMode.Workspace, CancellationToken.None);
        Assert.Equal("只要回答", output.ToString().Trim());
    }

    [Fact]
    public async Task RedirectedJsonIsPureAscii()
    {
        // 输出给程序读时：中文转义成 \uXXXX，GBK 的 PowerShell 读进去也不会乱码，解析出来还是原文
        foreach (var format in new[] { "json", "stream-json" })
        {
            var (o, _) = CliOptions.Parse(new[] { "-p", "x", "--format", format, "-w", _dir });
            o = new CliOptions { Command = o!.Command, Prompt = o.Prompt, Format = o.Format, Workspace = o.Workspace, AsciiJson = true };
            var output = new StringWriter();
            await PrintCommand.RunAsync(o, Env(new Scripted(new ChatTurn { Content = "你好，Xin chào" })), null, output, PermissionMode.Workspace, CancellationToken.None);
            var text = output.ToString();
            Assert.All(text, c => Assert.True(c < 128, $"{format} 里有非 ASCII 字符：{c}"));
            var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
            var result = format == "json" ? JsonNode.Parse(text)! : JsonNode.Parse(last)!;
            Assert.Equal("你好，Xin chào", result["answer"]!.GetValue<string>());
        }

        // 直接显示在窗口里：原样输出，人看得懂
        var (plain, _) = CliOptions.Parse(new[] { "-p", "x", "-w", _dir });
        var shown = new StringWriter();
        await PrintCommand.RunAsync(plain!, Env(new Scripted(new ChatTurn { Content = "你好" })), null, shown, PermissionMode.Workspace, CancellationToken.None);
        Assert.Contains("你好", shown.ToString());
    }

    [Fact]
    public async Task MissingWorkspaceIsAUsageError()
    {
        var (o, _) = CliOptions.Parse(new[] { "-p", "x", "-w", Path.Combine(_dir, "nope") });
        var output = new StringWriter();
        var code = await PrintCommand.RunAsync(o!, Env(new Scripted()), null, output, PermissionMode.Workspace, CancellationToken.None);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("工作区不存在", output.ToString());
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

    // ---------- ACP ----------

    /// <summary>IDE 一侧：往服务的标准输入写消息、读它的标准输出。</summary>
    private sealed class Ide : IAsyncDisposable
    {
        private readonly Channel<string?> _toServer = Channel.CreateUnbounded<string?>();
        private readonly Channel<string> _fromServer = Channel.CreateUnbounded<string>();
        private readonly Task _run;
        private int _id;

        public Ide(HeadlessEnvironment? env, PermissionMode permission = PermissionMode.Workspace, string unavailable = "", AcpSessionStore? store = null)
        {
            var server = new AcpServer(env, permission, new ChannelReaderText(_toServer.Reader), new ChannelWriterText(_fromServer.Writer),
                version: "9.9.9", unavailable: unavailable, store: store);
            _run = server.RunAsync(CancellationToken.None);
        }

        public List<JsonObject> Updates { get; } = new();

        /// <summary>IDE 收到确认请求时怎么回（返回 null 表示回 cancelled）。</summary>
        public Func<JsonObject, string?> OnPermission { get; set; } = _ => "allow_once";
        public List<JsonObject> PermissionRequests { get; } = new();

        public async Task Send(JsonObject message) => await _toServer.Writer.WriteAsync(message.ToJsonString());

        public Task Notify(string method, JsonObject p) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p });

        /// <summary>发请求，一边处理服务发来的更新和确认请求，一边等这个请求的回复。</summary>
        public async Task<JsonObject> Request(string method, JsonObject p, TimeSpan? timeout = null)
        {
            var id = ++_id;
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p });
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
            while (true)
            {
                var line = await _fromServer.Reader.ReadAsync(cts.Token);
                var msg = JsonNode.Parse(line)!.AsObject();
                Assert.Equal("2.0", msg["jsonrpc"]!.GetValue<string>());
                if (msg["method"]?.GetValue<string>() == "session/update")
                {
                    Updates.Add(msg["params"]!["update"]!.AsObject());
                }
                else if (msg["method"]?.GetValue<string>() == "session/request_permission")
                {
                    var req = msg["params"]!.AsObject();
                    PermissionRequests.Add(req);
                    var choice = OnPermission(req);
                    var outcome = choice is null
                        ? new JsonObject { ["outcome"] = "cancelled" }
                        : new JsonObject { ["outcome"] = "selected", ["optionId"] = choice };
                    await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone(), ["result"] = new JsonObject { ["outcome"] = outcome } });
                }
                else if (msg["id"]?.GetValue<int>() == id)
                {
                    return msg;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _toServer.Writer.TryWrite(null);
            await _run.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    private sealed class ChannelReaderText : TextReader
    {
        private readonly ChannelReader<string?> _reader;
        public ChannelReaderText(ChannelReader<string?> reader) => _reader = reader;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct) => await _reader.ReadAsync(ct);
    }

    private sealed class ChannelWriterText : TextWriter
    {
        private readonly ChannelWriter<string> _writer;
        public ChannelWriterText(ChannelWriter<string> writer) => _writer = writer;
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            Assert.DoesNotContain('\n', value ?? ""); // 一条消息一行，消息里不能有换行
            _writer.TryWrite(value ?? "");
            return Task.CompletedTask;
        }
    }

    private async Task<string> NewSession(Ide ide)
    {
        var r = await ide.Request("session/new", new JsonObject { ["cwd"] = _dir, ["mcpServers"] = new JsonArray() });
        return r["result"]!["sessionId"]!.GetValue<string>();
    }

    private static JsonObject Prompt(string sid, string text) => new()
    {
        ["sessionId"] = sid,
        ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };

    [Fact]
    public async Task AcpBatchesStreamedChunks()
    {
        // 模型一个字一个字吐：以前每个字一条 session/update，IDE 每条都要重新渲染；现在攒一下再发，内容和顺序不变
        await using var ide = new Ide(Env(new Dribbling(), new ListAudit(), actor: "ide"));
        await ide.Request("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
        var sid = await NewSession(ide);
        var done = await ide.Request("session/prompt", Prompt(sid, "想一想再答"));
        Assert.Equal("end_turn", done["result"]!["stopReason"]!.GetValue<string>());

        var chunks = ide.Updates
            .Where(u => u["sessionUpdate"]!.GetValue<string>() is "agent_thought_chunk" or "agent_message_chunk")
            .Select(u => (Kind: u["sessionUpdate"]!.GetValue<string>(), Text: u["content"]!["text"]!.GetValue<string>()))
            .ToList();
        Assert.True(chunks.Count <= 6, $"发了 {chunks.Count} 条片段，没有合并");
        Assert.Equal(new string('想', Dribbling.Pieces), string.Concat(chunks.Where(c => c.Kind == "agent_thought_chunk").Select(c => c.Text)));
        Assert.Equal(new string('答', Dribbling.Pieces), string.Concat(chunks.Where(c => c.Kind == "agent_message_chunk").Select(c => c.Text)));
        // 先思考后回答，不交错
        var firstAnswer = chunks.FindIndex(c => c.Kind == "agent_message_chunk");
        Assert.True(firstAnswer > 0);
        Assert.DoesNotContain(chunks.Skip(firstAnswer), c => c.Kind == "agent_thought_chunk");
    }

    // ---------- IDE 会话保存、列表、重新打开 ----------

    [Fact]
    public async Task AcpSessionsAreSavedListedAndReopened()
    {
        var store = new AcpSessionStore(Path.Combine(Path.GetTempPath(), "flyknit-ide-" + Guid.NewGuid().ToString("N")[..8]));
        string sid;
        await using (var ide = new Ide(Env(new Scripted(
            new ChatTurn { ToolCalls = new[] { new ToolCall("t1", "write_file", JsonSerializer.Serialize(new { path = "a.txt", content = "1" })) } },
            new ChatTurn { Content = "写好了" }), new ListAudit(), actor: "ide"), store: store))
        {
            var init = await ide.Request("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
            Assert.True(init["result"]!["agentCapabilities"]!["loadSession"]!.GetValue<bool>());
            Assert.NotNull(init["result"]!["agentCapabilities"]!["sessionCapabilities"]!["list"]);
            sid = await NewSession(ide);
            await ide.Request("session/prompt", Prompt(sid, "写 a.txt\n内容是 1"));

            var list = (await ide.Request("session/list", new JsonObject { ["cwd"] = _dir }))["result"]!["sessions"]!.AsArray();
            var row = Assert.Single(list)!;
            Assert.Equal(sid, row["sessionId"]!.GetValue<string>());
            Assert.Equal("写 a.txt 内容是 1", row["title"]!.GetValue<string>());
            Assert.Equal(Path.GetFullPath(_dir), row["cwd"]!.GetValue<string>());
            Assert.True(DateTimeOffset.TryParse(row["updatedAt"]!.GetValue<string>(), out _));
            // 别的项目的会话不列
            Assert.Empty((await ide.Request("session/list", new JsonObject { ["cwd"] = Path.GetTempPath() }))["result"]!["sessions"]!.AsArray());
        }

        // IDE 关掉重开：新进程从会话列表打开，历史推给 IDE 显示，接着聊时模型看得到之前的内容
        var gateway = new Scripted(new ChatTurn { Content = "接着做完了" });
        await using (var ide = new Ide(Env(gateway, new ListAudit(), actor: "ide"), store: store))
        {
            await ide.Request("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
            var loaded = await ide.Request("session/load", new JsonObject { ["sessionId"] = sid, ["cwd"] = _dir, ["mcpServers"] = new JsonArray() });
            Assert.Null(loaded["error"]);
            var kinds = ide.Updates.Select(u => u["sessionUpdate"]!.GetValue<string>()).ToList();
            Assert.Equal(new[] { "user_message_chunk", "tool_call", "agent_message_chunk" }, kinds);
            Assert.Equal("t1", ide.Updates[1]["toolCallId"]!.GetValue<string>());
            Assert.Equal("completed", ide.Updates[1]["status"]!.GetValue<string>());
            Assert.Equal("写好了", ide.Updates[2]["content"]!["text"]!.GetValue<string>());

            var done = await ide.Request("session/prompt", Prompt(sid, "再检查一下"));
            Assert.Equal("end_turn", done["result"]!["stopReason"]!.GetValue<string>());
            var sent = gateway.Requests.Single().Messages;
            Assert.Contains(sent, m => m.Role == ChatRole.User && m.Content.StartsWith("写 a.txt"));
            Assert.Contains(sent, m => m.Role == ChatRole.Tool && m.ToolCallId == "t1");
            Assert.Equal("再检查一下", sent.Last(m => m.Role == ChatRole.User).Content);
        }
        var saved = store.Load(sid)!;
        Assert.Equal("写 a.txt 内容是 1", saved.Title); // 标题还是第一句
        Assert.Equal("接着做完了", saved.Messages.Last().Content);
    }

    [Fact]
    public async Task AcpLoadRejectsUnknownOrBadSessionIds()
    {
        var store = new AcpSessionStore(Path.Combine(Path.GetTempPath(), "flyknit-ide-" + Guid.NewGuid().ToString("N")[..8]));
        await using var ide = new Ide(Env(new Scripted(), new ListAudit(), actor: "ide"), store: store);
        await ide.Request("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
        foreach (var id in new[] { "acp-0123456789ab", "../../settings", "acp-../../x" })
        {
            var r = await ide.Request("session/load", new JsonObject { ["sessionId"] = id, ["cwd"] = _dir, ["mcpServers"] = new JsonArray() });
            Assert.NotNull(r["error"]);
        }
        Assert.False(AcpSessionStore.IsValidId("acp-0123456789AB/.."));
        Assert.Null(store.Load("../approval-rules"));
    }

    [Fact]
    public async Task WithoutAStoreSessionListIsNotOffered()
    {
        await using var ide = new Ide(Env(new Scripted(), new ListAudit(), actor: "ide"));
        var init = await ide.Request("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
        Assert.Null(init["result"]!["agentCapabilities"]!["sessionCapabilities"]!["list"]);
        Assert.NotNull((await ide.Request("session/list", new JsonObject()))["error"]);
    }

    [Fact]
    public void RestoredHistoryGetsMissingToolResults()
    {
        // 上次在工具执行到一半时进程没了：发出的调用没有结果，直接接着聊会被模型服务拒绝
        var session = new HeadlessSession(Env(new Scripted(), new ListAudit()), _dir, PermissionMode.Workspace, "acp-0123456789ab");
        var call = new ToolCall("c9", "run_shell", "{}");
        session.Restore(new[] { ChatMessage.User("跑测试"), ChatMessage.Assistant("", new[] { call }) }, Array.Empty<PlanItem>(), new[] { "grep" });
        Assert.Equal(3, session.History.Count);
        Assert.Equal("c9", session.History[2].ToolCallId);
        Assert.Contains("grep", session.LoadedTools);
    }

    [Fact]
    public void OldSessionsArePruned()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flyknit-ide-" + Guid.NewGuid().ToString("N")[..8]);
        var now = DateTimeOffset.Now;
        var store = new AcpSessionStore(dir, () => now);
        var old = new HeadlessSession(Env(new Scripted(), new ListAudit()), _dir, PermissionMode.Workspace, "acp-00000000000a");
        old.Restore(new[] { ChatMessage.User("旧的") }, Array.Empty<PlanItem>(), Array.Empty<string>());
        Assert.True(store.Save(old, "旧的", now));

        now += AcpSessionStore.KeepFor + TimeSpan.FromDays(1);
        var fresh = new HeadlessSession(Env(new Scripted(), new ListAudit()), _dir, PermissionMode.Workspace, "acp-00000000000b");
        fresh.Restore(new[] { ChatMessage.User("新的") }, Array.Empty<PlanItem>(), Array.Empty<string>());
        Assert.True(store.Save(fresh, "新的", now));

        Assert.Equal(new[] { "acp-00000000000b" }, store.List().Select(s => s.Id));
    }

    [Fact]
    public async Task AcpInitializeAndPromptRoundTrip()
    {
        var gateway = new Scripted(
            new ChatTurn { ToolCalls = new[] { new ToolCall("t1", "write_file", JsonSerializer.Serialize(new { path = "a.txt", content = "1" })) } },
            new ChatTurn { Content = "写好了" });
        var audit = new ListAudit();
        await using var ide = new Ide(Env(gateway, audit, actor: "ide"));

        var init = await ide.Request("initialize", new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });
        Assert.Equal(1, init["result"]!["protocolVersion"]!.GetValue<int>());
        Assert.Equal("flyknit", init["result"]!["agentInfo"]!["name"]!.GetValue<string>());
        Assert.False(init["result"]!["agentCapabilities"]!["loadSession"]!.GetValue<bool>());

        var sid = await NewSession(ide);
        var done = await ide.Request("session/prompt", Prompt(sid, "写 a.txt"));
        Assert.Equal("end_turn", done["result"]!["stopReason"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_dir, "a.txt")));

        var kinds = ide.Updates.Select(u => u["sessionUpdate"]!.GetValue<string>()).ToList();
        Assert.Contains("tool_call", kinds);
        Assert.Contains("tool_call_update", kinds);
        Assert.Equal("agent_message_chunk", kinds[^1]);
        var started = ide.Updates.First(u => u["sessionUpdate"]!.GetValue<string>() == "tool_call");
        Assert.Equal("t1", started["toolCallId"]!.GetValue<string>());
        Assert.Equal("edit", started["kind"]!.GetValue<string>());
        var finished = ide.Updates.First(u => u["sessionUpdate"]!.GetValue<string>() == "tool_call_update");
        Assert.Equal("completed", finished["status"]!.GetValue<string>());
        Assert.All(audit.Entries, e => Assert.Equal("ide", e.Actor));

        // 多轮：第二句带着第一轮的历史
        await ide.Request("session/prompt", Prompt(sid, "再看看"));
        Assert.True(gateway.Requests.Last().Messages.Count > 3);
    }

    [Fact]
    public async Task AcpPermissionGoesToTheIde()
    {
        var gateway = new Scripted(
            Call("run_shell", new { command = "del keep.txt" }, "t1"),
            new ChatTurn { Content = "好" });
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "x");
        await using var ide = new Ide(Env(gateway));
        var sid = await NewSession(ide);
        // IDE 回「总是允许」——高危操作根本没给这个选项，按拒绝处理
        ide.OnPermission = _ => "allow_always";
        var r = await ide.Request("session/prompt", Prompt(sid, "删"));
        Assert.Equal("end_turn", r["result"]!["stopReason"]!.GetValue<string>());

        var request = ide.PermissionRequests.Single();
        Assert.Equal(sid, request["sessionId"]!.GetValue<string>());
        Assert.Equal("t1", request["toolCall"]!["toolCallId"]!.GetValue<string>());
        Assert.Equal("execute", request["toolCall"]!["kind"]!.GetValue<string>());
        var options = request["options"]!.AsArray().Select(o => o!["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "allow_once", "reject_once" }, options);
        Assert.True(File.Exists(Path.Combine(_dir, "keep.txt")));
        Assert.Equal("failed", ide.Updates.Last(u => u["sessionUpdate"]!.GetValue<string>() == "tool_call_update")["status"]!.GetValue<string>());
    }

    [Fact]
    public void AcpOffersAlwaysOnlyForRememberableOperations()
    {
        var write = Decision(CommandEffect.Write);
        Assert.Contains(AcpServer.AcpConfirm.Options(write), o => o!["kind"]!.GetValue<string>() == "allow_always");
        Assert.DoesNotContain(AcpServer.AcpConfirm.Options(Decision(CommandEffect.Destructive)), o => o!["kind"]!.GetValue<string>() == "allow_always");
        Assert.DoesNotContain(AcpServer.AcpConfirm.Options(Decision(CommandEffect.Write, alwaysAsk: true)), o => o!["kind"]!.GetValue<string>() == "allow_always");

        static JsonObject Selected(string id) => new() { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = id } };
        Assert.Equal(ConfirmChoice.AllowAlways, AcpServer.AcpConfirm.Map(Selected("allow_always"), write));
        Assert.Equal(ConfirmChoice.AllowOnce, AcpServer.AcpConfirm.Map(Selected("allow_once"), write));
        Assert.Equal(ConfirmChoice.Reject, AcpServer.AcpConfirm.Map(Selected("reject_once"), write));
        Assert.Equal(ConfirmChoice.Reject, AcpServer.AcpConfirm.Map(Selected("something-else"), write));
        Assert.Equal(ConfirmChoice.Reject, AcpServer.AcpConfirm.Map(Selected("allow_always"), Decision(CommandEffect.Destructive)));
        Assert.Equal(ConfirmChoice.Reject, AcpServer.AcpConfirm.Map(new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }, write));
        Assert.Equal(ConfirmChoice.Reject, AcpServer.AcpConfirm.Map(null, write));
    }

    [Fact]
    public async Task AcpAllowOnceRunsTheCommand()
    {
        // 工作区权限下认不出的命令要确认：IDE 允许一次就执行；这类命令能设「以后自动执行」，所以有「总是允许」
        var gateway = new Scripted(Call("run_shell", new { command = "touch made.txt" }, "t1"), new ChatTurn { Content = "好" });
        await using var ide = new Ide(Env(gateway));
        var sid = await NewSession(ide);
        ide.OnPermission = _ => "allow_once";
        await ide.Request("session/prompt", Prompt(sid, "写"), TimeSpan.FromSeconds(60));
        var request = Assert.Single(ide.PermissionRequests);
        Assert.Contains(request["options"]!.AsArray(), o => o!["kind"]!.GetValue<string>() == "allow_always");
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.Exists(Path.Combine(_dir, "made.txt")));
        }
    }

    [Fact]
    public async Task AcpCancelStopsTheTurn()
    {
        var started = new TaskCompletionSource();
        var gateway = new Scripted(new ChatTurn { Content = "慢" })
        {
            Delay = async ct =>
            {
                started.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            },
        };
        await using var ide = new Ide(Env(gateway));
        var sid = await NewSession(ide);
        var prompt = ide.Request("session/prompt", Prompt(sid, "慢慢做"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ide.Notify("session/cancel", new JsonObject { ["sessionId"] = sid });
        var r = await prompt;
        Assert.Equal("cancelled", r["result"]!["stopReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AcpErrors()
    {
        await using var ide = new Ide(Env(new Scripted()));
        var unknown = await ide.Request("session/set_model", new JsonObject());
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
        var badCwd = await ide.Request("session/new", new JsonObject { ["cwd"] = "relative/dir", ["mcpServers"] = new JsonArray() });
        Assert.Equal(-32602, badCwd["error"]!["code"]!.GetValue<int>());
        var noSession = await ide.Request("session/prompt", Prompt("nope", "x"));
        Assert.Equal(-32602, noSession["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task AcpSurvivesMalformedMessages()
    {
        await using var ide = new Ide(Env(new Scripted()));
        await ide.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 99, ["method"] = 123 });
        await ide.Notify("session/cancel", new JsonObject { ["sessionId"] = 5 });
        // 服务还活着，照常回答
        var init = await ide.Request("initialize", new JsonObject { ["protocolVersion"] = 1 });
        Assert.Equal(1, init["result"]!["protocolVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task AcpExplainsWhyItCannotStart()
    {
        await using var ide = new Ide(null, unavailable: "IT 没有开启「命令行 / IDE 接入」");
        var init = await ide.Request("initialize", new JsonObject { ["protocolVersion"] = 1 });
        Assert.NotNull(init["result"]);
        var r = await ide.Request("session/new", new JsonObject { ["cwd"] = _dir, ["mcpServers"] = new JsonArray() });
        Assert.Contains("命令行 / IDE 接入", r["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void AcpPromptBlocksBecomeText()
    {
        var file = Path.Combine(_dir, "a.cs");
        var text = AcpServer.PromptText(new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "看看这个" },
            new JsonObject { ["type"] = "resource_link", ["uri"] = new Uri(file).AbsoluteUri, ["name"] = "a.cs" },
            new JsonObject { ["type"] = "resource", ["resource"] = new JsonObject { ["uri"] = new Uri(file).AbsoluteUri, ["text"] = "class A {}" } },
            new JsonObject { ["type"] = "image", ["data"] = "xx", ["mimeType"] = "image/png" }));
        Assert.Contains("看看这个", text);
        Assert.Contains($"[文件：{file}]", text);
        Assert.Contains("class A {}", text);
        Assert.DoesNotContain("xx", text);
    }
}
