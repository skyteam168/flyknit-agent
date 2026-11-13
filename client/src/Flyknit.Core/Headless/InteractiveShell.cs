using System.Diagnostics;
using System.Text;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Headless;

/// <summary>
/// 终端颜色（ANSI）。输出被重定向、设了 NO_COLOR、或者终端不支持时关掉，全部原样输出。
/// </summary>
public sealed class ConsoleStyle
{
    public ConsoleStyle(bool enabled) => Enabled = enabled;

    public bool Enabled { get; }

    public static readonly ConsoleStyle Plain = new(false);

    private string Wrap(string code, string text) => Enabled ? $"\u001b[{code}m{text}\u001b[0m" : text;

    public string Bold(string t) => Wrap("1", t);
    public string Dim(string t) => Wrap("2", t);
    public string Red(string t) => Wrap("31", t);
    public string Green(string t) => Wrap("32", t);
    public string Yellow(string t) => Wrap("33", t);
    public string Cyan(string t) => Wrap("36", t);
    public string Magenta(string t) => Wrap("35", t);

    /// <summary>24 位真彩色（Windows Terminal、Win10 以后的控制台都支持）。</summary>
    public string Rgb((int R, int G, int B) c, string t) => Wrap($"38;2;{c.R};{c.G};{c.B}", t);

    /// <summary>清屏并把光标移到左上角。没有颜色时（重定向）什么都不做。</summary>
    public string Clear => Enabled ? "\u001b[2J\u001b[H" : "";
}

/// <summary>
/// flyknit 交互模式（同 Codex CLI / Claude Code 的终端对话）：在命令行里一句一句地聊，回答边生成边打印，
/// 工具调用显示成一行、需要确认的操作在终端里问员工。
///
/// 安全和 IDE（ACP）一样：闸门、审计、额度和员工端是同一套；确认时「以后自动」只在员工端也能设成自动执行的
/// 操作上出现，删除、覆盖等高危操作只有「允许一次 / 拒绝」；没有完全权限。
/// 会话存在本机，和 IDE 的会话共用（/resume、flyknit -c）。
/// </summary>
public sealed class InteractiveShell
{
    private readonly HeadlessEnvironment _env;
    /// <summary>新会话、打开的旧会话用的权限（/permission 改它，也改当前会话的）。</summary>
    private PermissionMode _permission;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ConsoleStyle _style;
    private readonly AcpSessionStore? _store;
    /// <summary>深度思考开着时也把思考过程显示出来（--think 或 /think on）。</summary>
    private bool _showThinking;
    private readonly string _version;
    private readonly Func<Task>? _afterTurn;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly int _terminalWidth;

    /// <summary>读一个按键（不回显）。真终端里才有：用来在输入行显示灰色提示、敲第一个字时擦掉。</summary>
    private readonly Func<CancellationToken, Task<ConsoleKeyInfo?>>? _readKey;

    /// <summary>真终端里自己读整行（退格、Tab 补全、↑↓ 翻历史），见 LineEditor。输入被重定向时为 null，照旧用 ReadLine。</summary>
    private readonly LineEditor? _editor;

    /// <summary>Tab 能补全的命令。</summary>
    public static readonly string[] CommandNames =
    {
        "/help", "/new", "/resume", "/plan", "/usage", "/compact", "/permission", "/context", "/status", "/think", "/clear", "/exit",
    };

    /// <summary>输入行空着时显示的灰色提示（同 Codex 的「Ask Codex to do anything」）。</summary>
    public const string Placeholder = "告诉 FlyknitBuddy 要做什么（/help 看命令）";

    private HeadlessSession _session;
    private DateTimeOffset _createdAt;
    private string? _firstPrompt;
    private CancellationTokenSource? _turn;
    private DateTimeOffset _lastIdleInterrupt = DateTimeOffset.MinValue;
    private long _sessionPrompt;
    private long _sessionCompletion;

    /// <summary>空闲时两次 Ctrl+C 之间不超过这么久才退出（第一次只提示）。</summary>
    public static readonly TimeSpan DoubleInterrupt = TimeSpan.FromSeconds(2);

    public InteractiveShell(HeadlessEnvironment env, string workspace, PermissionMode permission, TextReader input, TextWriter output,
        ConsoleStyle? style = null, AcpSessionStore? store = null, bool showThinking = false, string version = "0.1.0",
        Func<Task>? afterTurn = null, Func<DateTimeOffset>? now = null, int terminalWidth = 0,
        Func<CancellationToken, Task<ConsoleKeyInfo?>>? readKey = null)
    {
        _readKey = readKey;
        _terminalWidth = terminalWidth;
        var styleOn = (style ?? ConsoleStyle.Plain).Enabled;
        if (readKey is not null && styleOn)
        {
            _editor = new LineEditor(readKey, output, style!, terminalWidth) { Completions = CommandNames };
        }
        _env = env;
        _permission = permission == PermissionMode.Full ? PermissionMode.Workspace : permission;
        _input = input;
        _output = output;
        _style = style ?? ConsoleStyle.Plain;
        _store = store;
        _showThinking = showThinking;
        _thinking = env.DeepThinking;
        _version = version;
        _afterTurn = afterTurn;
        _now = now ?? (() => DateTimeOffset.Now);
        _session = NewSession(workspace);
        _createdAt = _now();
    }

    public HeadlessSession Session => _session;

    /// <summary>新会话的 ID 和 IDE 的同一个格式：会话列表、/resume 两边通用。</summary>
    private HeadlessSession NewSession(string workspace) =>
        new(_env, workspace, _permission, "acp-" + Guid.NewGuid().ToString("N")[..12]) { DeepThinking = _thinking };

    /// <summary>深度思考（--think 起始，/think 切换）；新会话、打开的旧会话都跟着它。</summary>
    private bool _thinking;

    /// <summary>
    /// Ctrl+C：有任务在跑就停下这一轮（已经发出的工具调用照常补结果），返回 true（不退出）。
    /// 空闲时第一次只提示，<see cref="DoubleInterrupt"/> 内再按一次返回 false（退出）。
    /// </summary>
    public bool Interrupt()
    {
        lock (_gate)
        {
            if (_turn is { } turn)
            {
                try
                {
                    turn.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                return true;
            }
            var now = _now();
            if (now - _lastIdleInterrupt <= DoubleInterrupt)
            {
                return false;
            }
            _lastIdleInterrupt = now;
            var message = _style.Dim("（再按一次 Ctrl+C 退出，或输入 /exit）");
            if (_editor is not null)
            {
                _editor.Interrupted(message); // 换一行写提示，重新显示提示符，正在打的这一行清空
            }
            else
            {
                Write("\n" + message + "\n");
            }
            return true;
        }
    }

    /// <param name="firstPrompt">命令行上带着的第一句话（flyknit "……"）</param>
    /// <param name="resume">continue：接着这个工作区最近的会话；list：先列出来让员工选；会话 ID：直接打开</param>
    public async Task<int> RunAsync(string? firstPrompt, string? resume, CancellationToken ct)
    {
        Banner();
        if (resume is not null)
        {
            await ResumeAsync(resume == "list" ? "" : resume, ct);
        }
        if (!string.IsNullOrWhiteSpace(firstPrompt))
        {
            await TurnAsync(firstPrompt.Trim(), ct);
        }
        while (!ct.IsCancellationRequested)
        {
            var text = await ReadPromptAsync(ct);
            if (text is null)
            {
                break; // 输入结束（Ctrl+Z / 管道读完）
            }
            if (text.Length == 0)
            {
                continue;
            }
            if (text.StartsWith('/'))
            {
                if (!await CommandAsync(text, ct))
                {
                    break;
                }
                continue;
            }
            await TurnAsync(text, ct);
        }
        Write(_style.Dim("再见。") + "\n");
        return ExitCodes.Ok;
    }

    // ---------- 输入 ----------

    /// <summary>
    /// 读一句话。行尾写 \ 接着下一行；单独一行 """ 开始多行输入，再一行 """ 结束（粘贴大段文字用）。
    /// 返回 null 表示输入结束。
    /// </summary>
    private string PromptMark => _style.Bold(_style.Cyan("> "));

    private async Task<string?> ReadPromptAsync(CancellationToken ct)
    {
        Write(PromptMark);
        var first = await ReadFirstLineAsync(ct);
        if (first is null)
        {
            return null;
        }
        if (first.Trim() == "\"\"\"")
        {
            var block = new List<string>();
            while (true)
            {
                Write(_style.Dim("… "));
                var line = await ReadLineAsync(ct, _style.Dim("… "));
                if (line is null || line.Trim() == "\"\"\"")
                {
                    break;
                }
                block.Add(line);
            }
            return string.Join("\n", block).Trim();
        }
        var lines = new List<string>();
        var current = first;
        while (current.EndsWith('\\'))
        {
            lines.Add(current[..^1]);
            Write(_style.Dim("… "));
            current = await ReadLineAsync(ct, _style.Dim("… ")) ?? "";
        }
        lines.Add(current);
        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// 读输入的第一行。真终端里先在光标后面显示灰色提示、光标退回行首：控制台的 ReadLine 没法在敲字时擦掉别的字，
    /// 所以先单独读第一个按键，擦掉提示、把这个字回显出来，剩下的照常交给 ReadLine（中文输入法、粘贴都正常）。
    /// </summary>
    private async Task<string?> ReadFirstLineAsync(CancellationToken ct)
    {
        if (_editor is not null)
        {
            return await _editor.ReadLineAsync(PromptMark, Placeholder, ct);
        }
        if (_readKey is null || !_style.Enabled)
        {
            return await ReadLineAsync(ct);
        }
        var width = ConsoleBanner.DisplayWidth(Placeholder);
        Write(_style.Dim(Placeholder) + $"\u001b[{width}D");
        ConsoleKeyInfo? key;
        try
        {
            key = await _readKey(ct);
        }
        finally
        {
            Write("\u001b[K"); // 擦掉提示
        }
        if (key is not { } k)
        {
            return await ReadLineAsync(ct);
        }
        if (k.Key == ConsoleKey.Enter)
        {
            Write("\n");
            return "";
        }
        // 方向键、退格这类没有字符的按键：不回显，照常读这一行
        if (k.KeyChar == '\0' || char.IsControl(k.KeyChar))
        {
            return await ReadLineAsync(ct);
        }
        Write(k.KeyChar.ToString());
        var rest = await ReadLineAsync(ct);
        return rest is null ? k.KeyChar.ToString() : k.KeyChar + rest;
    }

    /// <param name="prompt">调用方已经写出来的提示（真终端里自己读整行时要知道它占几列）</param>
    private async Task<string?> ReadLineAsync(CancellationToken ct, string prompt = "")
    {
        if (_editor is not null)
        {
            return await _editor.ReadLineAsync(prompt, null, ct, remember: false);
        }
        while (true)
        {
            var line = await _input.ReadLineAsync(ct);
            // 空闲时按 Ctrl+C（只提示、不退出），控制台的 ReadLine 会返回 null：不当成输入结束
            if (line is null && _now() - _lastIdleInterrupt < TimeSpan.FromMilliseconds(500))
            {
                Write(_style.Bold(_style.Cyan("> ")));
                continue;
            }
            return line;
        }
    }

    // ---------- 一轮 ----------

    private async Task TurnAsync(string text, CancellationToken ct)
    {
        _firstPrompt ??= text;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _turn = cts;
        }
        var observer = new ConsoleObserver(_output, _style, _showThinking);
        var confirm = new ConsoleConfirm(_input, _output, _style, observer,
            _editor is null ? null : (prompt, token) => _editor.ReadLineAsync(prompt, null, token, remember: false));
        var watch = Stopwatch.StartNew();
        observer.ShowWaiting();
        AgentRunResult? result = null;
        try
        {
            result = await _session.PromptAsync(text, confirm, observer, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            observer.EndLine();
            Write(_style.Red("出错了：" + ex.Message) + "\n");
        }
        finally
        {
            lock (_gate)
            {
                _turn = null;
            }
        }
        watch.Stop();
        observer.EndLine();
        if (result is not null)
        {
            _sessionPrompt += result.Usage?.PromptTokens ?? 0;
            _sessionCompletion += result.Usage?.CompletionTokens ?? 0;
        }
        Write(_style.Dim(Footer(result, cts.IsCancellationRequested && !ct.IsCancellationRequested, watch.Elapsed)) + "\n\n");
        _store?.Save(_session, _firstPrompt, _createdAt);
        if (_afterTurn is not null)
        {
            try
            {
                await _afterTurn();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Write(_style.Dim("（上报审计失败，下一轮再试：" + ex.Message + "）") + "\n");
            }
        }
    }

    public static string Footer(AgentRunResult? result, bool stopped, TimeSpan elapsed)
    {
        var parts = new List<string>();
        if (stopped || result?.StopReason == AgentStopReason.Cancelled)
        {
            parts.Add("已停止");
        }
        else if (result is not null && result.StopReason != AgentStopReason.Completed)
        {
            parts.Add(result.StopReason switch
            {
                AgentStopReason.MaxSteps => "步数到上限，没做完",
                AgentStopReason.Budget => "这次任务的额度用完了，没做完",
                AgentStopReason.TooManyFailures => "失败太多次，停下了",
                AgentStopReason.Stuck => "一直在重复同一件事，停下了",
                AgentStopReason.Failed => "模型服务出错",
                _ => result.StopReason.ToString(),
            });
        }
        if (result is not null)
        {
            if (result.ToolCalls > 0)
            {
                parts.Add($"{result.ToolCalls} 次工具");
            }
            if (result.Usage is { } u && u.Total > 0)
            {
                parts.Add($"{Short(u.Total)} tokens");
            }
        }
        parts.Add(elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:0.0} 秒" : $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒");
        return "· " + string.Join(" · ", parts);
    }

    private static string Short(long n) => n >= 10_000 ? $"{n / 1000.0:0.#}k" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---------- 斜杠命令 ----------

    public const string Help = """
        命令：
          /new              开一个新会话（当前会话已保存）
          /resume [序号]    列出这个工作区以前的会话；带序号直接打开接着聊
          /plan             看当前的任务计划
          /usage            这个会话用了多少 token
          /compact [重点]   马上整理对话，较早的压成摘要；可以写上要重点保留什么
          /context          上下文用了多少、各部分占多少
          /permission [档]  看或调权限：只读、工作区（命令行没有完全权限）
          /think [开|关]    打开 / 关闭深度思考（更慢、更费额度）
          /status           这个会话的版本、工作区、权限、开发者工具、深度思考
          /clear            清屏
          /help             显示这段说明
          /exit             退出（也可以 Ctrl+D / Ctrl+Z 回车，或空闲时连按两次 Ctrl+C）
        输入：
          回车发送；行尾写 \ 换行接着写；单独一行 \"\"\" 开始多行输入，再一行 \"\"\" 结束。
          打 / 再按 Tab 补全命令（有好几个时再按一次 Tab 列出来）；↑ ↓ 翻之前输过的话；Esc 清空这一行。
          任务进行中按 Ctrl+C 停下这一轮。
        """;

    /// <summary>处理一条斜杠命令。返回 false 表示退出。</summary>
    private async Task<bool> CommandAsync(string line, CancellationToken ct)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit" or "/quit" or "/q":
                return false;
            case "/help" or "/?":
                Write(Help.Replace("\\\"", "\"") + "\n");
                break;
            case "/clear":
                Write(_style.Clear);
                Banner();
                break;
            case "/new":
                _session = NewSession(_session.Workspace);
                _createdAt = _now();
                _firstPrompt = null;
                _sessionPrompt = _sessionCompletion = 0;
                Write(_style.Green("已开始新会话。") + "\n\n");
                break;
            case "/resume" or "/sessions":
                await ResumeAsync(arg, ct);
                break;
            case "/plan":
                if (_session.Plan.Count == 0)
                {
                    Write(_style.Dim("现在没有任务计划。") + "\n");
                }
                else
                {
                    ConsoleObserver.WritePlan(_output, _style, _session.Plan);
                }
                break;
            case "/usage":
                Write($"这个会话：输入 {Short(_sessionPrompt)}、输出 {Short(_sessionCompletion)} tokens（今日额度和员工端共用）\n");
                break;
            case "/compact":
                await CompactAsync(arg, ct);
                break;
            case "/permission" or "/permissions":
                SetPermission(arg);
                break;
            case "/context":
                ShowContext();
                break;
            case "/status":
                ShowStatus();
                break;
            case "/think":
                SetThinking(arg);
                break;
            default:
                Write(_style.Yellow($"不认识的命令 {parts[0]}，输入 /help 看能用哪些。") + "\n");
                break;
        }
        return true;
    }

    /// <summary>/compact [要重点保留的内容]：和员工端「整理对话」同一套，压完说一声省了多少。</summary>
    private async Task CompactAsync(string focus, CancellationToken ct)
    {
        if (_session.History.Count == 0)
        {
            Write(_style.Dim("还没有对话，不用整理。") + "\n");
            return;
        }
        Write(_style.Dim("  … 正在整理对话") + "\n");
        CompactionInfo? info;
        try
        {
            info = await _session.CompactAsync(focus, ct);
        }
        catch (OperationCanceledException)
        {
            Write(_style.Yellow("已取消整理。") + "\n");
            return;
        }
        catch (Exception ex)
        {
            Write(_style.Yellow("整理失败：" + ex.Message) + "\n");
            return;
        }
        if (info is null)
        {
            Write(_style.Dim("对话还很短，不用整理（最近一问一答保留原文，之前的不到两条）。") + "\n");
            return;
        }
        _store?.Save(_session, _firstPrompt ?? "", _createdAt);
        Write(_style.Green($"已整理 {info.MessagesCompacted} 条较早的消息：约 {Short(info.TokensBefore)} → {Short(info.TokensAfter)} tokens") +
              (focus.Length > 0 ? _style.Dim($"（重点保留：{Clip(focus, 60)}）") : "") + "\n");
    }

    /// <summary>/permission [只读|工作区]：没有参数时说当前是哪一档。命令行没有完全权限。</summary>
    private void SetPermission(string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        if (a.Length == 0)
        {
            Write($"当前权限：{PermissionLabel(_session.Permission)}。可以选：/permission 只读、/permission 工作区（命令行没有完全权限）\n");
            return;
        }
        PermissionMode? mode = a switch
        {
            "readonly" or "read" or "ro" or "只读" or "仅可查看" => PermissionMode.ReadOnly,
            "workspace" or "ws" or "edit" or "工作区" or "工作区内修改" => PermissionMode.Workspace,
            _ => null,
        };
        if (a is "full" or "完全" or "完全权限")
        {
            Write(_style.Yellow("命令行没有完全权限：只能员工在聊天窗口里对单个任务选。") + "\n");
            return;
        }
        if (mode is not { } m)
        {
            Write(_style.Yellow($"没有「{arg}」这一档，可以选：只读、工作区") + "\n");
            return;
        }
        _permission = m;
        _session.SetPermission(m);
        Write(_style.Green($"权限已切换为「{PermissionLabel(m)}」，下一句话起生效。") + "\n");
    }

    /// <summary>/think [开|关]：不带参数就切换。思考过程跟着一起显示 / 不显示。</summary>
    private void SetThinking(string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        bool? on = a switch
        {
            "" => !_thinking,
            "on" or "开" or "1" or "true" => true,
            "off" or "关" or "0" or "false" => false,
            _ => null,
        };
        if (on is not { } value)
        {
            Write(_style.Yellow("用法：/think、/think 开、/think 关") + "\n");
            return;
        }
        _thinking = value;
        _showThinking = value;
        _session.DeepThinking = value;
        Write(value
            ? _style.Green("已打开深度思考：每一步先想一想，更慢、更费额度。") + "\n"
            : _style.Green("已关闭深度思考。") + "\n");
    }

    /// <summary>/context：上下文用了多少、各部分占多少（估算）。</summary>
    private void ShowContext()
    {
        var report = _session.MeasureContext();
        var percent = report.ContextLength > 0 ? report.Used * 100.0 / report.ContextLength : 0;
        Write(_style.Bold($"上下文：约 {Short(report.Used)} / {Short(report.ContextLength)} tokens（{percent:0}%）") +
              _style.Dim($"，超过 {Short(report.CompactAt)} 会自动整理") + "\n");
        foreach (var part in report.Parts.Where(p => p.Tokens > 0))
        {
            // 中文标签一个字占两列：按显示宽度补空格，数字才对得齐
            var pad = new string(' ', Math.Max(1, 12 - ConsoleBanner.DisplayWidth(part.Label)));
            Write($"  {part.Label}{pad}{Short(part.Tokens),8}\n");
        }
        if (_session.Summary is not null)
        {
            Write(_style.Dim("  （较早的对话已经整理成摘要）") + "\n");
        }
        Write(_style.Dim("  按字数估算，和实际计费会有出入；/compact 可以马上整理。") + "\n");
    }

    /// <summary>/status：这个会话正在用的设置。</summary>
    private void ShowStatus()
    {
        var dev = _env.Security.On(SecuritySettings.DeveloperTools, fallback: false);
        var rows = new (string, string)[]
        {
            ("版本", _version),
            ("会话", _session.Id),
            ("工作区", _session.Workspace),
            ("权限", PermissionLabel(_session.Permission)),
            ("开发者工具", dev ? "开" : "关（IT 在安全中心控制）"),
            ("深度思考", _thinking ? "开" : "关"),
            ("对话", $"{_session.History.Count(m => m.Role == ChatRole.User)} 句" + (_session.Summary is null ? "" : "（较早的已整理成摘要）")),
            ("本会话用量", $"输入 {Short(_sessionPrompt)}、输出 {Short(_sessionCompletion)} tokens"),
        };
        foreach (var (k, v) in rows)
        {
            Write($"  {_style.Dim(k + "：")}{v}\n");
        }
    }

    /// <summary>
    /// 打开以前的会话。arg 为空：列出这个工作区的会话让员工选；数字：列表里的第几个；continue：最近一个；其余当会话 ID。
    /// </summary>
    private async Task ResumeAsync(string arg, CancellationToken ct)
    {
        if (_store is null)
        {
            Write(_style.Yellow("这台电脑上没有保存会话。") + "\n");
            return;
        }
        var sessions = _store.List(_session.Workspace).Where(s => s.Id != _session.Id).ToList();
        string? id = null;
        if (arg == "continue")
        {
            id = sessions.FirstOrDefault()?.Id;
            if (id is null)
            {
                Write(_style.Dim("这个工作区还没有以前的会话，开一个新的。") + "\n\n");
                return;
            }
        }
        else if (arg.Length > 0 && !int.TryParse(arg, out _))
        {
            id = arg;
        }
        else
        {
            if (sessions.Count == 0)
            {
                Write(_style.Dim("这个工作区还没有以前的会话。") + "\n");
                return;
            }
            var shown = sessions.Take(20).ToList();
            if (!int.TryParse(arg, out var pick))
            {
                Write(_style.Bold("以前的会话（这个工作区，最近的在前）：") + "\n");
                for (var i = 0; i < shown.Count; i++)
                {
                    Write($"  {i + 1,2}. {shown[i].Title}  {_style.Dim(shown[i].UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"))}\n");
                }
                var ask = _style.Dim("输入序号打开，直接回车取消：") + " ";
                Write(ask);
                var answer = (await ReadLineAsync(ct, ask))?.Trim() ?? "";
                if (!int.TryParse(answer, out pick))
                {
                    Write("\n");
                    return;
                }
            }
            if (pick < 1 || pick > shown.Count)
            {
                Write(_style.Yellow($"没有第 {pick} 个。") + "\n");
                return;
            }
            id = shown[pick - 1].Id;
        }

        var saved = _store.Load(id);
        if (saved is null)
        {
            Write(_style.Yellow($"找不到会话 {id}。") + "\n");
            return;
        }
        if (!Directory.Exists(saved.Cwd))
        {
            Write(_style.Yellow($"这个会话的工作目录已经不在了：{saved.Cwd}") + "\n");
            return;
        }
        var session = new HeadlessSession(_env, saved.Cwd, _permission, saved.Id) { DeepThinking = _thinking };
        session.Restore(saved.Messages, saved.Plan, saved.LoadedTools, saved.Summary);
        _session = session;
        _createdAt = saved.CreatedAt;
        _firstPrompt = saved.Title;
        _sessionPrompt = _sessionCompletion = 0;
        Write(_style.Green($"已打开「{saved.Title}」，接着聊。") + "\n");
        // 回顾最近一问一答，免得员工不记得聊到哪了
        var lastUser = session.History.LastOrDefault(m => m.Role == ChatRole.User);
        var lastAnswer = session.History.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Trim().Length > 0);
        if (lastUser is not null)
        {
            Write(_style.Dim("  你：" + Clip(lastUser.Content, 200)) + "\n");
        }
        if (lastAnswer is not null)
        {
            Write(_style.Dim("  AI：" + Clip(lastAnswer.Content, 400)) + "\n");
        }
        Write("\n");
    }

    private void Banner()
    {
        var dev = _env.Security.On(SecuritySettings.DeveloperTools, fallback: false);
        var info = new[]
        {
            $"工作区 {_session.Workspace}",
            // 框里不用「·」「…」这类宽度不确定的字符（有的字体占一列、有的占两列），框线会歪
            $"权限：{PermissionLabel(_permission)}    开发者工具：{(dev ? "开" : "关")}    深度思考：{(_thinking ? "开" : "关")}",
        };
        Write(ConsoleBanner.Render(_style, _terminalWidth, _version, info,
            "输入要做的事，回车发送；/help 看命令；任务进行中按 Ctrl+C 停下"));
    }

    private static string PermissionLabel(PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "只读",
        PermissionMode.Workspace => "工作区内修改",
        _ => "工作区内修改",
    };

    private void Write(string text)
    {
        lock (_output)
        {
            _output.Write(text);
            _output.Flush();
        }
    }

    internal static string Clip(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }
}

/// <summary>把一轮的过程打到终端：回答边生成边打印，工具调用一行一个，计划、提示单独成行。</summary>
internal sealed class ConsoleObserver : IAgentObserver
{
    private readonly TextWriter _out;
    private readonly ConsoleStyle _style;
    private readonly bool _showThinking;
    private readonly bool _showContent;
    private bool _midLine;
    private bool _thinking;
    private bool _streamed;

    /// <summary>行首正显示着「…」占位（等模型回复时）。有颜色的终端才显示，下一次输出前擦掉。</summary>
    private bool _waiting;

    /// <param name="showContent">
    /// false：只显示过程（工具调用、计划、思考中），回答本身不打——flyknit -p 在终端里跑时用，
    /// 进度写到标准错误，回答最后照常写到标准输出，不重复。
    /// </param>
    public ConsoleObserver(TextWriter output, ConsoleStyle style, bool showThinking, bool showContent = true)
    {
        _out = output;
        _style = style;
        _showThinking = showThinking;
        _showContent = showContent;
    }

    private void Write(string text)
    {
        lock (_out)
        {
            ClearWaiting();
            _out.Write(text);
            _out.Flush();
            if (text.Length > 0)
            {
                _midLine = text[^1] != '\n';
            }
        }
    }

    /// <summary>
    /// 等模型回复时在行首放一个「…思考中」：模型先读完上下文才开口，长任务里要等好几秒，什么都不显示像卡住了。
    /// 只在有颜色的终端显示（要用回车 + 清行把它擦掉；重定向到文件时不写）。
    /// </summary>
    public void ShowWaiting()
    {
        if (!_style.Enabled)
        {
            return;
        }
        lock (_out)
        {
            if (_midLine || _waiting)
            {
                return;
            }
            _out.Write(_style.Dim("  … 思考中"));
            _out.Flush();
            _waiting = true;
        }
    }

    private void ClearWaiting()
    {
        if (_waiting)
        {
            _out.Write("\r\u001b[K");
            _waiting = false;
        }
    }

    /// <summary>当前行没写完时先换行（工具调用、确认、页脚都从新的一行开始）。</summary>
    public void EndLine()
    {
        lock (_out)
        {
            ClearWaiting();
            if (_midLine)
            {
                _out.Write('\n');
                _out.Flush();
                _midLine = false;
            }
            _thinking = false;
        }
    }

    public void OnReasoning(string delta)
    {
        if (!_showThinking || delta.Length == 0)
        {
            return;
        }
        if (!_thinking)
        {
            EndLine();
            Write(_style.Dim("思考："));
            _thinking = true;
        }
        Write(_style.Dim(delta));
    }

    public void OnContent(string delta)
    {
        if (delta.Length == 0 || !_showContent)
        {
            return;
        }
        if (_thinking)
        {
            EndLine();
        }
        _streamed = true;
        Write(delta);
    }

    public void OnAssistantMessage(ChatMessage message)
    {
        // 正常的回答已经一段段打过了；模型服务出错时的说明不走流式，这里补上
        if (_showContent && !_streamed && message.Content.Trim().Length > 0)
        {
            EndLine();
            Write(message.Content.Trim());
        }
        EndLine();
        _streamed = false;
    }

    public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision)
    {
        EndLine();
        Write("  " + _style.Cyan("▸ ") + InteractiveShell.Clip(summary.Length > 0 ? summary : call.Name, 120) + "\n");
    }

    public void OnToolFinished(ToolCall call, ToolResult result, string decision)
    {
        var first = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var mark = result.Ok ? _style.Green("✓") : _style.Red("✗");
        Write("    " + mark + " " + _style.Dim(InteractiveShell.Clip(first, 110)) + "\n");
        ShowWaiting(); // 接下来又要等模型
    }

    public void OnToolMessage(ChatMessage message)
    {
    }

    public void OnPlanUpdated(IReadOnlyList<PlanItem> plan)
    {
        EndLine();
        lock (_out)
        {
            WritePlan(_out, _style, plan);
        }
    }

    public static void WritePlan(TextWriter output, ConsoleStyle style, IReadOnlyList<PlanItem> plan)
    {
        output.Write(style.Bold("  计划") + "\n");
        foreach (var item in plan)
        {
            var mark = item.Status switch
            {
                "completed" => style.Green("☑"),
                "in_progress" => style.Yellow("◐"),
                _ => style.Dim("☐"),
            };
            var text = item.Status == "completed" ? style.Dim(item.Step) : item.Step;
            output.Write($"    {mark} {text}\n");
        }
        output.Flush();
    }

    public void OnContextCompacted(CompactionInfo info)
    {
        EndLine();
        Write(_style.Dim($"  （对话太长，较早的 {info.MessagesCompacted} 条已压缩成摘要）") + "\n");
    }

    public void OnNotice(string text)
    {
        EndLine();
        Write(_style.Yellow("  " + text) + "\n");
    }
}

/// <summary>
/// 在终端里问员工要不要放行。选项和 IDE 一样：允许一次、拒绝；员工端也能设成自动执行的操作多一个「以后自动」
/// （记成和员工端共用的规则）。删除、覆盖等高危操作没有「以后自动」。直接回车、输入读完了都按拒绝。
/// </summary>
internal sealed class ConsoleConfirm : IConfirmationHandler
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ConsoleStyle _style;
    private readonly ConsoleObserver _observer;
    private readonly SemaphoreSlim _one = new(1, 1);
    /// <summary>真终端里由 LineEditor 读回答（退格能用）；为 null 时照旧从输入流读一行。</summary>
    private readonly Func<string, CancellationToken, Task<string?>>? _readLine;

    public ConsoleConfirm(TextReader input, TextWriter output, ConsoleStyle style, ConsoleObserver observer,
        Func<string, CancellationToken, Task<string?>>? readLine = null)
    {
        _readLine = readLine;
        _input = input;
        _output = output;
        _style = style;
        _observer = observer;
    }

    public static ConfirmChoice? Parse(string? answer, bool offersAlways)
    {
        switch ((answer ?? "").Trim().ToLowerInvariant())
        {
            case "y" or "yes" or "1" or "是" or "允许":
                return ConfirmChoice.AllowOnce;
            case "a" or "always" or "2" when offersAlways:
                return ConfirmChoice.AllowAlways;
            case "n" or "no" or "" or "否" or "拒绝":
                return ConfirmChoice.Reject;
            case "3" when offersAlways:
                return ConfirmChoice.Reject;
            case "2" when !offersAlways:
                return ConfirmChoice.Reject;
            default:
                return null;
        }
    }

    public async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        // 并行的工具同时要确认时一个一个问
        await _one.WaitAsync(ct);
        try
        {
            _observer.EndLine();
            var offersAlways = NonInteractiveConfirm.CanAutoApprove(request.Decision);
            var destructive = request.Decision.Effect == CommandEffect.Destructive;
            var w = new StringBuilder();
            w.Append("  ").Append(_style.Yellow(destructive ? "⚠ 高危操作，需要确认：" : "⚠ 需要确认：")).Append(_style.Bold(request.Summary)).Append('\n');
            if (request.Rationale.Trim().Length > 0)
            {
                w.Append("    ").Append(_style.Dim("理由：" + InteractiveShell.Clip(request.Rationale, 200))).Append('\n');
            }
            if (request.Decision.Reason.Length > 0)
            {
                w.Append("    ").Append(_style.Dim(InteractiveShell.Clip(request.Decision.Reason, 200))).Append('\n');
            }
            var options = offersAlways ? "[y] 允许一次  [a] 以后自动  [n] 拒绝" : "[y] 允许一次  [n] 拒绝";
            lock (_output)
            {
                _output.Write(w.ToString());
                _output.Flush();
            }
            while (true)
            {
                var prompt = "    " + options + " > ";
                lock (_output)
                {
                    _output.Write(prompt);
                    _output.Flush();
                }
                var answer = _readLine is null ? await _input.ReadLineAsync(ct) : await _readLine(prompt, ct);
                if (answer is null)
                {
                    return ConfirmChoice.Reject;
                }
                if (Parse(answer, offersAlways) is { } choice)
                {
                    var said = choice switch
                    {
                        ConfirmChoice.AllowOnce => _style.Green("    已允许这一次"),
                        ConfirmChoice.AllowAlways => _style.Green("    已允许，以后同类操作自动执行"),
                        _ => _style.Red("    已拒绝"),
                    };
                    lock (_output)
                    {
                        _output.Write(said + "\n");
                        _output.Flush();
                    }
                    return choice;
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }
}
