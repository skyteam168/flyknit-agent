using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>
/// 系统级工具：注册表、服务、计划任务、WMI。安全中心里那个开关关着时一律阻止。
///
/// 为什么单独一层：这几样能绕过别处的限制。改一个注册表自启动项、建一个计划任务，
/// 都是在工作区之外留下长期生效的东西——工作区隔离管不着，删除保护也管不着。
///
/// 两种写法都要认。只拦 <c>sc config</c> 而放过 <c>Set-Service</c>，这个开关就只是演戏：
/// PowerShell 里有一整套等价的 cmdlet，它们干的是同一件事。
/// </summary>
public static class SystemToolPolicy
{
    private sealed record Family(string What, Regex Pattern);

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Family[] Families =
    {
        new("注册表", Make(
            @"(?<![\w.-])reg(?:\.exe)?\s+(?:add|delete|import|copy|load|unload|restore|save)(?![\w.-])" +
            @"|(?<![\w.-])regedit(?:\.exe)?(?![\w.-])" +
            @"|(?<![\w.-])(?:new|set|remove|rename|clear)-item(?:property)?\b[^|;&]*\bhk(?:lm|cu|cr|u|cc)\s*:" +
            @"|(?<![\w.-])(?:new|remove)-psdrive\b[^|;&]*\bregistry\b")),

        new("服务", Make(
            @"(?<![\w.-])sc(?:\.exe)?\s+(?:config|create|delete|start|stop|failure|sdset)(?![\w.-])" +
            @"|(?<![\w.-])(?:set|new|remove|restart|start|stop|suspend|resume)-service(?![\w.-])" +
            @"|(?<![\w.-])net(?:\.exe)?\s+(?:start|stop)\s+\S")),

        new("计划任务", Make(
            @"(?<![\w.-])schtasks(?:\.exe)?(?![\w.-])" +
            @"|(?<![\w.-])(?:register|unregister|set|start|stop|enable|disable)-scheduledtask(?![\w.-])" +
            @"|(?<![\w.-])(?:at|taskkill)(?:\.exe)?\s+/")),

        new("WMI", Make(
            @"(?<![\w.-])wmic(?:\.exe)?(?![\w.-])" +
            @"|(?<![\w.-])(?:invoke|set|remove|new)-(?:wmi|cim)(?:object|method|instance)(?![\w.-])" +
            @"|(?<![\w.-])(?:get-wmiobject|get-ciminstance)\b[^|;&]*\|[^|;&]*\b(?:invoke|remove|set)-")),
    };

    /// <summary>这条命令用到的系统级工具，没用到返回 null。</summary>
    public static string? FamilyOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        foreach (var family in Families)
        {
            if (family.Pattern.IsMatch(command))
            {
                return family.What;
            }
        }
        return null;
    }

    /// <summary>
    /// 开关关着时这条命令的判定；没用到系统级工具，或者开关开着，返回 null。
    ///
    /// 只会更严不会更松：开关打开不等于放行，命令照样回到原来的分级里去（多半是「要确认」）。
    /// </summary>
    public static PolicyDecision? Evaluate(string? command, bool allowed)
    {
        if (allowed)
        {
            return null;
        }
        var family = FamilyOf(command);
        return family is null
            ? null
            : PolicyDecision.Blocked(
                $"这条命令要动{family}，而「系统级工具」在安全中心里是关闭的。需要用请联系 IT 为这台电脑开启");
    }
}
