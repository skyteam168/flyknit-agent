namespace Flyknit.Core.Settings;

/// <summary>一条快捷键命令。界面上的名字按 Id 到 i18n 里取，这里不放任何界面文案。</summary>
/// <param name="Id">命令标识，同时是 i18n 的 key。</param>
/// <param name="Group">分组：task / chat / view / window。</param>
/// <param name="Default">默认按键，例如 "Ctrl+N"。</param>
/// <param name="Global">true 表示全局热键（程序不在前台也生效），由 WPF 注册。</param>
/// <param name="Fixed">true 表示不允许改，例如 Enter 发送。</param>
public sealed record ShortcutCommand(string Id, string Group, string Default, bool Global = false, bool Fixed = false);

/// <summary>
/// 快捷键注册表。
///
/// 命令、默认按键、分组都在这张表里；界面按表渲染，按键处理也按表分派。
/// 加一条快捷键 = 在表里加一行并在界面接上处理函数，不用改设置面板，三种语言的名字各加一条就行。
///
/// 用户改过的按键只存「和默认不一样的那些」（AppSettings.Shortcuts）。
/// 这样以后调整默认值，没自定义过的用户会自动跟上新默认值。
/// </summary>
public static class Shortcuts
{
    public static readonly IReadOnlyList<ShortcutCommand> All = new[]
    {
        // 任务
        new ShortcutCommand("conversation.new", "task", "Ctrl+N"),
        new ShortcutCommand("conversation.prev", "task", "Ctrl+["),
        new ShortcutCommand("conversation.next", "task", "Ctrl+]"),
        new ShortcutCommand("conversation.rename", "task", "F2"),
        new ShortcutCommand("conversation.delete", "task", "Ctrl+Delete"),

        // 对话
        new ShortcutCommand("chat.send", "chat", "Enter", Fixed: true),
        new ShortcutCommand("chat.newline", "chat", "Shift+Enter", Fixed: true),
        new ShortcutCommand("chat.stop", "chat", "Escape"),
        new ShortcutCommand("chat.search", "chat", "Ctrl+F"),
        new ShortcutCommand("chat.regenerate", "chat", "Ctrl+R"),
        new ShortcutCommand("chat.focusInput", "chat", "Ctrl+L"),

        // 视图
        new ShortcutCommand("sidebar.toggle", "view", "Ctrl+B"),
        new ShortcutCommand("preview.toggle", "view", "Ctrl+Shift+B"),
        new ShortcutCommand("font.increase", "view", "Ctrl+="),
        new ShortcutCommand("font.decrease", "view", "Ctrl+-"),
        new ShortcutCommand("font.reset", "view", "Ctrl+0"),

        // 面板
        new ShortcutCommand("settings.open", "panel", "Ctrl+,"),
        new ShortcutCommand("skills.open", "panel", "Ctrl+Shift+S"),
        new ShortcutCommand("memory.open", "panel", "Ctrl+Shift+M"),
        new ShortcutCommand("schedules.open", "panel", "Ctrl+Shift+T"),
        new ShortcutCommand("usage.open", "panel", "Ctrl+Shift+U"),

        // 窗口
        new ShortcutCommand("window.fullscreen", "window", "F11"),
        new ShortcutCommand("window.toggle", "window", "Ctrl+Alt+Space", Global: true),

        // 划词翻译：在任何程序里选中文字后按下，就地弹出译文
        new ShortcutCommand("selection.translate", "window", "Ctrl+Alt+T", Global: true),
    };

    /// <summary>修饰键的固定顺序，保证 "Shift+Ctrl+B" 和 "Ctrl+Shift+B" 算同一个键。</summary>
    private static readonly string[] ModifierOrder = { "Ctrl", "Alt", "Shift", "Meta" };

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["control"] = "Ctrl", ["ctrl"] = "Ctrl",
        ["alt"] = "Alt", ["option"] = "Alt",
        ["shift"] = "Shift",
        ["meta"] = "Meta", ["win"] = "Meta", ["cmd"] = "Meta",
        ["esc"] = "Escape", ["escape"] = "Escape",
        ["del"] = "Delete", ["delete"] = "Delete",
        ["space"] = "Space", [" "] = "Space",
        ["plus"] = "=", ["add"] = "=",
        ["minus"] = "-", ["subtract"] = "-",
        ["arrowup"] = "Up", ["arrowdown"] = "Down", ["arrowleft"] = "Left", ["arrowright"] = "Right",
    };

    public static ShortcutCommand? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// 把按键写法统一成一个形式，便于比较和存储。
    /// "shift+ctrl+b" / "Ctrl+Shift+B" / "CTRL + SHIFT + b" 都变成 "Ctrl+Shift+B"。
    /// </summary>
    public static string Normalize(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return "";
        }
        var parts = binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = new List<string>();
        string key = "";
        foreach (var raw in parts)
        {
            var token = KeyAliases.TryGetValue(raw, out var alias) ? alias : raw;
            if (ModifierOrder.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                var canonical = ModifierOrder.First(m => m.Equals(token, StringComparison.OrdinalIgnoreCase));
                if (!mods.Contains(canonical))
                {
                    mods.Add(canonical);
                }
            }
            else
            {
                // 单个字母统一成大写；F11、Escape 这类保持原样
                key = token.Length == 1 ? token.ToUpperInvariant() : Capitalize(token);
            }
        }
        if (key.Length == 0)
        {
            return ""; // 只有修饰键不算一个快捷键
        }
        mods.Sort((a, b) => Array.IndexOf(ModifierOrder, a).CompareTo(Array.IndexOf(ModifierOrder, b)));
        return mods.Count > 0 ? string.Join("+", mods) + "+" + key : key;
    }

    private static string Capitalize(string s) =>
        s.Length switch
        {
            0 => s,
            1 => s.ToUpperInvariant(),
            _ when s.Length == 2 && s[0] is 'f' or 'F' && char.IsDigit(s[1]) => "F" + s[1],
            _ when s.Length == 3 && s[0] is 'f' or 'F' && char.IsDigit(s[1]) && char.IsDigit(s[2]) => "F" + s[1..],
            _ => char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant(),
        };

    /// <summary>默认值 + 用户改动，得到最终生效的绑定表。</summary>
    public static Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string>? overrides)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cmd in All)
        {
            var custom = overrides is not null && overrides.TryGetValue(cmd.Id, out var v) ? Normalize(v) : "";
            // 固定的命令不接受改动；改动为空字符串表示用户把这条禁用了
            map[cmd.Id] = cmd.Fixed ? Normalize(cmd.Default)
                : overrides is not null && overrides.ContainsKey(cmd.Id) ? custom
                : Normalize(cmd.Default);
        }
        return map;
    }

    /// <summary>这个按键能不能用作快捷键。</summary>
    public static (bool Ok, string Reason) Validate(string? binding)
    {
        var normalized = Normalize(binding);
        if (normalized.Length == 0)
        {
            return (false, "empty");
        }
        var parts = normalized.Split('+');
        var key = parts[^1];
        var hasModifier = parts.Length > 1;

        // 单个可打印字符不带修饰键会把正常打字也吃掉
        if (!hasModifier && key.Length == 1)
        {
            return (false, "needsModifier");
        }
        // 这几个键系统或界面另有用途，让出来
        if (normalized is "Ctrl+C" or "Ctrl+V" or "Ctrl+X" or "Ctrl+A" or "Ctrl+Z" or "Alt+F4" or "Ctrl+Alt+Delete")
        {
            return (false, "reserved");
        }
        return (true, "");
    }

    /// <summary>这个按键是不是已经被别的命令占了，是的话返回那条命令的 id。</summary>
    public static string? Conflict(string id, string binding, IReadOnlyDictionary<string, string> resolved)
    {
        var normalized = Normalize(binding);
        if (normalized.Length == 0)
        {
            return null;
        }
        foreach (var (otherId, other) in resolved)
        {
            if (otherId != id && other.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return otherId;
            }
        }
        return null;
    }
}

/// <summary>界面字号的几档倍率。只给固定档位，避免用户拖出一个看不清或撑破布局的值。</summary>
public static class FontScales
{
    public static readonly IReadOnlyList<double> All = new[] { 0.85, 0.925, 1.0, 1.1, 1.25 };

    public const double Default = 1.0;

    /// <summary>把任意输入吸附到最近的档位。</summary>
    public static double Nearest(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return Default;
        }
        var best = Default;
        var bestGap = double.MaxValue;
        foreach (var step in All)
        {
            var gap = Math.Abs(step - value);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = step;
            }
        }
        return best;
    }

    /// <summary>往上 / 往下挪一档（Ctrl+= / Ctrl+-）。</summary>
    public static double Step(double current, int direction)
    {
        var index = All.ToList().IndexOf(Nearest(current));
        return All[Math.Clamp(index + direction, 0, All.Count - 1)];
    }
}
