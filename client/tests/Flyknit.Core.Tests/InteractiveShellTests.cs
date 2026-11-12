using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Headless;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>flyknit 交互模式：多轮对话、终端里确认、斜杠命令、接着以前的会话、Ctrl+C。模型是替身，工具和闸门都是真的。</summary>
public sealed class InteractiveShellTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-shell").FullName;
    private readonly string _storeDir = Directory.CreateTempSubdirectory("flyknit-shell-store").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _dir, _storeDir })
        {
            try
            {
                Directory.Delete(d, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class Scripted : IChatGateway
    {
        private readonly Queue<ChatTurn> _turns;
        public List<ChatRequest> Requests { get; } = new();
        public Func<CancellationToken, Task>? Delay { get; set; }

        public Scripted(params ChatTurn[] turns) => _turns = new(turns);

        public async Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            if (Delay is not null)
            {
                await Delay(ct);
            }
            var turn = _turns.Count > 0 ? _turns.Dequeue() : new ChatTurn { Content = "完成" };
            if (turn.Content.Length > 0)
            {
                sink?.OnContent(turn.Content);
            }
            return turn;
        }
    }

    private static ChatTurn Call(string name, object args) => new()
    {
        ToolCalls = new[] { new ToolCall("call_" + Guid.NewGuid().ToString("N")[..6], name, JsonSerializer.Serialize(args)) },
    };

    private HeadlessEnvironment Env(IChatGateway gateway) => new()
    {
        Gateway = gateway,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.CliAccess] = new() { Value = true },
            [SecuritySettings.DeleteProtection] = new() { Value = false },
        }),
        Approvals = new ApprovalStore(Path.Combine(_storeDir, "approval-rules.json")),
    };

    private AcpSessionStore Store() => new(Path.Combine(_storeDir, "sessions"));

    private async Task<(string Output, InteractiveShell Shell)> Run(IChatGateway gateway, string input, string? first = null, string? resume = null)
    {
        var output = new StringWriter();
        var shell = new InteractiveShell(Env(gateway), _dir, PermissionMode.Workspace, new StringReader(input), output, store: Store(), version: "9.9.9");
        var code = await shell.RunAsync(first, resume, CancellationToken.None);
        Assert.Equal(ExitCodes.Ok, code);
        return (output.ToString(), shell);
    }

    [Fact]
    public async Task ChatKeepsHistoryAcrossTurnsAndSavesTheSession()
    {
        var gateway = new Scripted(new ChatTurn { Content = "目录里有 3 个文件" }, new ChatTurn { Content = "README 写的是安装说明" });
        var (output, _) = await Run(gateway, "README 写了什么\n/exit\n", first: "先看看目录");

        Assert.Contains("FlyknitBuddy 9.9.9", output);
        Assert.Contains("目录里有 3 个文件", output);
        Assert.Contains("README 写的是安装说明", output);
        Assert.Contains("· ", output);                 // 每轮的页脚
        Assert.Contains("再见", output);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Contains(gateway.Requests[1].Messages, m => m.Role == ChatRole.User && m.Content == "先看看目录");
        var saved = Assert.Single(Store().List(_dir));
        Assert.Equal("先看看目录", saved.Title);
    }

    [Fact]
    public async Task MultiLineInput()
    {
        var gateway = new Scripted(new ChatTurn { Content = "好" }, new ChatTurn { Content = "好" });
        await Run(gateway, "第一行\\\n第二行\n\"\"\"\n甲\n乙\n\"\"\"\n/exit\n");
        Assert.Equal("第一行\n第二行", gateway.Requests[0].Messages.Last(m => m.Role == ChatRole.User).Content);
        Assert.Equal("甲\n乙", gateway.Requests[1].Messages.Last(m => m.Role == ChatRole.User).Content);
    }

    [Fact]
    public async Task WritesAreConfirmedInTheTerminal()
    {
        var gateway = new Scripted(Call("run_shell", new { command = "touch made.txt" }), new ChatTurn { Content = "没做" });
        var (output, _) = await Run(gateway, "建个文件\nn\n/exit\n");
        Assert.Contains("需要确认", output);
        Assert.Contains("[a] 以后自动", output);   // 普通写入：员工端也能设成自动执行
        Assert.Contains("已拒绝", output);
        Assert.False(File.Exists(Path.Combine(_dir, "made.txt")));

        var allowed = new Scripted(Call("run_shell", new { command = "touch made.txt" }), new ChatTurn { Content = "好了" });
        (output, _) = await Run(allowed, "建个文件\ny\n/exit\n");
        Assert.Contains("已允许这一次", output);
    }

    [Fact]
    public async Task DestructiveOperationsNeverOfferAlways()
    {
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "x");
        var gateway = new Scripted(Call("delete_path", new { path = "keep.txt" }), new ChatTurn { Content = "没删" });
        // 员工输入 a（以后自动）：高危操作没有这个选项，再问一次；回车（空）按拒绝
        var (output, _) = await Run(gateway, "删掉 keep.txt\na\n\n/exit\n");
        Assert.Contains("需要确认", output);
        Assert.DoesNotContain("[a] 以后自动", output);
        Assert.Contains("已拒绝", output);
        Assert.True(File.Exists(Path.Combine(_dir, "keep.txt")));
    }

    [Fact]
    public async Task EndOfInputDuringAConfirmationRejects()
    {
        var gateway = new Scripted(Call("run_shell", new { command = "touch made.txt" }), new ChatTurn { Content = "没做" });
        await Run(gateway, "建个文件\n");
        Assert.False(File.Exists(Path.Combine(_dir, "made.txt")));
    }

    [Fact]
    public async Task SlashCommands()
    {
        var (output, _) = await Run(new Scripted(), "/help\n/usage\n/plan\n/bogus\n/new\n/exit\n");
        Assert.Contains("/resume", output);
        Assert.Contains("这个会话：输入", output);
        Assert.Contains("现在没有任务计划", output);
        Assert.Contains("不认识的命令 /bogus", output);
        Assert.Contains("已开始新会话", output);
    }

    [Fact]
    public async Task ResumePicksUpAnEarlierSession()
    {
        await Run(new Scripted(new ChatTurn { Content = "旧回答" }), "/exit\n", first: "旧问题");

        // /resume 列出来，选第 1 个，接着聊
        var gateway = new Scripted(new ChatTurn { Content = "接着做完了" });
        var (output, shell) = await Run(gateway, "/resume\n1\n接着做\n/exit\n");
        Assert.Contains("1. 旧问题", output);
        Assert.Contains("已打开「旧问题」", output);
        Assert.Contains("AI：旧回答", output);
        Assert.Contains(gateway.Requests.Single().Messages, m => m.Role == ChatRole.User && m.Content == "旧问题");
        Assert.Single(Store().List(_dir));          // 接着的是同一个会话，没有另存一份

        // flyknit -c：直接接着最近的一个
        var again = new Scripted(new ChatTurn { Content = "好" });
        (output, _) = await Run(again, "再看看\n/exit\n", resume: "continue");
        Assert.Contains("已打开「旧问题」", output);
        Assert.Contains(again.Requests.Single().Messages, m => m.Content == "接着做");
    }

    [Fact]
    public async Task CtrlCStopsTheRunningTurnButNotTheShell()
    {
        var started = new TaskCompletionSource();
        var gateway = new Scripted(new ChatTurn { Content = "不会到这里" }, new ChatTurn { Content = "第二句的回答" })
        {
            Delay = async ct =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        var output = new StringWriter();
        var input = new BlockingReader("跑个很久的任务");
        var shell = new InteractiveShell(Env(gateway), _dir, PermissionMode.Workspace, input, output, store: Store());
        var run = shell.RunAsync(null, null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(shell.Interrupt());          // 停下这一轮，不退出
        gateway.Delay = null;
        input.Push("/exit");
        Assert.Equal(ExitCodes.Ok, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("已停止", output.ToString());
    }

    [Fact]
    public async Task ColorsAndTheWaitingHintOnlyOnARealTerminal()
    {
        var gateway = new Scripted(Call("list_dir", new { path = "." }), new ChatTurn { Content = "看完了" });
        var output = new StringWriter();
        var shell = new InteractiveShell(Env(gateway), _dir, PermissionMode.Workspace, new StringReader("看看\n/exit\n"), output,
            style: new ConsoleStyle(true), store: Store());
        await shell.RunAsync(null, null, CancellationToken.None);
        var text = output.ToString();
        Assert.Contains("\u001b[", text, StringComparison.Ordinal);
        Assert.Contains("思考中", text);
        Assert.Contains("\r\u001b[K", text, StringComparison.Ordinal);     // 占位在下一次输出前擦掉

        // 重定向到文件（没有颜色）：不写占位、不写转义
        var (plain, _) = await Run(new Scripted(Call("list_dir", new { path = "." }), new ChatTurn { Content = "看完了" }), "看看\n/exit\n");
        Assert.False(plain.Contains('\u001b'), "重定向时不该有转义字符");
        Assert.DoesNotContain("思考中", plain);
    }

    [Fact]
    public void BannerDrawsTheWordmarkWhenTheTerminalIsWideEnough()
    {
        var info = new[] { "工作区 D:\\日报", "权限：工作区内修改    开发者工具：关    深度思考：关" };
        var wide = ConsoleBanner.Render(new ConsoleStyle(true), 120, "9.9.9", info, "提示");
        Assert.Contains("\u001b[38;2;", wide, StringComparison.Ordinal);     // 真彩色渐变
        var plainText = System.Text.RegularExpressions.Regex.Replace(wide, "\u001b\\[[0-9;]*m", "");
        Assert.Contains("███████╗", plainText);                              // 大字
        Assert.Contains(">_ FlyknitBuddy (9.9.9)", plainText);
        Assert.True(ConsoleBanner.ArtWidth <= 110, $"字标太宽：{ConsoleBanner.ArtWidth}");
        // 框的每一行一样宽（中文按两列算），框线才是直的
        var boxRows = plainText.Split('\n').Where(l => l.TrimStart().StartsWith('│')).Select(ConsoleBanner.DisplayWidth).Distinct();
        Assert.Single(boxRows);

        // 终端窄：不画大字，框也不超出终端
        var narrow = ConsoleBanner.Render(new ConsoleStyle(true), 60, "9.9.9", new[] { "工作区 " + new string('长', 80) }, "提示");
        var narrowText = System.Text.RegularExpressions.Regex.Replace(narrow, "\u001b\\[[0-9;]*m", "");
        Assert.DoesNotContain("███████╗", narrowText);
        Assert.All(narrowText.Split('\n'), l => Assert.True(ConsoleBanner.DisplayWidth(l) <= 60, l));

        // 没有颜色（重定向）：不画大字，信息照常
        var plain = ConsoleBanner.Render(ConsoleStyle.Plain, 120, "9.9.9", info, "提示");
        Assert.DoesNotContain("█", plain);
        Assert.Contains("FlyknitBuddy 9.9.9", plain);
        Assert.Contains("工作区 D:\\日报", plain);
    }

    /// <summary>把一串字变成按键：\r 回车、\b 退格、\t Tab、\u001b Esc，其他按字符。</summary>
    internal static Queue<ConsoleKeyInfo?> Keys(string typed)
    {
        var q = new Queue<ConsoleKeyInfo?>();
        foreach (var c in typed)
        {
            q.Enqueue(c switch
            {
                '\r' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
                '\b' => new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
                '\t' => new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
                '\u001b' => new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false),
                _ => new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false),
            });
        }
        return q;
    }

    [Fact]
    public async Task TypedTextCanBeDeletedAndCommandsCompleteWithTab()
    {
        // 真终端里整行由 LineEditor 读：第一个字也能退格删掉（以前删不掉）；/ex + Tab 补全成 /exit
        var keys = Keys("看x\b\b看看目录\r" + "\r" + "/ex\t\r");
        var gateway = new Scripted(new ChatTurn { Content = "好" });
        var output = new StringWriter();
        var shell = new InteractiveShell(Env(gateway), _dir, PermissionMode.Workspace, new StringReader(""), output,
            style: new ConsoleStyle(true), store: Store(), readKey: _ => Task.FromResult(keys.Count > 0 ? keys.Dequeue() : null));
        await shell.RunAsync(null, null, CancellationToken.None);

        Assert.Equal("看看目录", gateway.Requests.Single().Messages.Last(m => m.Role == ChatRole.User).Content);
        var text = output.ToString();
        Assert.Contains(InteractiveShell.Placeholder, text);
        Assert.Contains("\u001b[K", text, StringComparison.Ordinal);   // 打字时提示被擦掉
        Assert.Contains("再见", text);                                   // /ex + Tab = /exit
    }

    [Fact]
    public async Task NoPlaceholderWithoutColors()
    {
        var (output, _) = await Run(new Scripted(), "/exit\n");
        Assert.DoesNotContain(InteractiveShell.Placeholder, output);
    }

    [Fact]
    public void IdleCtrlCAsksBeforeExiting()
    {
        var now = DateTimeOffset.Parse("2026-11-09T10:00:00Z");
        var shell = new InteractiveShell(Env(new Scripted()), _dir, PermissionMode.Workspace, new StringReader(""), new StringWriter(), now: () => now);
        Assert.True(shell.Interrupt());          // 第一次：提示
        now += TimeSpan.FromSeconds(1);
        Assert.False(shell.Interrupt());         // 两秒内再按：退出
        now += TimeSpan.FromSeconds(10);
        Assert.True(shell.Interrupt());          // 隔久了：重新提示
    }

    [Fact]
    public void FullPermissionIsNeverUsedInChat()
    {
        var shell = new InteractiveShell(Env(new Scripted()), _dir, PermissionMode.Full, new StringReader(""), new StringWriter());
        Assert.Equal(PermissionMode.Workspace, shell.Session.Permission);
    }

    [Theory]
    [InlineData(new[] { "-c" }, "continue", "")]
    [InlineData(new[] { "--resume" }, "list", "")]
    [InlineData(new[] { "--resume", "acp-0123456789ab" }, "acp-0123456789ab", "")]
    [InlineData(new[] { "--think", "看看", "目录" }, null, "看看 目录")]
    public void ParsesChatOptions(string[] args, string? resume, string prompt)
    {
        var (o, error) = CliOptions.Parse(args);
        Assert.Null(error);
        Assert.Equal("chat", o!.Command);
        Assert.Equal(resume, o.Resume);
        Assert.Equal(prompt, o.Prompt);
    }

    [Theory]
    [InlineData("--allow-writes")]
    [InlineData("--format", "text")]
    [InlineData("--timeout", "30")]
    public void PrintOnlyOptionsNeedP(params string[] args) => Assert.Contains("-p", CliOptions.Parse(args).Error);

    [Fact]
    public void PrintRejectsChatOnlyOptions()
    {
        Assert.NotNull(CliOptions.Parse(new[] { "-p", "x", "-c" }).Error);
        Assert.NotNull(CliOptions.Parse(new[] { "-p", "x", "多余" }).Error);
    }

    [Theory]
    [InlineData("y", true, ConfirmChoice.AllowOnce)]
    [InlineData("是", false, ConfirmChoice.AllowOnce)]
    [InlineData("a", true, ConfirmChoice.AllowAlways)]
    [InlineData("n", true, ConfirmChoice.Reject)]
    [InlineData("", false, ConfirmChoice.Reject)]
    public void ParsesConfirmAnswers(string answer, bool offersAlways, ConfirmChoice expected) =>
        Assert.Equal(expected, ConsoleConfirm.Parse(answer, offersAlways));

    [Fact]
    public void AlwaysIsNotAcceptedWhenNotOffered() => Assert.Null(ConsoleConfirm.Parse("a", offersAlways: false));

    /// <summary>像控制台一样：没有输入时等着，测试里再往里推。</summary>
    private sealed class BlockingReader : TextReader
    {
        private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public BlockingReader(params string[] lines)
        {
            foreach (var l in lines)
            {
                Push(l);
            }
        }

        public void Push(string line) => _lines.Writer.TryWrite(line);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct) => await _lines.Reader.ReadAsync(ct);
    }
}
