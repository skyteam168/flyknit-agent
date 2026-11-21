using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Flyknit.Core.Terminal;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 编码模式底部终端里员工自己的 PowerShell：开发者工具关着打不开、最多开几个、输出按 UTF-8 拼好推给界面、
/// 结束时发 pty.exit；内容不进日志。最后一个用例在 Windows 上真的起一个伪控制台（CI 的 Windows 机器上会跑）。
/// </summary>
public class UserTerminalTests
{
    private sealed class FakeSession : ITerminalSession
    {
        public readonly Channel<byte[]> Output = Channel.CreateUnbounded<byte[]>();
        public readonly StringBuilder Input = new();
        public (short Cols, short Rows) Size;
        public bool Disposed;

        public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct)
        {
            if (!await Output.Reader.WaitToReadAsync(ct))
            {
                return 0;
            }
            var chunk = await Output.Reader.ReadAsync(ct);
            chunk.CopyTo(buffer, 0);
            return chunk.Length;
        }

        public void Write(string text) => Input.Append(text);
        public void Resize(short cols, short rows) => Size = (cols, rows);
        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(0);

        public void Dispose()
        {
            Disposed = true;
            Output.Writer.TryComplete();
        }
    }

    private sealed class Rig
    {
        public bool Allowed = true;
        public readonly ConcurrentQueue<JsonElement> Events = new();
        public readonly List<string> Log = new();
        public readonly List<FakeSession> Sessions = new();
        public string? Cwd;
        public readonly UserTerminalFeature Feature;

        public Rig()
        {
            Feature = new UserTerminalFeature(
                id => id == "c1" ? "/work" : null,
                () => Allowed,
                e => Events.Enqueue(JsonSerializer.SerializeToElement(e)),
                Log.Add,
                (cmd, cwd, cols, rows) =>
                {
                    Cwd = cwd;
                    var s = new FakeSession { Size = (cols, rows) };
                    Sessions.Add(s);
                    return s;
                });
        }

        public JsonElement Open() => JsonSerializer.SerializeToElement(Feature.Open("c1", 100, 30));

        public async Task<string> OutputUntil(string id, Func<string, bool> done)
        {
            var text = new StringBuilder();
            for (var i = 0; i < 200 && !done(text.ToString()); i++)
            {
                while (Events.TryDequeue(out var e))
                {
                    if (e.GetProperty("type").GetString() == "pty.output" && e.GetProperty("id").GetString() == id)
                    {
                        text.Append(e.GetProperty("data").GetString());
                    }
                }
                await Task.Delay(10);
            }
            return text.ToString();
        }
    }

    [Fact]
    public void ClosedWhenDeveloperToolsAreOff()
    {
        var rig = new Rig { Allowed = false };
        var r = rig.Open();
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Empty(rig.Sessions);
    }

    [Fact]
    public async Task OutputIsDecodedAcrossChunksAndInputGoesThrough()
    {
        var rig = new Rig();
        var r = rig.Open();
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("/work", rig.Cwd);   // 起在对话的工作区
        var id = r.GetProperty("id").GetString()!;
        var s = rig.Sessions.Single();
        // 「工作区」的 UTF-8 故意从中间切开
        var bytes = Encoding.UTF8.GetBytes("PS D:\\工作区> ");
        await s.Output.Writer.WriteAsync(bytes[..7]);
        await s.Output.Writer.WriteAsync(bytes[7..]);
        Assert.Equal("PS D:\\工作区> ", await rig.OutputUntil(id, t => t.EndsWith("> ")));
        Assert.True((bool)rig.Feature.Write(id, "dir\r"));
        Assert.Equal("dir\r", s.Input.ToString());
        rig.Feature.Resize(id, 140, 40);
        Assert.Equal((140, 40), ((int)s.Size.Cols, (int)s.Size.Rows));
        // 日志里只有开了终端，没有输入输出
        Assert.DoesNotContain(rig.Log, l => l.Contains("dir") || l.Contains("PS "));
    }

    [Fact]
    public async Task ExitIsReportedAndSessionsAreLimited()
    {
        var rig = new Rig();
        for (var i = 0; i < UserTerminalFeature.MaxSessions; i++)
        {
            Assert.True(rig.Open().GetProperty("ok").GetBoolean());
        }
        Assert.False(rig.Open().GetProperty("ok").GetBoolean());
        // 第一个退出：发 pty.exit，名额空出来
        rig.Sessions[0].Output.Writer.TryComplete();
        JsonElement exit = default;
        for (var i = 0; i < 200 && exit.ValueKind == JsonValueKind.Undefined; i++)
        {
            await Task.Delay(10);
            exit = rig.Events.FirstOrDefault(e => e.GetProperty("type").GetString() == "pty.exit");
        }
        Assert.Equal("pty1", exit.GetProperty("id").GetString());
        Assert.True(rig.Sessions[0].Disposed);
        Assert.True(rig.Open().GetProperty("ok").GetBoolean());
        rig.Feature.Dispose();
        Assert.All(rig.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task RealPseudoConsoleOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 伪控制台是 Windows 的系统接口；CI 的 Windows 机器上会真的跑
        }
        using var console = PseudoConsole.Start("cmd.exe /d /q", Path.GetTempPath(), 100, 30);
        var output = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            int n;
            while ((n = await console.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(buffer, 0, n));
                }
            }
        });
        console.Write("echo flyknit-pty-ok\r");
        for (var i = 0; i < 300; i++)
        {
            lock (output)
            {
                if (output.ToString().Contains("flyknit-pty-ok\r\n") || output.ToString().Split("flyknit-pty-ok").Length > 2)
                {
                    break;
                }
            }
            await Task.Delay(50);
        }
        console.Write("exit\r");
        var code = await console.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        lock (output)
        {
            Assert.Contains("flyknit-pty-ok", output.ToString());
        }
        Assert.Equal(0, code);
    }
}
