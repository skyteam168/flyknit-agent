using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Headless;

/// <summary>
/// flyknit 对话模式的输入行（真终端里用；输入被重定向时照旧用 ReadLine）。
///
/// 以前是「第一个按键单独读（为了擦掉灰色提示）+ 剩下的交给控制台 ReadLine」：控制台的行编辑不知道第一个字，
/// 退格删不掉它；ReadLine 也做不了 Tab 补全。现在整行自己读：
/// - 打字、粘贴、中文输入法上屏的字照常显示；中日韩文字按两列算，emoji 这类代理对算一个字；
/// - 退格删最后一个字（跨行时也对得上）；Esc / Ctrl+U 清空整行；
/// - Tab：输入以 / 开头时补全命令（只有一个就补全，有好几个先补公共部分，再按一次列出全部）；
/// - ↑ / ↓：翻这次打开以来输过的话；
/// - 行空着时显示灰色提示；
/// - 回车返回这一行；空行上按 Ctrl+D / Ctrl+Z 当作结束输入（返回 null）。
/// 光标只在行尾（不支持左右移动插入），换来的是在各种终端里都不会错位。
/// </summary>
public sealed class LineEditor
{
    private readonly Func<CancellationToken, Task<ConsoleKeyInfo?>> _readKey;
    private readonly TextWriter _output;
    private readonly ConsoleStyle _style;
    private readonly int _width;
    private readonly object _gate = new();
    private readonly List<string> _history = new();

    /// <summary>每个字一项（代理对合成一项），和它在终端里占几列。</summary>
    private readonly List<(string Text, int Width)> _chars = new();
    private int _total;
    private int _promptWidth;
    private string _prompt = "";
    private string? _placeholder;
    private bool _placeholderShown;
    private char? _highSurrogate;
    private bool _clearRequested;
    private string? _lastTabPrefix;

    /// <param name="terminalWidth">终端宽度（列），0 = 不知道（按不换行处理）</param>
    public LineEditor(Func<CancellationToken, Task<ConsoleKeyInfo?>> readKey, TextWriter output, ConsoleStyle style, int terminalWidth)
    {
        _readKey = readKey;
        _output = output;
        _style = style;
        _width = terminalWidth > 0 ? terminalWidth : int.MaxValue / 4;
    }

    /// <summary>Tab 补全的候选（比如 /help、/new……）。</summary>
    public IReadOnlyList<string> Completions { get; set; } = Array.Empty<string>();

    public string Text => string.Concat(_chars.Select(c => c.Text));

    private static readonly Regex Ansi = new("\u001b\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>
    /// 读一行。prompt 由调用方先写好（如「> 」），这里只要知道它占几列；placeholder 是行空着时显示的灰字。
    /// </summary>
    public async Task<string?> ReadLineAsync(string prompt, string? placeholder, CancellationToken ct, bool remember = true)
    {
        lock (_gate)
        {
            _chars.Clear();
            _total = 0;
            _prompt = prompt;
            // 提示符可能带颜色：算宽度时去掉转义序列
            _promptWidth = ConsoleBanner.DisplayWidth(Ansi.Replace(prompt, ""));
            _placeholder = placeholder;
            _highSurrogate = null;
            _clearRequested = false;
            _lastTabPrefix = null;
            ShowPlaceholder();
        }
        var historyIndex = _history.Count;
        while (true)
        {
            var key = await _readKey(ct);
            if (key is not { } k)
            {
                return null; // 输入流结束
            }
            lock (_gate)
            {
                if (_clearRequested)
                {
                    // Ctrl+C 已经换了一行重新显示提示符：这一行重新开始
                    _clearRequested = false;
                    _chars.Clear();
                    _total = 0;
                    _placeholderShown = false;
                }
                if (k.Key != ConsoleKey.Tab)
                {
                    _lastTabPrefix = null;
                }
                switch (k.Key)
                {
                    case ConsoleKey.Enter:
                        HidePlaceholder();
                        Write("\n");
                        var line = Text;
                        if (remember && line.Trim().Length > 0 && (_history.Count == 0 || _history[^1] != line))
                        {
                            _history.Add(line);
                        }
                        return line;
                    case ConsoleKey.Backspace:
                        if (_chars.Count > 0)
                        {
                            var before = _total;
                            _total -= _chars[^1].Width;
                            _chars.RemoveAt(_chars.Count - 1);
                            Redraw(before);
                        }
                        continue;
                    case ConsoleKey.Escape:
                        Replace("");
                        continue;
                    case ConsoleKey.Tab:
                        Complete();
                        continue;
                    case ConsoleKey.UpArrow:
                        if (historyIndex > 0)
                        {
                            historyIndex--;
                            Replace(_history[historyIndex]);
                        }
                        continue;
                    case ConsoleKey.DownArrow:
                        if (historyIndex < _history.Count)
                        {
                            historyIndex++;
                            Replace(historyIndex < _history.Count ? _history[historyIndex] : "");
                        }
                        continue;
                }
                var c = k.KeyChar;
                if ((k.Modifiers & ConsoleModifiers.Control) != 0 && k.Key == ConsoleKey.U)
                {
                    Replace("");
                    continue;
                }
                if ((c == '\u0004' || c == '\u001a') && _chars.Count == 0)
                {
                    Write("\n");
                    return null; // Ctrl+D / Ctrl+Z：结束
                }
                if (c == '\0' || (char.IsControl(c) && c != '\t'))
                {
                    continue; // 方向键、功能键这类没有字的按键
                }
                Insert(c);
            }
        }
    }

    /// <summary>
    /// 空闲时按了 Ctrl+C（由 Console.CancelKeyPress 在别的线程里调）：在新的一行写提示，重新显示提示符，这一行清空。
    /// </summary>
    public void Interrupted(string message)
    {
        lock (_gate)
        {
            Write("\n" + message + "\n" + _prompt);
            _clearRequested = true;
            _placeholderShown = false;
        }
    }

    // ---------- 编辑 ----------

    private void Insert(char c)
    {
        string text;
        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c; // 等低位代理到了再算一个字
            return;
        }
        if (char.IsLowSurrogate(c) && _highSurrogate is { } high)
        {
            text = new string(new[] { high, c });
            _highSurrogate = null;
        }
        else
        {
            text = c.ToString();
        }
        HidePlaceholder();
        var w = ConsoleBanner.DisplayWidth(text);
        _chars.Add((text, w));
        _total += w;
        Write(text); // 在行尾追加：直接写，不重画（粘贴一大段也不会慢）
    }

    private void Replace(string text)
    {
        var before = _total;
        _chars.Clear();
        _total = 0;
        foreach (var e in Elements(text))
        {
            var w = ConsoleBanner.DisplayWidth(e);
            _chars.Add((e, w));
            _total += w;
        }
        Redraw(before);
    }

    private void Complete()
    {
        var text = Text;
        if (!text.StartsWith('/') || text.Contains(' '))
        {
            return;
        }
        var hits = Completions.Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        if (hits.Count == 0)
        {
            return;
        }
        if (hits.Count == 1)
        {
            Replace(hits[0] + " ");
            return;
        }
        var common = CommonPrefix(hits);
        if (common.Length > text.Length)
        {
            Replace(common);
            _lastTabPrefix = common;
            return;
        }
        if (_lastTabPrefix == text)
        {
            // 第二次按 Tab：列出全部，再把这一行原样显示回来
            HidePlaceholder();
            Write("\n" + _style.Dim(string.Join("  ", hits)) + "\n" + _prompt + text);
            _lastTabPrefix = null;
            return;
        }
        _lastTabPrefix = text;
    }

    internal static string CommonPrefix(IReadOnlyList<string> items)
    {
        var prefix = items[0];
        foreach (var s in items.Skip(1))
        {
            var n = 0;
            while (n < prefix.Length && n < s.Length && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(s[n]))
            {
                n++;
            }
            prefix = prefix[..n];
        }
        return prefix;
    }

    private static IEnumerable<string> Elements(string text)
    {
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            yield return (string)e.Current;
        }
    }

    // ---------- 显示 ----------

    /// <summary>
    /// 光标在输入了 total 列之后的那一刻在第几行（相对提示符那一行）。
    /// 正好写满一行时终端把光标停在最后一列（等下一个字才换行），所以算在上一行。
    /// </summary>
    internal static int CursorRow(int promptWidth, int total, int width)
    {
        var pos = promptWidth + total;
        if (total > 0 && pos % width == 0)
        {
            return pos / width - 1;
        }
        return pos / width;
    }

    /// <summary>从「输入了 before 列」的光标位置回到输入开头，清掉后面的，再把现在的内容写出来。</summary>
    private void Redraw(int before)
    {
        var up = CursorRow(_promptWidth, before, _width);
        var sb = new StringBuilder();
        if (up > 0)
        {
            sb.Append($"\u001b[{up}A");
        }
        sb.Append('\r');
        var col = _promptWidth % _width;
        if (col > 0)
        {
            sb.Append($"\u001b[{col}C");
        }
        sb.Append("\u001b[J");
        sb.Append(Text);
        Write(sb.ToString());
        _placeholderShown = false;
        if (_chars.Count == 0)
        {
            ShowPlaceholder();
        }
    }

    private void ShowPlaceholder()
    {
        if (string.IsNullOrEmpty(_placeholder) || _placeholderShown)
        {
            return;
        }
        var w = ConsoleBanner.DisplayWidth(_placeholder);
        Write(_style.Dim(_placeholder) + $"\u001b[{w}D");
        _placeholderShown = true;
    }

    private void HidePlaceholder()
    {
        if (_placeholderShown)
        {
            Write("\u001b[K");
            _placeholderShown = false;
        }
    }

    private void Write(string text)
    {
        _output.Write(text);
        _output.Flush();
    }
}
