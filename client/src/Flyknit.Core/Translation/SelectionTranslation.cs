using System.Text;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Translation;

/// <summary>
/// 划词翻译里和界面无关的部分：整理选中的文字、猜它是什么语言、决定译成什么语言、发请求。
///
/// 取词（UI Automation / 模拟复制）和弹窗在 WPF 外壳里，这里只放能单测的逻辑。
/// </summary>
public static class SelectionTranslation
{
    /// <summary>一次最多翻译这么多字。划词是查一段话，不是翻一整份文件——整份文件请用翻译模式。</summary>
    public const int MaxChars = 4000;

    /// <summary>
    /// 整理选中的文字：统一换行、去掉首尾空白和行尾空格、压掉连续的空行，太长就截断。
    /// 从 Excel 复制出来的单元格带一个结尾换行，从网页复制的常有一堆空行，都在这里处理掉。
    /// </summary>
    public static (string Text, bool Truncated) Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("", false);
        }
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\0', ' ').Split('\n');
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Trim().Length == 0)
            {
                blank++;
                continue;
            }
            if (sb.Length > 0)
            {
                sb.Append(blank > 0 ? "\n\n" : "\n");
            }
            sb.Append(trimmed);
            blank = 0;
        }
        var text = sb.ToString().Trim();
        if (text.Length <= MaxChars)
        {
            return (text, false);
        }
        var cut = MaxChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--; // 别把一个 emoji 劈成两半
        }
        return (text[..cut], true);
    }

    /// <summary>
    /// 粗略判断文字是什么语言，返回翻译用的语言代码；认不出返回空字符串。
    ///
    /// 只用来决定「要不要换个目标语言」，不追求准确：按文字系统数字符，
    /// 汉字里夹假名算日语，拉丁字母里有越南语特有的字母算越南语，其余拉丁字母算英语。
    /// </summary>
    public static string GuessLanguage(string text)
    {
        int han = 0, kana = 0, hangul = 0, thai = 0, khmer = 0, latin = 0, viet = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case >= '一' and <= '鿿':
                case >= '㐀' and <= '䶿':
                case >= '豈' and <= '﫿':
                    han++;
                    break;
                case >= '぀' and <= 'ヿ':
                    kana++;
                    break;
                case >= '가' and <= '힯':
                case >= 'ᄀ' and <= 'ᇿ':
                    hangul++;
                    break;
                case >= '฀' and <= '๿':
                    thai++;
                    break;
                case >= 'ក' and <= '៿':
                    khmer++;
                    break;
                case >= 'a' and <= 'z':
                case >= 'A' and <= 'Z':
                    latin++;
                    break;
                default:
                    if (IsVietnameseLetter(ch))
                    {
                        latin++;
                        viet++;
                    }
                    else if (char.IsLetter(ch) && ch < 'ɐ')
                    {
                        latin++; // 其他带调号的拉丁字母（é、ü…）
                    }
                    break;
            }
        }

        // 中文里夹几个英文单词、型号很常见，所以汉字只要占到一定比例就算中文
        var cjk = han + kana;
        var total = cjk + hangul + thai + khmer + latin;
        if (total == 0)
        {
            return "";
        }
        if (kana > 0 && kana * 10 >= cjk)
        {
            return "ja";
        }
        var best = new[] { ("zh-CN", han * 3), ("ko", hangul * 3), ("th", thai), ("km", khmer), ("latin", latin) }
            .OrderByDescending(x => x.Item2)
            .First();
        if (best.Item2 == 0)
        {
            return "";
        }
        if (best.Item1 != "latin")
        {
            return best.Item1;
        }
        // 这些字母别的语言基本不用，出现一个就够了
        return viet > 0 ? "vi" : "en";
    }

    /// <summary>越南语特有、其他常见拉丁语言里基本不出现的字母（含声调组合）。</summary>
    private static bool IsVietnameseLetter(char ch)
    {
        // đ ơ ư ă 本身就是越南语的标志
        if (ch is 'đ' or 'Đ' or 'ơ' or 'Ơ' or 'ư' or 'Ư' or 'ă' or 'Ă')
        {
            return true;
        }
        // 预组合的声调字母（ạ ả ấ ầ ẩ … ỹ）集中在 U+1EA0–U+1EF9
        return ch is >= 'Ạ' and <= 'ỹ';
    }

    /// <summary>界面语言对应的翻译语言代码。</summary>
    public static string UiToTranslateCode(string uiLanguage) => uiLanguage switch
    {
        "vi-VN" => "vi",
        "en-US" => "en",
        _ => "zh-CN",
    };

    /// <summary>
    /// 决定这次译成什么语言。
    ///
    /// preferred 是用户在弹窗里选过的目标语言，"auto"（没选过）就用界面语言。
    /// 原文已经是目标语言时换一个：依次试界面语言、中文、越南语，取第一个和原文不同的——
    /// 越南同事划到越南语就译成中文，中国同事划到中文就译成越南语。
    /// </summary>
    public static (string Target, string Fallback) PickTarget(string text, string? preferred, string uiLanguage)
    {
        var target = !string.IsNullOrEmpty(preferred) && preferred != "auto" && Languages.TranslateTargets.ContainsKey(preferred)
            ? preferred
            : UiToTranslateCode(uiLanguage);
        if (GuessLanguage(text) == target)
        {
            target = Alternative(target, uiLanguage);
        }
        return (target, Alternative(target, uiLanguage));
    }

    /// <summary>不是 language 的另一种常用语言：依次试界面语言、中文、越南语。</summary>
    public static string Alternative(string language, string uiLanguage) =>
        new[] { UiToTranslateCode(uiLanguage), "zh-CN", "vi" }.First(c => c != language);

    /// <summary>一个词或一个短语（而不是一句话）。这时候提示词允许给出几个常见意思。</summary>
    public static bool IsLookup(string text)
    {
        if (text.Length == 0 || text.Contains('\n'))
        {
            return false;
        }
        var han = text.Count(c => c is >= '一' and <= '鿿');
        if (han > 0)
        {
            return text.Length <= 6;
        }
        return text.Length <= 40 && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3
            && !text.TrimEnd().EndsWith('.') && !text.TrimEnd().EndsWith('?') && !text.TrimEnd().EndsWith('!');
    }

    /// <summary>组装请求。每次划词都是一个独立的会话 id，服务端按它归档。</summary>
    public static ChatRequest BuildRequest(string text, string target, string fallback, string conversationId) => new()
    {
        Scene = Scenes.Translate,
        Stream = true,
        ConversationId = conversationId,
        // 弹窗里等的是译文，不是思考过程；关掉思考能快好几秒
        ExtraBody = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["enable_thinking"] = false },
        Messages = new[]
        {
            ChatMessage.System(PromptBuilder.TranslatePrompt("auto", target, fallback, IsLookup(text))),
            ChatMessage.User(text),
        },
    };

    /// <summary>新的划词会话 id（服务端截断到 64 个字符，这里远小于它）。</summary>
    public static string NewConversationId() => "sel-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// 去掉推理模型混进正文的 &lt;think&gt; 段。流式输出时还没收到结束标记，就先什么都不显示。
    /// </summary>
    public static string StripThinking(string content)
    {
        var start = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return content;
        }
        var end = content.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return content[..start];
        }
        return (content[..start] + content[(end + 8)..]).TrimStart();
    }
}
