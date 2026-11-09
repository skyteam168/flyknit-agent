using System.Globalization;
using System.Text;

namespace Flyknit.Core.Headless;

/// <summary>
/// flyknit 对话模式启动时的欢迎画面：一行渐变色的大字 FLYKNITBUDDY（ANSI Shadow 字体，带立体阴影），下面一个圆角信息框。
/// 终端不够宽时换成一行小字；没有颜色（重定向、NO_COLOR、终端不支持）时只输出纯文字的信息，不画字。
/// </summary>
public static class ConsoleBanner
{
    /// <summary>
    /// ANSI Shadow 字体（同 Gemini CLI 的字标）：█ 是字身，╗ ║ ╝ ═ 这些线是右下的立体阴影。每个字 6 行。
    /// </summary>
    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['F'] = new[] { "███████╗", "██╔════╝", "█████╗  ", "██╔══╝  ", "██║     ", "╚═╝     " },
        ['L'] = new[] { "██╗     ", "██║     ", "██║     ", "██║     ", "███████╗", "╚══════╝" },
        ['Y'] = new[] { "██╗   ██╗", "╚██╗ ██╔╝", " ╚████╔╝ ", "  ╚██╔╝  ", "   ██║   ", "   ╚═╝   " },
        ['K'] = new[] { "██╗  ██╗", "██║ ██╔╝", "█████╔╝ ", "██╔═██╗ ", "██║  ██╗", "╚═╝  ╚═╝" },
        ['N'] = new[] { "███╗   ██╗", "████╗  ██║", "██╔██╗ ██║", "██║╚██╗██║", "██║ ╚████║", "╚═╝  ╚═══╝" },
        ['I'] = new[] { "██╗", "██║", "██║", "██║", "██║", "╚═╝" },
        ['T'] = new[] { "████████╗", "╚══██╔══╝", "   ██║   ", "   ██║   ", "   ██║   ", "   ╚═╝   " },
        ['B'] = new[] { "██████╗ ", "██╔══██╗", "██████╔╝", "██╔══██╗", "██████╔╝", "╚═════╝ " },
        ['U'] = new[] { "██╗   ██╗", "██║   ██║", "██║   ██║", "██║   ██║", "╚██████╔╝", " ╚═════╝ " },
        ['D'] = new[] { "██████╗ ", "██╔══██╗", "██║  ██║", "██║  ██║", "██████╔╝", "╚═════╝ " },
    };

    private const string Word = "FLYKNITBUDDY";
    private const int Rows = 6;
    private const string Margin = "  ";

    /// <summary>渐变的三个点：蓝 → 紫 → 粉。</summary>
    private static readonly (int R, int G, int B)[] Stops = { (79, 142, 247), (139, 92, 246), (232, 104, 154) };
    private static readonly (int R, int G, int B) Frame = (122, 108, 196);

    /// <summary>每一行拼好的字标（还没上色）。</summary>
    internal static string[] Lines()
    {
        var lines = new string[Rows];
        for (var r = 0; r < Rows; r++)
        {
            var sb = new StringBuilder();
            foreach (var ch in Word)
            {
                var glyph = Font[ch];
                var w = glyph.Max(g => g.Length);
                sb.Append(glyph[r].PadRight(w));
            }
            lines[r] = sb.ToString().TrimEnd();
        }
        return lines;
    }

    /// <summary>字标占多宽（终端列数，含左边距）。</summary>
    public static int ArtWidth => Margin.Length + Lines().Max(l => l.Length);

    /// <param name="width">终端宽度（列）；0 表示不知道，按够宽处理</param>
    public static string Render(ConsoleStyle style, int width, string version, IReadOnlyList<string> info, string tip)
    {
        var sb = new StringBuilder();
        if (!style.Enabled)
        {
            // 没有颜色：不画字，信息原样输出（重定向到文件、日志里也看得懂）
            sb.Append($"FlyknitBuddy {version}  命令行对话\n");
            foreach (var line in info)
            {
                sb.Append(line).Append('\n');
            }
            sb.Append(tip).Append("\n\n");
            return sb.ToString();
        }

        sb.Append('\n');
        if (width == 0 || width >= ArtWidth + 2)
        {
            AppendArt(sb, style);
        }
        else
        {
            sb.Append(Margin).Append(style.Rgb(Stops[1], "▍")).Append(' ').Append(style.Bold(Gradient(style, "FlyknitBuddy"))).Append('\n');
        }
        sb.Append('\n');

        var title = $">_ FlyknitBuddy ({version})  命令行对话";
        var lines = new List<string> { title, "" };
        lines.AddRange(info);
        var maxInner = width > 0 ? Math.Max(20, width - Margin.Length - 4) : int.MaxValue;
        var clipped = lines.Select(l => ClipToWidth(l, maxInner)).ToList();
        var inner = clipped.Max(DisplayWidth);
        sb.Append(Margin).Append(style.Rgb(Frame, "╭" + new string('─', inner + 2) + "╮")).Append('\n');
        for (var i = 0; i < clipped.Count; i++)
        {
            var text = clipped[i];
            var shown = i == 0 ? style.Bold(style.Rgb(Stops[1], text)) : style.Dim(text);
            sb.Append(Margin).Append(style.Rgb(Frame, "│")).Append(' ').Append(shown)
              .Append(new string(' ', inner - DisplayWidth(text))).Append(' ').Append(style.Rgb(Frame, "│")).Append('\n');
        }
        sb.Append(Margin).Append(style.Rgb(Frame, "╰" + new string('─', inner + 2) + "╯")).Append('\n');
        sb.Append('\n').Append(Margin).Append(style.Dim(tip)).Append("\n\n");
        return sb.ToString();
    }

    private static void AppendArt(StringBuilder sb, ConsoleStyle style)
    {
        var lines = Lines();
        var columns = lines.Max(l => l.Length);
        foreach (var line in lines)
        {
            sb.Append(Margin);
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (ch == ' ')
                {
                    sb.Append(' ');
                    continue;
                }
                // 字身用渐变色，阴影线用同一列颜色的暗一些的版本，看起来是立体的
                var color = ColorAt(c, columns);
                sb.Append(style.Rgb(ch == '█' ? color : Darker(color), ch.ToString()));
            }
            sb.Append('\n');
        }
    }

    private static (int R, int G, int B) Darker((int R, int G, int B) c) =>
        ((int)(c.R * 0.55), (int)(c.G * 0.55), (int)(c.B * 0.55));

    private static string Gradient(ConsoleStyle style, string text)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            sb.Append(style.Rgb(ColorAt(i, text.Length), text[i].ToString()));
        }
        return sb.ToString();
    }

    internal static (int R, int G, int B) ColorAt(int index, int count)
    {
        var t = count <= 1 ? 0 : (double)index / (count - 1);
        var segment = t < 0.5 ? 0 : 1;
        var local = segment == 0 ? t * 2 : (t - 0.5) * 2;
        var (a, b) = (Stops[segment], Stops[segment + 1]);
        static int Mix(int x, int y, double k) => (int)Math.Round(x + (y - x) * k);
        return (Mix(a.R, b.R, local), Mix(a.G, b.G, local), Mix(a.B, b.B, local));
    }

    /// <summary>终端里占几列：中日韩文字、全角符号占两列。</summary>
    public static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }
        return width;
    }

    private static bool IsWide(int cp) =>
        cp is >= 0x1100 and <= 0x115F
        or >= 0x2E80 and <= 0x303E
        or >= 0x3041 and <= 0x33FF
        or >= 0x3400 and <= 0x4DBF
        or >= 0x4E00 and <= 0x9FFF
        or >= 0xA000 and <= 0xA4CF
        or >= 0xAC00 and <= 0xD7A3
        or >= 0xF900 and <= 0xFAFF
        or >= 0xFE30 and <= 0xFE4F
        or >= 0xFF00 and <= 0xFF60
        or >= 0xFFE0 and <= 0xFFE6
        or >= 0x1F300 and <= 0x1FAFF
        or >= 0x20000 and <= 0x3FFFD;

    /// <summary>超出宽度的截断，末尾加「…」（工作区路径很长时）。</summary>
    internal static string ClipToWidth(string text, int max)
    {
        if (DisplayWidth(text) <= max)
        {
            return text;
        }
        var sb = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var w = IsWide(rune.Value) ? 2 : 1;
            if (used + w > max - 3)
            {
                break;
            }
            sb.Append(rune.ToString());
            used += w;
        }
        return sb.Append("...").ToString();
    }
}
