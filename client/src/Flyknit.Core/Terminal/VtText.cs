using System.Text;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Terminal;

/// <summary>
/// 终端输出（伪控制台里跑的命令带颜色、光标控制）→ 给 AI 看的纯文本：去掉 VT 控制序列，换行统一成 \n。
/// 界面上的终端照样拿原始输出画颜色；AI 用 shell_output 读到的是这里的纯文本，和以前普通进程的输出一样。
/// </summary>
public static partial class VtText
{
    // CSI（颜色、光标、清屏）、OSC（窗口标题）、单字符转义；以及剩下的退格、响铃之类控制字符
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]|[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f\x7f]")]
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
