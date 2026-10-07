using System.Text;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Settings;

/// <summary>一种回复语气。<see cref="Playful"/> 的是玩笑类风格，IT 可以统一关掉（安全中心 persona_playful）。</summary>
public sealed record TonePreset(string Key, string Prompt, bool Playful = false);

/// <summary>
/// 个性化：回复语气、AI 对用户的称呼、AI 的名字。
///
/// 语气放进系统提示词里“你的性格与语气”那一段（原来那一段就是 soul.md）：
/// - default：内置的默认语气；
/// - custom：用户自己写的 soul.md；
/// - 其余：下面写好的预设。
/// 语气只影响表达方式，不改变工作准则和安全规则；用户在对话里对语气、长短提的具体要求优先（见 <see cref="ToneNote"/>）。
/// </summary>
public static class Personas
{
    public const string Default = "default";
    public const string Custom = "custom";

    /// <summary>称呼、名字的长度上限。</summary>
    public const int MaxNameLength = 20;

    /// <summary>内置的默认语气（也是 soul.md 第一次生成时的内容）。</summary>
    public const string DefaultTone = "耐心、礼貌、简洁、专业。面对不熟悉电脑的用户时，用简单易懂的话解释。";

    public static readonly IReadOnlyList<TonePreset> Presets = new TonePreset[]
    {
        new(Default, DefaultTone),
        new("professional", "专业严谨：措辞准确、条理清晰，结论要有依据；不确定的地方明确说出来，不夸大、不随意。"),
        new("friendly", "亲和友善：语气温暖、平易近人，适当肯定和鼓励；照顾不熟悉电脑的同事，但不啰嗦。"),
        new("direct", "直言不讳：直接说结论和问题，不绕弯子、不客套、不说空话；有更好的办法就直说。"),
        new("imaginative", "天马行空：可以发挥想象力，善用比喻和类比把事情讲得生动；但数据、步骤和操作必须准确。", Playful: true),
        new("efficient", "高效务实：用最少的文字给出最多的信息，能用列表就不用段落，不加寒暄和总结性的套话。"),
        new("roast", "毒舌吐槽：可以幽默地吐槽、调侃，犀利但绝不伤人、不贬低用户；一涉及出错、安全、数据就立刻回到认真模式。", Playful: true),
        new("socratic", "启发引导：多用提问帮用户理清思路，授人以渔；用户明确只要结果、或者在赶时间时，直接给结果。"),
    };

    /// <summary>附在语气后面：语气不能越过规则，对话里的具体要求优先。</summary>
    public const string ToneNote = "（以上是默认的说话风格，只影响表达方式，不改变工作准则和安全规则；用户在对话里对语气、详略提出的具体要求优先。）";

    public static TonePreset? Find(string? key) => Presets.FirstOrDefault(p => p.Key == key);

    /// <summary>认识的预设或 custom 原样返回，其他的当 default。</summary>
    public static string Normalize(string? key) => key == Custom || Find(key) is not null ? key! : Default;

    /// <summary>
    /// 按公司策略得出实际生效的语气。<paramref name="allowPlayful"/> 为 false 时玩笑类预设退回默认；
    /// <paramref name="allowCustom"/> 为 false 时自定义语气退回默认。
    /// </summary>
    public static string Effective(string? key, bool allowPlayful, bool allowCustom)
    {
        key = Normalize(key);
        if (key == Custom)
        {
            return allowCustom ? Custom : Default;
        }
        return Find(key)!.Playful && !allowPlayful ? Default : key;
    }

    /// <summary>
    /// 放进提示词的语气说明。custom 用 soul.md 的内容（去掉标题，空的就退回默认）。
    /// </summary>
    public static string ToneText(string effectiveKey, string soulFile)
    {
        var text = effectiveKey == Custom ? StripHeadings(soulFile) : Find(effectiveKey)?.Prompt ?? DefaultTone;
        return (text.Length == 0 ? DefaultTone : text) + "\n" + ToneNote;
    }

    /// <summary>以前用户手改过 soul.md、又没选过预设：当作自定义，保留他写的东西。</summary>
    public static string Infer(string? savedKey, string soulFile)
    {
        if (!string.IsNullOrWhiteSpace(savedKey))
        {
            return Normalize(savedKey);
        }
        var soul = StripHeadings(soulFile);
        return soul.Length > 0 && Collapse(soul) != Collapse(DefaultTone) ? Custom : Default;
    }

    /// <summary>称呼和名字：一行、不超过 20 个字、去掉尖括号和引号（它们会被原样放进提示词）。</summary>
    public static string CleanName(string? name)
    {
        var s = Regex.Replace(name ?? "", @"[\r\n\t<>""“”{}]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > MaxNameLength ? s[..MaxNameLength].Trim() : s;
    }

    private static string StripHeadings(string text) =>
        string.Join("\n", text.Replace("\r", "").Split('\n').Where(l => !l.TrimStart().StartsWith('#'))).Trim();

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", "");
}

/// <summary>
/// role.md（“关于我”）的表单读写：几项固定字段 + 其他说明。字段写成“- 部门：质检部”这样的行，
/// 和原来的模板格式一致，用户直接改文件也认得出来。
/// </summary>
public sealed record AboutMe(string Department = "", string Position = "", string Language = "", string Systems = "", string Folders = "", string Other = "")
{
    private static readonly (string Label, Func<AboutMe, string> Get)[] Fields =
    {
        ("部门", a => a.Department),
        ("岗位", a => a.Position),
        ("母语", a => a.Language),
        ("常用系统与软件", a => a.Systems),
        ("常用文件夹", a => a.Folders),
    };

    private static readonly Regex Line = new(@"^[-*]\s*(?<label>[^：:]+)[：:]\s*(?<value>.*)$", RegexOptions.Compiled);

    public static AboutMe Parse(string text)
    {
        var values = new Dictionary<string, string>();
        var other = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                continue;
            }
            var m = Line.Match(line);
            if (m.Success && Fields.Any(f => f.Label == m.Groups["label"].Value.Trim()))
            {
                values[m.Groups["label"].Value.Trim()] = m.Groups["value"].Value.Trim();
            }
            else
            {
                other.Add(raw.TrimEnd());
            }
        }
        string V(string label) => values.TryGetValue(label, out var v) ? v : "";
        return new AboutMe(V("部门"), V("岗位"), V("母语"), V("常用系统与软件"), V("常用文件夹"), string.Join("\n", other).Trim());
    }

    public string Render()
    {
        var sb = new StringBuilder("# 关于我\n\n");
        foreach (var (label, get) in Fields)
        {
            sb.Append("- ").Append(label).Append('：').Append(OneLine(get(this))).Append('\n');
        }
        if (Other.Trim().Length > 0)
        {
            sb.Append('\n').Append(Other.Trim().Replace("\r", "")).Append('\n');
        }
        return sb.ToString();
    }

    private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s*[\r\n]+\s*", "；").Trim();
}
