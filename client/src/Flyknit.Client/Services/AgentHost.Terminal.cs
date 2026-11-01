using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Flyknit.Core.Terminal;

namespace Flyknit.Client.Services;

/// <summary>
/// 终端：AI 执行的命令和输出实时推给界面右侧「终端」标签（terminal.started / terminal.output / terminal.ended）。
/// 输出按 150 毫秒攒一批再推，开发服务器刷屏也不会把界面压垮。
/// </summary>
public sealed partial class AgentHost
{
    private static readonly TimeSpan TerminalFlushInterval = TimeSpan.FromMilliseconds(150);

    private readonly Dictionary<TerminalRun, StringBuilder> _terminalPending = new();
    private readonly object _terminalGate = new();
    private TerminalHub? _terminal;
    private Timer? _terminalTimer;

    /// <summary>所有对话共用的终端。第一次用到时建好并接上界面推送。</summary>
    public TerminalHub Terminal
    {
        get
        {
            lock (_terminalGate)
            {
                if (_terminal is not null)
                {
                    return _terminal;
                }
                var hub = new TerminalHub();
                hub.Started += run =>
                {
                    FlushTerminal();
                    Events?.Post(new { type = "terminal.started", run = TerminalDto(run, includeOutput: false) });
                };
                hub.Output += (run, text) =>
                {
                    lock (_terminalGate)
                    {
                        if (!_terminalPending.TryGetValue(run, out var sb))
                        {
                            _terminalPending[run] = sb = new StringBuilder();
                        }
                        // 界面一批最多收这么多：刷屏太快时中间的丢掉（完整的最后一段仍在 TerminalRun 里）
                        if (sb.Length < 64 * 1024)
                        {
                            sb.Append(text);
                        }
                    }
                };
                hub.Ended += run =>
                {
                    FlushTerminal();
                    Events?.Post(new { type = "terminal.ended", run = TerminalDto(run, includeOutput: false) });
                };
                _terminalTimer = new Timer(_ => FlushTerminal(), null, TerminalFlushInterval, TerminalFlushInterval);
                return _terminal = hub;
            }
        }
    }

    private void FlushTerminal()
    {
        List<(TerminalRun Run, string Text)> batch;
        lock (_terminalGate)
        {
            if (_terminalPending.Count == 0)
            {
                return;
            }
            batch = _terminalPending.Select(kv => (kv.Key, kv.Value.ToString())).ToList();
            _terminalPending.Clear();
        }
        foreach (var (run, text) in batch)
        {
            Events?.Post(new { type = "terminal.output", conversationId = run.ConversationId, runId = run.Id, text });
        }
    }

    public static object TerminalDto(TerminalRun r, bool includeOutput) => new
    {
        id = r.Id,
        conversationId = r.ConversationId,
        command = r.Command,
        shell = r.Shell,
        cwd = r.WorkingDirectory,
        background = r.Background,
        startedAt = r.StartedAt.ToString("O"),
        endedAt = r.EndedAt?.ToString("O"),
        running = r.Running,
        stopped = r.Stopped,
        exitCode = r.ExitCode,
        output = includeOutput ? r.Output : null,
    };

    /// <summary>界面重新加载时取这个对话的终端记录（带输出）。</summary>
    public IReadOnlyList<object> TerminalRuns(string conversationId) =>
        Terminal.Runs(conversationId).Select(r => TerminalDto(r, includeOutput: true)).ToList();

    /// <summary>员工在终端标签里点了停止。</summary>
    public bool StopTerminalRun(string conversationId, string runId) =>
        Terminal.Find(conversationId, runId) is { } run && Terminal.Stop(run);

    public void ClearTerminal(string conversationId) => Terminal.Clear(conversationId);

    /// <summary>对话删掉了：它的后台命令一起停掉。</summary>
    public void StopTerminalFor(string conversationId)
    {
        if (_terminal is { } hub)
        {
            hub.StopAll(conversationId);
        }
    }

    private void DisposeTerminal()
    {
        _terminalTimer?.Dispose();
        _terminal?.Dispose();
    }
}
