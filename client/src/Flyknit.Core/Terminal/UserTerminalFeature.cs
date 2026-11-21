using System.Collections.Concurrent;
using System.Text;
using Flyknit.Core.Hosting;

namespace Flyknit.Core.Terminal;

/// <summary>
/// 编码模式底部的「终端」里，员工自己的 PowerShell（像 VS Code 的终端）。界面用 xterm.js 显示。
///
/// - 只在开发者工具开着时能开（IT 在安全中心打开，默认关）；
/// - 这是员工自己敲命令，和自己打开 PowerShell 一样：不经过 AI，内容不进 AI 的上下文、不进日志；
///   日志只记谁在哪个目录开了 / 关了终端；
/// - 起在对话的工作区目录；同时最多开 <see cref="MaxSessions"/> 个；程序退出时一并关掉。
/// AI 自己跑的命令（run_shell，包括后台的）还是走原来的命令确认，在终端列表里和员工的终端并排显示。
///
/// 方法：pty.open / pty.write / pty.resize / pty.close。事件：pty.output（一段输出）、pty.exit（结束）。
/// </summary>
public sealed class UserTerminalFeature : IBridgeModule, IDisposable
{
    public const int MaxSessions = 6;

    private readonly Func<string, string?> _workspaceOf;
    private readonly Func<bool> _allowed;
    private readonly Action<object> _post;
    private readonly Action<string> _log;
    private readonly Func<string, string, short, short, ITerminalSession> _start;

    /// <summary>员工往 AI 的后台命令（伪控制台里跑的）敲字、调整大小：(对话, 命令编号, 内容) / (对话, 命令编号, 列, 行)。</summary>
    public Func<string, string, string, bool>? WriteRun { get; init; }
    public Func<string, string, short, short, bool>? ResizeRun { get; init; }
    private readonly ConcurrentDictionary<string, ITerminalSession> _sessions = new(StringComparer.Ordinal);
    private int _counter;

    /// <param name="workspaceOf">对话 id → 工作区目录。</param>
    /// <param name="allowed">现在能不能开（开发者工具开着、在 Windows 上）。</param>
    /// <param name="post">把事件发给界面。</param>
    /// <param name="start">启动一个终端（测试里换成假的；默认是 Windows 伪控制台里的 PowerShell）。</param>
    public UserTerminalFeature(Func<string, string?> workspaceOf, Func<bool> allowed, Action<object> post, Action<string> log,
        Func<string, string, short, short, ITerminalSession>? start = null)
    {
        _workspaceOf = workspaceOf;
        _allowed = allowed;
        _post = post;
        _log = log;
        _start = start ?? StartPowerShell;
    }

    public IReadOnlyCollection<string> Methods { get; } = new[] { "pty.open", "pty.write", "pty.resize", "pty.close", "pty.runWrite", "pty.runResize" };

    public Task<object?> HandleAsync(string method, BridgeArgs args, CancellationToken ct)
    {
        object? result = method switch
        {
            "pty.open" => Open(args.Str("conversationId"), Clamp(args.Int("cols"), 120), Clamp(args.Int("rows"), 30)),
            "pty.write" => Write(args.Str("id"), args.Str("data")),
            "pty.resize" => Resize(args.Str("id"), Clamp(args.Int("cols"), 120), Clamp(args.Int("rows"), 30)),
            "pty.close" => Close(args.Str("id")),
            "pty.runWrite" => WriteRun?.Invoke(args.Str("conversationId"), args.Str("runId"), args.Str("data")) ?? false,
            "pty.runResize" => ResizeRun?.Invoke(args.Str("conversationId"), args.Str("runId"), Clamp(args.Int("cols"), 120), Clamp(args.Int("rows"), 30)) ?? false,
            _ => null,
        };
        return Task.FromResult(result);
    }

    private static short Clamp(int? value, short fallback) => (short)Math.Clamp(value ?? fallback, 10, 500);

    public object Open(string conversationId, short cols, short rows)
    {
        if (!_allowed())
        {
            return new { ok = false, id = "", title = "", message = "终端只在开发者工具打开时可用（IT 在安全中心设置），而且只支持 Windows" };
        }
        if (_sessions.Count >= MaxSessions)
        {
            return new { ok = false, id = "", title = "", message = $"最多同时开 {MaxSessions} 个终端，先关掉一个" };
        }
        var cwd = _workspaceOf(conversationId) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ITerminalSession session;
        try
        {
            session = _start("powershell.exe -NoLogo", cwd, cols, rows);
        }
        catch (Exception ex)
        {
            return new { ok = false, id = "", title = "", message = ex.Message };
        }
        var n = Interlocked.Increment(ref _counter);
        var id = $"pty{n}";
        _sessions[id] = session;
        _log($"员工打开了终端 {id}（{cwd}）");
        _ = PumpAsync(id, session);
        return new { ok = true, id, title = $"PowerShell {n}", cwd, message = "" };
    }

    public object Write(string id, string data)
    {
        if (!_sessions.TryGetValue(id, out var s))
        {
            return false;
        }
        try
        {
            s.Write(data);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public object Resize(string id, short cols, short rows)
    {
        if (_sessions.TryGetValue(id, out var s))
        {
            s.Resize(cols, rows);
            return true;
        }
        return false;
    }

    public object Close(string id)
    {
        if (!_sessions.TryRemove(id, out var s))
        {
            return false;
        }
        s.Dispose();
        _log($"员工关闭了终端 {id}");
        return true;
    }

    /// <summary>一直读输出推给界面；读完（程序退出）发 pty.exit。按 UTF-8 解码，多字节字符被拆在两段里也能接上。</summary>
    private async Task PumpAsync(string id, ITerminalSession session)
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192 + 4];
        try
        {
            while (true)
            {
                var n = await session.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }
                var count = decoder.GetChars(bytes, 0, n, chars, 0, flush: false);
                if (count > 0)
                {
                    _post(new { type = "pty.output", id, data = new string(chars, 0, count) });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 关掉了
        }
        var code = -1;
        try
        {
            code = await session.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // 拿不到退出码
        }
        if (_sessions.TryRemove(id, out var s))
        {
            s.Dispose();
        }
        _post(new { type = "pty.exit", id, exitCode = code });
    }

    private static ITerminalSession StartPowerShell(string commandLine, string cwd, short cols, short rows) =>
        OperatingSystem.IsWindows()
            ? new PseudoConsoleSession(PseudoConsole.Start(commandLine, cwd, cols, rows))
            : throw new PlatformNotSupportedException("终端只支持 Windows");

    public void Dispose()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(id, out var s))
            {
                s.Dispose();
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class PseudoConsoleSession(PseudoConsole console) : ITerminalSession
    {
        public Task<int> ReadAsync(byte[] buffer, CancellationToken ct) => console.ReadAsync(buffer, ct);
        public void Write(string text) => console.Write(text);
        public void Resize(short cols, short rows) => console.Resize(cols, rows);
        public Task<int> WaitForExitAsync(CancellationToken ct) => console.WaitForExitAsync(ct);
        public void Dispose() => console.Dispose();
    }
}

/// <summary>一个终端会话（伪控制台里的 shell；测试里是假的）。</summary>
public interface ITerminalSession : IDisposable
{
    Task<int> ReadAsync(byte[] buffer, CancellationToken ct);
    void Write(string text);
    void Resize(short cols, short rows);
    Task<int> WaitForExitAsync(CancellationToken ct);
}
