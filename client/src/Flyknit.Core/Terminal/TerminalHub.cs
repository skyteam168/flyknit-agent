using System.Diagnostics;
using System.Text;

namespace Flyknit.Core.Terminal;

/// <summary>终端里的一条命令：AI 执行过的（前台跑完的、后台一直跑着的）都在这里，界面右侧「终端」标签按对话列出来。</summary>
public sealed class TerminalRun
{
    /// <summary>每条命令最多留这么多字符的输出（只留最后的部分），后台跑一整天的开发服务器也不会把内存撑爆。</summary>
    public const int MaxOutputChars = 256 * 1024;

    private readonly StringBuilder _output = new();
    private readonly object _gate = new();
    private long _total;
    private long _readByModel;

    internal TerminalRun(string id, string conversationId, string command, string shell, string workingDirectory, bool background)
    {
        Id = id;
        ConversationId = conversationId;
        Command = command;
        Shell = shell;
        WorkingDirectory = workingDirectory;
        Background = background;
        StartedAt = DateTimeOffset.Now;
    }

    public string Id { get; }
    public string ConversationId { get; }
    public string Command { get; }
    public string Shell { get; }
    public string WorkingDirectory { get; }
    public bool Background { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>还在跑。</summary>
    public bool Running => ExitCode is null && !Stopped;

    public int? ExitCode { get; private set; }

    /// <summary>被停掉的（员工点了停止、AI 用 shell_stop、超时、任务被取消、程序退出）。</summary>
    public bool Stopped { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    internal Process? Process { get; set; }

    /// <summary>已经要求停掉：进程随后退出时记成「已停止」，而不是被杀掉的退出码。</summary>
    internal volatile bool StopRequested;

    /// <summary>进程结束（或被停掉）时完成。</summary>
    internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>目前留着的输出（太长时只有最后一段）。</summary>
    public string Output
    {
        get
        {
            lock (_gate)
            {
                return _output.ToString();
            }
        }
    }

    /// <summary>一共输出过多少字符（包括已经丢掉的开头）。</summary>
    public long TotalChars
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    internal void Append(string text)
    {
        lock (_gate)
        {
            _output.Append(text);
            _total += text.Length;
            if (_output.Length > MaxOutputChars)
            {
                _output.Remove(0, _output.Length - MaxOutputChars);
            }
        }
    }

    /// <summary>
    /// AI 上次看过之后新输出的部分（shell_output 用）。最多给 <paramref name="maxChars"/> 个字符：太多时只给最后一段，
    /// 并说明中间省略了多少。
    /// </summary>
    public string ReadNew(int maxChars, out long skipped)
    {
        lock (_gate)
        {
            var kept = _total - _output.Length; // 已经丢掉的开头
            var from = Math.Max(_readByModel, kept);
            skipped = from - _readByModel;
            var available = (int)(_total - from);
            if (available > maxChars)
            {
                skipped += available - maxChars;
                from = _total - maxChars;
                available = maxChars;
            }
            _readByModel = _total;
            return available <= 0 ? "" : _output.ToString((int)(from - kept), available);
        }
    }

    /// <summary>记下结束。已经记过返回 false（停止和进程退出可能同时到，只算一次）。</summary>
    internal bool MarkExited(int? exitCode, bool stopped)
    {
        lock (_gate)
        {
            if (EndedAt is not null)
            {
                return false;
            }
            ExitCode = stopped ? null : exitCode;
            Stopped = stopped;
            EndedAt = DateTimeOffset.Now;
        }
        Exited.TrySetResult();
        return true;
    }
}

/// <summary>
/// 终端：AI 执行的每条命令都从这里启动，输出实时推给界面（右侧「终端」标签）。
/// 前台命令跑完就结束；后台命令（开发服务器、监听编译之类）一直跑，AI 随时用 shell_output 看新输出、用 shell_stop 停掉，
/// 员工也能在终端标签里点停止。对话删掉、程序退出时，这个对话的后台命令全部停掉。
/// </summary>
public sealed class TerminalHub : IDisposable
{
    /// <summary>每个对话同时最多跑几个后台命令。</summary>
    public const int MaxBackgroundPerConversation = 4;

    /// <summary>每个对话最多记多少条命令（旧的、已经结束的先丢）。</summary>
    public const int MaxRunsPerConversation = 40;

    private readonly Dictionary<string, List<TerminalRun>> _byConversation = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _next;

    /// <summary>开始了一条命令。</summary>
    public event Action<TerminalRun>? Started;

    /// <summary>命令有新输出（一段文字，可能是好几行）。</summary>
    public event Action<TerminalRun, string>? Output;

    /// <summary>命令结束了（退出、被停掉）。</summary>
    public event Action<TerminalRun>? Ended;

    public IReadOnlyList<TerminalRun> Runs(string conversationId)
    {
        lock (_gate)
        {
            return _byConversation.TryGetValue(conversationId, out var list) ? list.ToList() : Array.Empty<TerminalRun>();
        }
    }

    public TerminalRun? Find(string conversationId, string id)
    {
        lock (_gate)
        {
            return _byConversation.TryGetValue(conversationId, out var list)
                ? list.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                : null;
        }
    }

    public int RunningInBackground(string conversationId)
    {
        lock (_gate)
        {
            return _byConversation.TryGetValue(conversationId, out var list) ? list.Count(r => r.Background && r.Running) : 0;
        }
    }

    /// <summary>
    /// 启动一条命令。输出一边存一边推给界面。后台命令超出上限时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public TerminalRun Start(string conversationId, ProcessStartInfo psi, string command, string shell, bool background,
        Action<string, bool>? onLine = null)
    {
        TerminalRun run;
        lock (_gate)
        {
            if (!_byConversation.TryGetValue(conversationId, out var list))
            {
                _byConversation[conversationId] = list = new List<TerminalRun>();
            }
            if (background && list.Count(r => r.Background && r.Running) >= MaxBackgroundPerConversation)
            {
                throw new InvalidOperationException($"这个对话已经有 {MaxBackgroundPerConversation} 个后台命令在跑，先用 shell_stop 停掉不用的");
            }
            run = new TerminalRun($"t{++_next}", conversationId, command, shell, psi.WorkingDirectory, background);
            list.Add(run);
            // 记录太多时丢掉最早的、已经结束的
            while (list.Count > MaxRunsPerConversation && list.FindIndex(r => !r.Running) is var old && old >= 0)
            {
                list.RemoveAt(old);
            }
        }

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = background; // 后台命令不读输入；前台的照旧
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Line(string? data, bool error)
        {
            if (data is null)
            {
                return;
            }
            var text = data + "\n";
            run.Append(text);
            onLine?.Invoke(data, error);
            Raise(() => Output?.Invoke(run, text));
        }
        process.OutputDataReceived += (_, e) => Line(e.Data, false);
        process.ErrorDataReceived += (_, e) => Line(e.Data, true);
        process.Exited += (_, _) => _ = FinishAsync(run, process);
        run.Process = process;

        Raise(() => Started?.Invoke(run));
        try
        {
            process.Start();
        }
        catch (Exception)
        {
            if (run.MarkExited(-1, stopped: false))
            {
                Raise(() => Ended?.Invoke(run));
            }
            process.Dispose();
            throw;
        }
        if (background)
        {
            try { process.StandardInput.Close(); } catch (Exception) { }
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return run;
    }

    /// <summary>等命令结束（或超时、取消）。超时返回 false，命令还在跑。</summary>
    public static async Task<bool> WaitAsync(TerminalRun run, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await run.Exited.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>停掉一条命令（连同它启动的子进程）。已经结束的不做什么，返回 false。</summary>
    public bool Stop(TerminalRun run)
    {
        if (!run.Running)
        {
            return false;
        }
        // 先标记再杀：杀掉后进程退出的回调可能先到，它看到这个标记就记成「已停止」
        run.StopRequested = true;
        try
        {
            run.Process?.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 已经退出了，或者没权限：照样标成停掉
        }
        if (run.MarkExited(null, stopped: true))
        {
            Raise(() => Ended?.Invoke(run));
        }
        return true;
    }

    /// <summary>停掉一个对话里还在跑的命令（对话删掉时）。</summary>
    public void StopAll(string conversationId)
    {
        foreach (var run in Runs(conversationId).Where(r => r.Running))
        {
            Stop(run);
        }
    }

    /// <summary>清掉一个对话里已经结束的命令记录（员工点了清空）。</summary>
    public void Clear(string conversationId)
    {
        lock (_gate)
        {
            if (_byConversation.TryGetValue(conversationId, out var list))
            {
                list.RemoveAll(r => !r.Running);
            }
        }
    }

    private async Task FinishAsync(TerminalRun run, Process process)
    {
        int? code = null;
        try
        {
            // Exited 事件比最后几行输出早到：等输出读完再算结束
            await process.WaitForExitAsync();
            code = process.ExitCode;
        }
        catch (Exception)
        {
        }
        if (run.MarkExited(code, stopped: run.StopRequested))
        {
            Raise(() => Ended?.Invoke(run));
        }
        process.Dispose();
    }

    /// <summary>界面那边出错不能影响命令本身。</summary>
    private static void Raise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        List<TerminalRun> all;
        lock (_gate)
        {
            all = _byConversation.Values.SelectMany(x => x).Where(r => r.Running).ToList();
        }
        foreach (var run in all)
        {
            Stop(run);
        }
    }
}
