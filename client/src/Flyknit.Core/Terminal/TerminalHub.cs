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
    /// <summary>后台命令。前台命令超时后可以转到后台（<see cref="TerminalHub.Promote"/>），所以会变。</summary>
    public bool Background { get; internal set; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>还在跑。</summary>
    public bool Running => ExitCode is null && !Stopped;

    public int? ExitCode { get; private set; }

    /// <summary>被停掉的（员工点了停止、AI 用 shell_stop、超时、任务被取消、程序退出）。</summary>
    public bool Stopped { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    internal Process? Process { get; set; }

    /// <summary>在伪控制台里跑的（后台命令，Windows 上）：界面用 xterm 画颜色，员工可以往里敲字。</summary>
    internal ITerminalSession? Console { get; set; }

    public bool Pty => Console is not null || _raw.Length > 0;

    private readonly StringBuilder _raw = new();

    /// <summary>伪控制台的原始输出（带颜色和光标控制，界面重放用；太长时只留最后一段）。</summary>
    public string RawOutput
    {
        get
        {
            lock (_gate)
            {
                return _raw.ToString();
            }
        }
    }

    internal void AppendRaw(string text)
    {
        lock (_gate)
        {
            _raw.Append(text);
            if (_raw.Length > MaxOutputChars)
            {
                _raw.Remove(0, _raw.Length - MaxOutputChars);
            }
        }
    }

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
    /// <summary>
    /// 后台命令放进伪控制台跑（宿主在 Windows 上打开）：开发服务器之类的输出带颜色，员工能在终端里直接和它交互。
    /// AI 读到的输出去掉了控制序列，和普通进程的一样。前台命令照旧用普通进程（要等结束、拿退出码、超时转后台）。
    /// </summary>
    public bool UsePseudoConsole { get; set; }

    /// <summary>伪控制台的原始输出（带颜色），界面上的终端画面用。</summary>
    public event Action<TerminalRun, string>? RawOutput;

    /// <summary>员工往伪控制台里跑的后台命令敲字。不是伪控制台的、已经结束的返回 false。</summary>
    public bool Write(TerminalRun run, string data)
    {
        if (!run.Running || run.Console is not { } console)
        {
            return false;
        }
        try
        {
            console.Write(data);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Resize(TerminalRun run, short cols, short rows) => run.Console?.Resize(cols, rows);

    /// <summary>测试里换成假的伪控制台；默认是 Windows 的。</summary>
    internal Func<string, string, ITerminalSession>? StartConsole { get; set; }

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

        if (background && UsePseudoConsole && (StartConsole is not null || OperatingSystem.IsWindows()))
        {
            StartInConsole(run, psi, onLine);
            return run;
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

    /// <summary>
    /// 前台命令跑太久：转到后台接着跑（不杀掉），AI 之后用 shell_output 看、shell_stop 停。
    /// 已经结束、这个对话后台命令已满时返回 false，调用方照旧按超时处理。
    /// </summary>
    public bool Promote(TerminalRun run)
    {
        lock (_gate)
        {
            if (!run.Running || run.Background)
            {
                return false;
            }
            var list = _byConversation.TryGetValue(run.ConversationId, out var l) ? l : new List<TerminalRun>();
            if (list.Count(r => r.Background && r.Running) >= MaxBackgroundPerConversation)
            {
                return false;
            }
            run.Background = true;
        }
        // 界面按「开始」事件更新这条命令（标成后台运行中）
        Raise(() => Started?.Invoke(run));
        return true;
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
            run.Console?.Dispose(); // 关掉伪控制台：里面的程序一起结束
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

    private void StartInConsole(TerminalRun run, ProcessStartInfo psi, Action<string, bool>? onLine)
    {
        var commandLine = VtText.CommandLine(psi.FileName, psi.ArgumentList);
        ITerminalSession console;
        Raise(() => Started?.Invoke(run));
        try
        {
            console = StartConsole is { } start ? start(commandLine, psi.WorkingDirectory)
                : OperatingSystem.IsWindows() ? new WindowsConsole(PseudoConsole.Start(commandLine, psi.WorkingDirectory, 120, 30))
                : throw new PlatformNotSupportedException();
        }
        catch (Exception)
        {
            if (run.MarkExited(-1, stopped: false))
            {
                Raise(() => Ended?.Invoke(run));
            }
            throw;
        }
        run.Console = console;

        // 读输出：原始的给界面画，去掉控制序列的给 AI（shell_output）
        var reading = Task.Run(async () =>
        {
            var decoder = new UTF8Encoding(false).GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[8196];
            var stripper = new VtStripper();
            try
            {
                int n;
                while ((n = await console.ReadAsync(bytes, CancellationToken.None).ConfigureAwait(false)) > 0)
                {
                    var count = decoder.GetChars(bytes, 0, n, chars, 0, flush: false);
                    if (count == 0)
                    {
                        continue;
                    }
                    var raw = new string(chars, 0, count);
                    run.AppendRaw(raw);
                    Raise(() => RawOutput?.Invoke(run, raw));
                    Emit(stripper.Feed(raw));
                }
            }
            catch (Exception)
            {
                // 伪控制台关了
            }
            Emit(stripper.Flush());

            void Emit(string plain)
            {
                if (plain.Length > 0)
                {
                    run.Append(plain);
                    onLine?.Invoke(plain.TrimEnd('\n'), false);
                    Raise(() => Output?.Invoke(run, plain));
                }
            }
        });

        // 程序退出后稍等最后的输出，再关伪控制台（不关的话读输出那边一直等着）
        _ = Task.Run(async () =>
        {
            int? code = null;
            try
            {
                code = await console.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            await Task.WhenAny(reading, Task.Delay(500)).ConfigureAwait(false);
            console.Dispose();
            await Task.WhenAny(reading, Task.Delay(2000)).ConfigureAwait(false);
            if (run.MarkExited(run.StopRequested ? null : code, stopped: run.StopRequested))
            {
                Raise(() => Ended?.Invoke(run));
            }
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class WindowsConsole(PseudoConsole console) : ITerminalSession
    {
        public Task<int> ReadAsync(byte[] buffer, CancellationToken ct) => console.ReadAsync(buffer, ct);
        public void Write(string text) => console.Write(text);
        public void Resize(short cols, short rows) => console.Resize(cols, rows);
        public Task<int> WaitForExitAsync(CancellationToken ct) => console.WaitForExitAsync(ct);
        public void Dispose() => console.Dispose();
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
