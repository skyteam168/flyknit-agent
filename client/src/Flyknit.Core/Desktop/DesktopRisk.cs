using System.Text.RegularExpressions;
using Flyknit.Core.Security;

namespace Flyknit.Core.Desktop;

/// <summary>
/// 允许 AI 按的键。只认一张固定的表：Win 键、Alt+Tab、Ctrl+Alt+… 这类能跳到别的程序、打开「运行」框的组合一律不认，
/// 否则 AI 能借键盘绕过命令规则（Win+R 打开运行框再敲命令）。
/// </summary>
public static class DesktopKeys
{
    /// <summary>解析好的组合：修饰键 + 一个主键。</summary>
    public sealed record Combo(bool Ctrl, bool Shift, bool Alt, string Key)
    {
        public override string ToString() =>
            string.Join("+", new[] { Ctrl ? "Ctrl" : null, Shift ? "Shift" : null, Alt ? "Alt" : null, Key }.Where(x => x is not null));
    }

    /// <summary>能单独按（或配 Shift）的主键。</summary>
    public static readonly IReadOnlySet<string> NamedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Enter", "Tab", "Esc", "Space", "Backspace", "Delete", "Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["return"] = "Enter", ["escape"] = "Esc", ["del"] = "Delete", ["back"] = "Backspace", ["pgup"] = "PageUp", ["pgdn"] = "PageDown",
        ["pagedown"] = "PageDown", ["pageup"] = "PageUp", ["arrowup"] = "Up", ["arrowdown"] = "Down", ["arrowleft"] = "Left", ["arrowright"] = "Right",
        ["control"] = "Ctrl", ["ctl"] = "Ctrl",
    };

    /// <summary>解析 "Ctrl+S"、"shift+tab"、"Enter"。不认识或不允许的返回 null，并给出原因。</summary>
    public static Combo? Parse(string? text, out string error)
    {
        error = "";
        var parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Aliases.TryGetValue(p, out var a) ? a : p).ToList();
        if (parts.Count == 0)
        {
            error = "没有指定按键";
            return null;
        }
        bool ctrl = false, shift = false, alt = false;
        foreach (var mod in parts.Take(parts.Count - 1))
        {
            switch (mod.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win" or "windows" or "meta" or "cmd" or "super":
                    error = "不允许按 Win 键组合（会打开开始菜单、运行框等系统界面）";
                    return null;
                default:
                    error = $"不认识的修饰键：{mod}";
                    return null;
            }
        }
        var key = parts[^1];
        if (key.Equals("win", StringComparison.OrdinalIgnoreCase) || key.Equals("windows", StringComparison.OrdinalIgnoreCase))
        {
            error = "不允许按 Win 键（会打开开始菜单）";
            return null;
        }
        var named = NamedKeys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        var letter = key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) ? key.ToUpperInvariant() : null;
        if (named is null && letter is null)
        {
            error = $"不认识的按键：{key}";
            return null;
        }
        if (ctrl && alt)
        {
            error = "不允许 Ctrl+Alt 组合（系统级快捷键）";
            return null;
        }
        if (alt && (named is "Tab" or "Esc" or "Space"))
        {
            error = "不允许切换程序、打开系统菜单的组合键（Alt+Tab、Alt+Esc、Alt+Space）";
            return null;
        }
        if (ctrl && named is "Esc")
        {
            error = "不允许 Ctrl+Esc、Ctrl+Shift+Esc（开始菜单、任务管理器）";
            return null;
        }
        if (letter is not null && !ctrl && !alt)
        {
            error = "单个字母、数字请用 desktop_type 填写";
            return null;
        }
        return new Combo(ctrl, shift, alt, named ?? letter!);
    }
}

/// <summary>
/// 桌面操作的风险判定（参照浏览器的 <see cref="Browser.BrowserRisk"/>）：
/// 1. <b>永远不能操作的程序</b>（写死、IT 也打不开）：命令行和系统管理工具（会绕过命令规则）、资源管理器（会绕过删除规则）、
///    浏览器（有专门的浏览器工具和外网规则）、远程桌面、安装程序、FlyknitBuddy 自己（AI 不能自己点「允许」）；
/// 2. 其余程序必须在 IT 的<b>程序白名单</b>里（命令策略 desktop_apps；空 = 一个都不能操作）；
/// 3. 密码框不代填；身份证、银行卡先问；会提交、保存、删除的按钮和按键先问；「仅可查看」权限下不能填写、不能点提交；
/// 4. IT 打开「每一步都确认」（默认开）时，点击、填写、按键都先问员工。
/// 这里的确认都不能记成「以后自动」（规则候选为空）。
/// </summary>
public static partial class DesktopRisk
{
    /// <summary>永远不能操作的程序（进程名，不带 .exe，小写比较）。</summary>
    public static readonly IReadOnlySet<string> NeverAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // 命令行、脚本宿主：操作它们等于绕过命令规则
        "cmd", "powershell", "pwsh", "powershell_ise", "conhost", "openconsole", "windowsterminal", "wt", "wsl", "bash", "mintty",
        "wscript", "cscript", "mshta", "rundll32", "regsvr32",
        // 系统管理
        "regedit", "regedt32", "mmc", "taskmgr", "msconfig", "control", "systemsettings", "gpedit", "secpol", "services",
        "compmgmt", "devmgmt", "diskmgmt", "eventvwr", "perfmon", "resmon", "lusrmgr", "certmgr", "netplwiz", "sysdm",
        "useraccountcontrolsettings", "msiexec", "consent", "credentialuibroker", "lockapp", "logonui", "winlogon",
        // 资源管理器、桌面、开始菜单：能删改文件、打开运行框
        "explorer", "startmenuexperiencehost", "searchhost", "searchapp", "shellexperiencehost",
        // 浏览器：用 browser_* 工具（有外网和提交规则）
        "msedge", "chrome", "firefox", "iexplore", "opera", "brave", "vivaldi", "360se", "360chrome", "qqbrowser", "sogouexplorer",
        // 远程控制
        "mstsc", "teamviewer", "anydesk", "todesk", "sunloginclient",
        // FlyknitBuddy 自己（不能自己点确认）
        "flyknitbuddy", "flyknit.client", "flyknit", "flyknit.agent", "flyknit.setup", "flyknit.updater", "msedgewebview2",
    };

    [GeneratedRegex(@"提交|确认|确定|保存|删除|发送|支付|付款|下单|审批|审核|同意|驳回|发布|注销|退出|转账|签收|作废|过账|记账|入库|出库|结案|关闭单据|submit|save|delete|remove|send|pay|confirm|approve|reject|publish|post|order|purchase|sign ?out|log ?out|transfer|apply|ok\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubmitLike();

    [GeneratedRegex(@"密码|口令|验证码|动态码|password|passwd|pwd|passcode|otp|cvv|cvc|pin\b", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    [GeneratedRegex(@"身份证|银行卡|卡号|card ?number|id ?number|社保", RegexOptions.IgnoreCase)]
    private static partial Regex Personal();

    public static string Normalize(string process)
    {
        // 后台可能填了完整路径：按 \ 和 / 都切（不依赖运行平台的路径规则）
        var p = (process ?? "").Trim().Split('\\', '/')[^1].Trim();
        return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
    }

    /// <summary>这个程序能不能让 AI 操作；不能时给出原因。</summary>
    public static bool AppAllowed(string process, IReadOnlyCollection<string> allowList, out string reason)
    {
        var name = Normalize(process);
        if (NeverAllowed.Contains(name))
        {
            reason = name.ToLowerInvariant() switch
            {
                "msedge" or "chrome" or "firefox" or "iexplore" or "opera" or "brave" or "vivaldi" or "360se" or "360chrome" or "qqbrowser" or "sogouexplorer"
                    => $"「{name}」是浏览器，请改用 browser_* 浏览器工具操作网页",
                "explorer" => "资源管理器 / 桌面不能用桌面操作控制，文件请用 read_file / write_file 等工具处理",
                _ when name.StartsWith("flyknit", StringComparison.OrdinalIgnoreCase) || name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
                    => "不能操作 FlyknitBuddy 自己的窗口",
                _ => $"「{name}」是系统 / 命令行类程序，出于安全不能用桌面操作控制",
            };
            return false;
        }
        if (!allowList.Any(a => Normalize(a).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            reason = allowList.Count == 0
                ? "IT 还没有设置允许 AI 操作的程序（后台「安全中心 → 电脑操作 → 允许的程序」）"
                : $"「{name}」不在 IT 允许 AI 操作的程序名单里（可以请 IT 加进去）";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>读窗口：程序要允许。</summary>
    public static PolicyDecision Read(DesktopWindow? window, IReadOnlyCollection<string> allowList)
    {
        if (window is null)
        {
            return PolicyDecision.Auto(); // 执行时会说找不到窗口
        }
        return AppAllowed(window.Process, allowList, out var reason) ? PolicyDecision.Auto("读取窗口内容（只读）") : PolicyDecision.Blocked(reason);
    }

    public static PolicyDecision Click(DesktopSnapshot? snap, DesktopElement? el, IReadOnlyCollection<string> allowList, PermissionMode permission, bool confirmAll)
    {
        if (Gate(snap, allowList) is { } blocked)
        {
            return blocked;
        }
        if (el is null)
        {
            return PolicyDecision.Auto(); // 编号不存在：执行时会说找不到，让 AI 重新读窗口
        }
        var label = Label(el);
        var submit = SubmitLike().IsMatch(el.Name);
        if (submit && permission == PermissionMode.ReadOnly)
        {
            return PolicyDecision.Blocked("当前是「仅可查看」权限，不能点会提交或修改数据的按钮");
        }
        if (submit)
        {
            return PolicyDecision.Confirm($"在「{snap!.Window.Title}」里点击「{label}」，可能会提交或修改数据");
        }
        return confirmAll ? PolicyDecision.Confirm($"在「{snap!.Window.Title}」里点击「{label}」") : PolicyDecision.Auto();
    }

    public static PolicyDecision Type(DesktopSnapshot? snap, DesktopElement? el, string text, bool submit, IReadOnlyCollection<string> allowList, PermissionMode permission, bool confirmAll)
    {
        if (Gate(snap, allowList) is { } blocked)
        {
            return blocked;
        }
        if (permission == PermissionMode.ReadOnly)
        {
            return PolicyDecision.Blocked("当前是「仅可查看」权限，不能在程序里填写内容");
        }
        if (el is not null)
        {
            if (el.IsPassword || Secret().IsMatch(el.Name))
            {
                return PolicyDecision.Blocked("密码、验证码请你自己输入，AI 不代填");
            }
            if (Personal().IsMatch(el.Name))
            {
                return PolicyDecision.Confirm($"要在「{Label(el)}」里填写个人敏感信息");
            }
        }
        var where = el is null ? "" : $"「{Label(el)}」";
        if (submit)
        {
            return PolicyDecision.Confirm($"在{where}填写「{Clip(text)}」后按回车提交");
        }
        return confirmAll ? PolicyDecision.Confirm($"在「{snap!.Window.Title}」的{where}填写「{Clip(text)}」") : PolicyDecision.Auto();
    }

    public static PolicyDecision Key(DesktopSnapshot? snap, DesktopKeys.Combo? keys, string error, IReadOnlyCollection<string> allowList, PermissionMode permission, bool confirmAll)
    {
        if (keys is null)
        {
            return PolicyDecision.Blocked(error);
        }
        if (Gate(snap, allowList) is { } blocked)
        {
            return blocked;
        }
        var text = keys.ToString();
        // 只读：只能浏览（移动焦点、翻页、复制、查找）
        var browsing = !keys.Alt && (keys.Key is "Tab" or "Up" or "Down" or "Left" or "Right" or "Home" or "End" or "PageUp" or "PageDown" or "Esc"
                                     || (keys.Ctrl && keys.Key is "C" or "A" or "F"));
        if (permission == PermissionMode.ReadOnly && !browsing)
        {
            return PolicyDecision.Blocked($"当前是「仅可查看」权限，不能按 {text}");
        }
        var risky = keys.Key is "Enter" or "Delete" or "F4" || (keys.Ctrl && keys.Key is "S" or "P" or "D" or "X" or "V" or "Z" or "Y" or "W" or "Enter");
        if (risky)
        {
            return PolicyDecision.Confirm($"在「{snap!.Window.Title}」里按 {text}（可能会提交、保存、删除或关闭）");
        }
        return confirmAll && !browsing ? PolicyDecision.Confirm($"在「{snap!.Window.Title}」里按 {text}") : PolicyDecision.Auto();
    }

    public static PolicyDecision Scroll(DesktopSnapshot? snap, IReadOnlyCollection<string> allowList) =>
        Gate(snap, allowList) ?? PolicyDecision.Auto();

    /// <summary>点击、填写、按键都要先读过一个允许的窗口。</summary>
    private static PolicyDecision? Gate(DesktopSnapshot? snap, IReadOnlyCollection<string> allowList)
    {
        if (snap is null)
        {
            return PolicyDecision.Blocked("还没有读过窗口：先用 desktop_read 读取要操作的窗口");
        }
        return AppAllowed(snap.Window.Process, allowList, out var reason) ? null : PolicyDecision.Blocked(reason);
    }

    private static string Label(DesktopElement el) => el.Name.Length > 0 ? Clip(el.Name) : $"编号 {el.Index}";

    private static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
