using System.Text;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Terminal;

/// <summary>
/// 终端输出（伪控制台里跑的命令带颜色、光标控制）→ 给 AI 看的纯文本：去掉 VT 控制序列，换行统一成 \n。
/// 界面上的终端照样拿原始输出画颜色；AI 用 shell_output 读到的是这里的纯文本，和以前普通进程的输出一样。
/// </summary>
public static partial class VtText
{
    // CSI（颜色、光标、清屏）、OSC（窗口标题）、单字符转义（不含 [ 和 ]：它们是 CSI / OSC 的开头，后面还有）；以及剩下的退格、响铃之类控制字符
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\^_]|[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f\x7f]")]
    private static partial Regex Control();

    public static string Strip(string text)
    {
        if (text.IndexOf('\x1b') < 0 && !text.Any(c => c < ' ' && c is not '\r' and not '\n' and not '\t'))
        {
            return text.Replace("\r\n", "\n");
        }
        var plain = Control().Replace(text, "");
        return plain.Replace("\r\n", "\n");
    }

    /// <summary>一个控制序列从 <paramref name="at"/> 开始、在 <paramref name="text"/> 里是完整的吗（不完整的要等下一段输出拼上）。</summary>
    internal static bool CompleteAt(string text, int at)
    {
        var m = Control().Match(text, at);
        return m.Success && m.Index == at;
    }

    /// <summary>参数拼成 Windows 命令行（CreateProcess 的规则：有空格、引号的加引号，引号前的反斜杠加倍）。</summary>
    public static string CommandLine(string file, IEnumerable<string> args)
    {
        var sb = new StringBuilder(Quote(file));
        foreach (var a in args)
        {
            sb.Append(' ').Append(Quote(a));
        }
        return sb.ToString();
    }

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return arg;
        }
        var sb = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', slashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', slashes).Append(c);
            }
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}

/// <summary>
/// 一段段读到的终端输出 → 纯文本。控制序列可能被拆在两段里（一段末尾只有 ESC 或者「ESC [ ? 2」），
/// 不完整的那一截先留着，和下一段拼起来再去掉，不会把半截 ESC 漏给 AI。
/// </summary>
public sealed class VtStripper
{
    /// <summary>一个控制序列最长这么多字符；再长还没结束的不当控制序列（防止一直攒着不放）。</summary>
    private const int MaxPending = 256;

    private string _pending = "";

    public string Feed(string chunk)
    {
        var text = _pending + chunk;
        _pending = "";
        var esc = text.LastIndexOf('\x1b');
        if (esc >= 0 && text.Length - esc < MaxPending && !VtText.CompleteAt(text, esc))
        {
            _pending = text[esc..];
            text = text[..esc];
        }
        return VtText.Strip(text);
    }

    /// <summary>输出结束：留着的半截不是完整的控制序列，去掉 ESC 本身。</summary>
    public string Flush()
    {
        var rest = _pending.Replace("\x1b", "");
        _pending = "";
        return VtText.Strip(rest);
    }
}
