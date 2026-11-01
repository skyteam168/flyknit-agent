using Xunit;
using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Security;
using Flyknit.Core.Terminal;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Tests;

/// <summary>终端：命令输出实时记下来、后台命令能看输出能停、IT 没打开时不能后台启动。命令在 Linux 上用 /bin/sh 跑。</summary>
public sealed class TerminalTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "flyknit-term-" + Guid.NewGuid().ToString("N"));
    private readonly TerminalHub _hub = new();

    public TerminalTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_work, true); } catch (Exception) { }
    }

    private ToolContext Ctx(bool background = true, string conv = "c1", bool terminal = true) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = conv,
        Workspace = _work,
        Permission = PermissionMode.Workspace,
        Terminal = terminal ? _hub : null,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.Sandbox] = new() { Value = false, Locked = true },
            [SecuritySettings.BackgroundCommands] = new() { Value = background, Locked = true },
        }),
    };

    // 命令在 Windows 上是 PowerShell（CI 就在 Windows 上跑），其他系统是 /bin/sh
    private static readonly bool Win = OperatingSystem.IsWindows();
    private static readonly string ExitThreeWithStderr = Win
        ? "echo one; [Console]::Error.WriteLine('two'); exit 3"
        : "echo one; echo two 1>&2; exit 3";
    private static readonly string TickForever = Win
        ? "echo ready; while ($true) { echo tick; Start-Sleep -Milliseconds 200 }"
        : "echo ready; while true; do echo tick; sleep 0.2; done";

    private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();

    [Fact]
    public async Task ForegroundCommandsShowUpInTheTerminalWithTheirOutputAsItComes()
    {
        var started = new List<string>();
        var chunks = new List<string>();
        var ended = new List<TerminalRun>();
        _hub.Started += r => { lock (started) started.Add(r.Command); };
        _hub.Output += (_, text) => { lock (chunks) chunks.Add(text); };
        _hub.Ended += r => { lock (ended) ended.Add(r); };

        var result = await new RunShellTool().ExecuteAsync(Args(new { command = ExitThreeWithStderr }), Ctx(), default);

        Assert.False(result.Ok);
        Assert.Contains("退出码：3", result.Output);
        Assert.Contains("one", result.Output);
        Assert.Contains("[错误输出]", result.Output);
        var run = Assert.Single(_hub.Runs("c1"));
        Assert.False(run.Background);
        Assert.False(run.Running);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("one", run.Output);
        Assert.Contains("two", run.Output);
        Assert.Equal(new[] { ExitThreeWithStderr }, started);
        Assert.Contains(chunks, c => c.Contains("one"));
        Assert.Single(ended);
    }

    [Fact]
    public async Task ATimedOutCommandIsStoppedAndMarkedSo()
    {
        var result = await new RunShellTool().ExecuteAsync(Args(new { command = "sleep 30", timeout_seconds = 1 }), Ctx(), default);

        Assert.False(result.Ok);
        Assert.Contains("超时", result.Output);
        Assert.Contains("background=true", result.Output); // 提醒模型一直运行的程序要在后台启动
        var run = Assert.Single(_hub.Runs("c1"));
        Assert.True(run.Stopped);
        Assert.False(run.Running);
    }

    [Fact]
    public async Task BackgroundCommandsKeepRunningAndTheModelReadsOnlyWhatIsNew()
    {
        var ctx = Ctx();
        var start = await new RunShellTool().ExecuteAsync(
            Args(new { command = TickForever, background = true, wait_seconds = 1 }), ctx, default);

        Assert.True(start.Ok, start.Output);
        Assert.Contains("t", start.Output);
        Assert.Contains("ready", start.Output);
        var run = Assert.Single(_hub.Runs("c1"));
        Assert.True(run.Background);
        Assert.True(run.Running);

        var more = await new ShellOutputTool().ExecuteAsync(Args(new { id = run.Id, wait_seconds = 1 }), ctx, default);
        Assert.True(more.Ok);
        Assert.Contains("运行中", more.Output);
        Assert.Contains("tick", more.Output);
        Assert.DoesNotContain("ready", more.Output); // 上次已经给过的不再给

        var list = await new ShellOutputTool().ExecuteAsync(Args(new { }), ctx, default);
        Assert.Contains(run.Id, list.Output);

        var stop = await new ShellStopTool().ExecuteAsync(Args(new { id = run.Id }), ctx, default);
        Assert.True(stop.Ok);
        Assert.True(run.Stopped);
        Assert.Equal(0, _hub.RunningInBackground("c1"));

        var again = await new ShellStopTool().ExecuteAsync(Args(new { id = run.Id }), ctx, default);
        Assert.Contains("本来就不在运行", again.Output);
    }

    [Fact]
    public async Task ABackgroundCommandThatEndsRightAwayReportsItsExitCode()
    {
        var r = await new RunShellTool().ExecuteAsync(Args(new { command = "echo boom; exit 2", background = true, wait_seconds = 5 }), Ctx(), default);

        Assert.False(r.Ok);
        Assert.Contains("已经结束", r.Output);
        Assert.Contains("boom", r.Output);
    }

    [Fact]
    public async Task BackgroundNeedsTheSwitchAndATerminal()
    {
        var off = await new RunShellTool().ExecuteAsync(Args(new { command = "echo hi", background = true }), Ctx(background: false), default);
        Assert.False(off.Ok);
        Assert.Contains("后台命令没有开启", off.Output);

        var noTerminal = await new RunShellTool().ExecuteAsync(Args(new { command = "echo hi", background = true }), Ctx(terminal: false), default);
        Assert.False(noTerminal.Ok);
        Assert.Empty(_hub.Runs("c1"));

        // 没有终端时普通命令照常执行（单测、专家子任务）
        var plain = await new RunShellTool().ExecuteAsync(Args(new { command = "echo hi" }), Ctx(terminal: false), default);
        Assert.True(plain.Ok);
        Assert.Contains("hi", plain.Output);

        // 服务端还没下发这一项时按关闭算
        var unset = new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c1", Security = new SecuritySettings() };
        Assert.False(unset.BackgroundCommands);
    }

    [Fact]
    public async Task OneConversationCannotSeeOrStopAnothersCommands()
    {
        await new RunShellTool().ExecuteAsync(Args(new { command = "sleep 30", background = true, wait_seconds = 0 }), Ctx(conv: "a"), default);
        var run = Assert.Single(_hub.Runs("a"));

        var read = await new ShellOutputTool().ExecuteAsync(Args(new { id = run.Id }), Ctx(conv: "b"), default);
        var stop = await new ShellStopTool().ExecuteAsync(Args(new { id = run.Id }), Ctx(conv: "b"), default);

        Assert.False(read.Ok);
        Assert.False(stop.Ok);
        Assert.True(run.Running);
        _hub.StopAll("a");
        Assert.False(run.Running);
    }

    [Fact]
    public async Task EachConversationHasALimitOnBackgroundCommands()
    {
        var ctx = Ctx();
        for (var i = 0; i < TerminalHub.MaxBackgroundPerConversation; i++)
        {
            var ok = await new RunShellTool().ExecuteAsync(Args(new { command = "sleep 30", background = true, wait_seconds = 0 }), ctx, default);
            Assert.True(ok.Ok, ok.Output);
        }
        var over = await new RunShellTool().ExecuteAsync(Args(new { command = "sleep 30", background = true, wait_seconds = 0 }), ctx, default);

        Assert.False(over.Ok);
        Assert.Contains("shell_stop", over.Output);
        Assert.Equal(TerminalHub.MaxBackgroundPerConversation, _hub.RunningInBackground("c1"));
    }

    [Fact]
    public async Task CancellingTheTaskStopsAForegroundCommand()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RunShellTool().ExecuteAsync(Args(new { command = "sleep 30" }), Ctx(), cts.Token));
        Assert.True(Assert.Single(_hub.Runs("c1")).Stopped);
    }

    [Fact]
    public void LongOutputKeepsOnlyTheTailAndTheModelIsToldWhatWasSkipped()
    {
        var run = typeof(TerminalRun).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0]
            .Invoke(new object[] { "t1", "c1", "x", "sh", "/", true }) as TerminalRun;
        var append = typeof(TerminalRun).GetMethod("Append", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        append.Invoke(run, new object[] { new string('a', TerminalRun.MaxOutputChars) });
        append.Invoke(run, new object[] { "END" });

        Assert.Equal(TerminalRun.MaxOutputChars, run!.Output.Length);
        Assert.EndsWith("END", run.Output);
        Assert.Equal(TerminalRun.MaxOutputChars + 3, run.TotalChars);

        var text = run.ReadNew(100, out var skipped);
        Assert.Equal(100, text.Length);
        Assert.EndsWith("END", text);
        Assert.Equal(TerminalRun.MaxOutputChars + 3 - 100, skipped);
        Assert.Equal("", run.ReadNew(100, out skipped));
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void TheTerminalGuideIsOnlyInTheAgentPromptWhenBackgroundCommandsAreOn()
    {
        var on = new PromptBuilder(null, null).Build(new PromptContext { Mode = ConversationMode.Agent, BackgroundCommands = true });
        var off = new PromptBuilder(null, null).Build(new PromptContext { Mode = ConversationMode.Agent });
        var chat = new PromptBuilder(null, null).Build(new PromptContext { Mode = ConversationMode.Chat, BackgroundCommands = true });

        Assert.Contains("<终端与后台命令>", on);
        Assert.DoesNotContain("<终端与后台命令>", off);
        Assert.DoesNotContain("<终端与后台命令>", chat);
    }
}
