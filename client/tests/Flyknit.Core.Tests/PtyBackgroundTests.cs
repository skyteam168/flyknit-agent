using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Flyknit.Core.Terminal;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// AI 的后台命令放进伪控制台跑：界面拿到带颜色的原始输出、员工能敲字；AI（shell_output）读到的是去掉控制序列的纯文本；
/// 结束、停止照旧。最后一个用例在 Windows 上真的跑（CI 的 Windows 机器）。
/// </summary>
public class PtyBackgroundTests
{
    [Fact]
    public void ControlSequencesAreStrippedForTheAi()
    {
        Assert.Equal("  VITE ready\n  Local: http://localhost:5173/\n",
            VtText.Strip("\x1b[?25l\x1b]0;npm run dev\x07\x1b[32m  VITE\x1b[39m ready\r\n  Local: \x1b[36mhttp://localhost:5173/\x1b[0m\r\n"));
        Assert.Equal("plain\n", VtText.Strip("plain\r\n"));
    }

    [Fact]
    public void SequencesSplitAcrossChunksAreStillRemoved()
    {
        // Windows CI 上真的遇到过：一段输出末尾只有 ESC，剩下的在下一段，逐段去掉会把 ESC 漏给 AI
        var s = new VtStripper();
        var text = s.Feed("\x1b[?9001h\x1b") + s.Feed("[?1004hflyknit\x1b[3") + s.Feed("2mok\x1b]0;title") + s.Feed("\x07 done\r\n") + s.Flush();
        Assert.Equal("flyknitok done\n", text);
        Assert.False(text.Contains('\x1b')); // 按字符比（DoesNotContain(string) 按文化比较，ESC 是可忽略字符，任何字符串都「包含」它）
        // 结束时还剩半截：ESC 去掉，不会一直攒着
        var t = new VtStripper();
        Assert.Equal("a", t.Feed("a\x1b["));
        Assert.False(t.Flush().Contains('\x1b')); // 按字符比（DoesNotContain(string) 按文化比较，ESC 是可忽略字符，任何字符串都「包含」它）
    }

    [Fact]
    public void CommandLineQuotingFollowsWindowsRules()
    {
        Assert.Equal("cmd.exe /d /c \"echo a & b\"", VtText.CommandLine("cmd.exe", new[] { "/d", "/c", "echo a & b" }));
        Assert.Equal("x.exe \"say \\\"hi\\\"\" \"C:\\dir with space\\\\\"", VtText.CommandLine("x.exe", new[] { "say \"hi\"", "C:\\dir with space\\" }));
        Assert.Equal("x.exe \"\"", VtText.CommandLine("x.exe", new[] { "" }));
    }

    private sealed class FakeConsole : ITerminalSession
    {
        public readonly Channel<byte[]> Output = Channel.CreateUnbounded<byte[]>();
        public readonly TaskCompletionSource<int> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly StringBuilder Input = new();
        public string? CommandLine;
        public bool Disposed;

        public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct)
        {
            if (!await Output.Reader.WaitToReadAsync(ct))
            {
                return 0;
            }
            var b = await Output.Reader.ReadAsync(ct);
            b.CopyTo(buffer, 0);
            return b.Length;
        }

        public void Write(string text) => Input.Append(text);
        public void Resize(short cols, short rows) { }
        public Task<int> WaitForExitAsync(CancellationToken ct) => Exit.Task.WaitAsync(ct);

        public void Dispose()
        {
            Disposed = true;
            Output.Writer.TryComplete();
            Exit.TrySetResult(-1);
        }
    }

    private static (TerminalHub Hub, FakeConsole Console) Hub()
    {
        var console = new FakeConsole();
        var hub = new TerminalHub { UsePseudoConsole = true, StartConsole = (cmd, cwd) => { console.CommandLine = cmd; return console; } };
        return (hub, console);
    }

    private static ProcessStartInfo Psi()
    {
        var psi = new ProcessStartInfo("powershell.exe") { WorkingDirectory = Path.GetTempPath() };
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("npm run dev");
        return psi;
    }

    [Fact]
    public async Task BackgroundCommandsRunInTheConsoleAndTheAiReadsPlainText()
    {
        var (hub, console) = Hub();
        var raw = new StringBuilder();
        hub.RawOutput += (_, text) => { lock (raw) raw.Append(text); };
        var run = hub.Start("c1", Psi(), "npm run dev", "powershell", background: true);
        Assert.True(run.Pty);
        Assert.Equal("powershell.exe -Command \"npm run dev\"", console.CommandLine);
        await console.Output.Writer.WriteAsync(Encoding.UTF8.GetBytes("\x1b[32m  VITE\x1b[39m ready\r\n"));
        for (var i = 0; i < 100 && !run.Output.Contains("ready"); i++)
        {
            await Task.Delay(10);
        }
        Assert.Equal("  VITE ready\n", run.Output);               // AI 看到的
        Assert.Contains("\x1b[32m", run.RawOutput);                // 界面画颜色用的
        lock (raw) Assert.Contains("\x1b[32m", raw.ToString());
        Assert.True(hub.Write(run, "q"));                          // 员工往里敲字
        Assert.Equal("q", console.Input.ToString());
        console.Exit.SetResult(0);
        console.Output.Writer.TryComplete();
        Assert.True(await TerminalHub.WaitAsync(run, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(0, run.ExitCode);
        Assert.False(hub.Write(run, "x"));                         // 结束了不能再敲
    }

    [Fact]
    public async Task StoppingClosesTheConsole()
    {
        var (hub, console) = Hub();
        var run = hub.Start("c1", Psi(), "npm run dev", "powershell", background: true);
        Assert.True(hub.Stop(run));
        Assert.True(console.Disposed);
        Assert.True(run.Stopped);
        await Task.Delay(50);
        Assert.Null(run.ExitCode);
    }

    [Fact]
    public void ForegroundCommandsStillUseAPlainProcess()
    {
        var (hub, console) = Hub();
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", "/d /c echo hi") : new ProcessStartInfo("/bin/sh", "-c \"echo hi\"");
        var run = hub.Start("c1", psi, "echo hi", "cmd", background: false);
        Assert.False(run.Pty);
        Assert.Null(console.CommandLine);
    }

    [Fact]
    public async Task RealBackgroundCommandOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // CI 的 Windows 机器上会真的跑
        }
        var hub = new TerminalHub { UsePseudoConsole = true };
        var psi = new ProcessStartInfo("cmd.exe") { WorkingDirectory = Path.GetTempPath() };
        foreach (var a in new[] { "/d", "/s", "/c", "echo flyknit-bg-ok & ping -n 2 127.0.0.1 >nul" })
        {
            psi.ArgumentList.Add(a);
        }
        var run = hub.Start("c1", psi, "echo", "cmd", background: true);
        Assert.True(run.Pty);
        Assert.True(await TerminalHub.WaitAsync(run, TimeSpan.FromSeconds(30), CancellationToken.None), "命令没有结束：" + run.Output);
        Assert.Contains("flyknit-bg-ok", run.Output);
        Assert.False(run.Output.Contains('\x1b')); // 按字符比（DoesNotContain(string) 按文化比较，ESC 是可忽略字符，任何字符串都「包含」它）
        Assert.Equal(0, run.ExitCode);
    }
}
