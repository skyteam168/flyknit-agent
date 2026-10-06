using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>
/// 网络白名单：AI 通过命令或 open_app 访问网址时，只放行服务端下发的域名。
///
/// 判断依据是命令里写明的网址。写明了就按主机名查名单；用了 curl 这类联网命令
/// 却看不出访问哪里（网址放在变量里、拼接出来的），证不了它安全，就交给用户确认。
/// </summary>
public sealed class NetworkPolicy
{
    private static readonly Regex UrlPattern = new(
        @"\b(?:https?|ftps?)://[^\s""'<>|`^{}\\]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>会联网的命令和 .NET 类型。命中但又没写明网址时，访问目标要到运行时才知道。</summary>
    private static readonly Regex NetworkCommand = new(
        @"(?<![\w.-])(?:curl(?:\.exe)?|wget(?:\.exe)?|iwr|irm|invoke-webrequest|invoke-restmethod|start-bitstransfer|bitsadmin|ftp|tftp" +
        @"|ssh|scp|sftp|msedge|chrome|firefox|iexplore)(?![\w.-])" +
        @"|(?<![\w.-])git(?:\.exe)?\s+(?:clone|fetch|pull|push|ls-remote|submodule)\b" +
        @"|(?<![\w.-])(?:pip3?|python(?:3)?\s+-m\s+pip)\s+(?:install|download)\b" +
        @"|(?<![\w.-])(?:npm|yarn|pnpm)\s+(?:install|i|ci|add)\b" +
        @"|net\.webclient|net\.webrequest|system\.net\.http|httpclient|downloadstring|downloadfile|-urlcache",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 没写协议头的主机名（<c>curl evil.com</c>、<c>ssh user@1.2.3.4</c>）。
    /// 只认常见顶级域：<c>.py</c> <c>.sh</c> <c>.md</c> 这些和文件扩展名撞车的故意不列，免得把文件名当成网站。
    /// </summary>
    private static readonly Regex BareHostPattern = new(
        @"(?<![\w.\\/=-])((?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+" +
        @"(?:com|net|org|cn|vn|io|ru|xyz|top|info|biz|co|me|app|dev|cc|tk|uk|de|jp|kr|tw|hk|us|site|online|club|link|live|cloud|tech|store|shop|edu|gov)" +
        @"|\d{1,3}(?:\.\d{1,3}){3})(?![\w.-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly List<string> _domains;
    private readonly bool _allowAll;

    public bool AllowPrivateNetwork { get; }

    /// <summary>整理过的名单（小写、去掉 *. 前缀），界面上照这个显示。</summary>
    public IReadOnlyList<string> Domains => _domains;

    public NetworkPolicy(IEnumerable<string>? allowedDomains, bool allowPrivateNetwork)
    {
        _domains = (allowedDomains ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _allowAll = _domains.Contains("*");
        AllowPrivateNetwork = allowPrivateNetwork;
    }

    /// <summary>命令里写明的网址。</summary>
    public static IReadOnlyList<string> UrlsIn(string text) =>
        UrlPattern.Matches(text ?? "")
            .Select(m => m.Value.TrimEnd(')', ',', '.', ';', ']'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>网址以外、没写协议头的主机名和 IP。</summary>
    public static IReadOnlyList<string> BareHostsIn(string text) =>
        BareHostPattern.Matches(UrlPattern.Replace(text ?? "", " "))
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static bool IsNetworkCommand(string command) => NetworkCommand.IsMatch(command ?? "");

    /// <summary>这个主机名能不能访问。</summary>
    public bool IsAllowed(string host)
    {
        host = Normalize(host);
        if (host.Length == 0)
        {
            return false;
        }
        if (_allowAll)
        {
            return true;
        }
        if (AllowPrivateNetwork && IsPrivate(host))
        {
            return true;
        }
        return _domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>打开一个网址。</summary>
    public PolicyDecision EvaluateUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.IdnHost.Length == 0)
        {
            return PolicyDecision.Blocked($"看不出要访问哪个网址（{url}），已阻止");
        }
        return EvaluateHost(uri.IdnHost);
    }

    private PolicyDecision EvaluateHost(string host) =>
        IsAllowed(host)
            ? PolicyDecision.Auto($"{host} 在网络白名单内")
            : PolicyDecision.Blocked(
                $"{host} 不在网络白名单内，不能访问。请不要换别的方式绕过，告诉用户需要访问的话请联系 IT 把这个网站加入白名单");

    /// <summary>
    /// 网址和没写协议头的主机名都查一遍（open_app 的启动参数：<c>msedge evil.com</c>）。
    /// 有不能访问的返回 Blocked，否则返回 null。
    /// </summary>
    public PolicyDecision? EvaluateTargetsIn(string text)
    {
        var blocked = EvaluateUrlsIn(text);
        if (blocked is not null)
        {
            return blocked;
        }
        foreach (var host in BareHostsIn(text))
        {
            var decision = EvaluateHost(host);
            if (decision.Level == RiskLevel.Blocked)
            {
                return decision;
            }
        }
        return null;
    }

    /// <summary>
    /// 一段文本（命令、启动参数）里出现的网址是否都能访问。
    /// 有不能访问的返回 Blocked；没有网址或都能访问返回 null。
    /// </summary>
    public PolicyDecision? EvaluateUrlsIn(string text)
    {
        foreach (var url in UrlsIn(text))
        {
            var decision = EvaluateUrl(url);
            if (decision.Level == RiskLevel.Blocked)
            {
                return decision;
            }
        }
        return null;
    }

    /// <summary>
    /// 一条 Shell 命令的网络访问。返回 null 表示没有网络方面的顾虑，交给其余规则判断。
    /// </summary>
    public PolicyDecision? EvaluateCommand(string command)
    {
        var blocked = EvaluateUrlsIn(command);
        if (blocked is not null)
        {
            return blocked;
        }
        if (!IsNetworkCommand(command))
        {
            return null;
        }
        // 联网命令里没写协议头的主机也要查：一个白名单网址不能替后面的 evil.com 打掩护
        var hosts = BareHostsIn(command);
        foreach (var host in hosts)
        {
            var decision = EvaluateHost(host);
            if (decision.Level == RiskLevel.Blocked)
            {
                return decision;
            }
        }
        if (UrlsIn(command).Count == 0 && hosts.Count == 0)
        {
            return PolicyDecision.Confirm("这条命令会访问网络，但看不出访问的是哪个网址，需要确认");
        }
        return null;
    }

    private static string Normalize(string raw)
    {
        var text = (raw ?? "").Trim().ToLowerInvariant().TrimEnd('.');
        if (text.StartsWith("*.", StringComparison.Ordinal))
        {
            text = text[2..];
        }
        return text.Trim('[', ']');
    }

    /// <summary>内网地址、本机，以及不带点的主机名（只有内网 DNS 才解析得了）。</summary>
    private static bool IsPrivate(string host)
    {
        if (host == "localhost")
        {
            return true;
        }
        if (!IPAddress.TryParse(host, out var ip))
        {
            return !host.Contains('.');
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }
}
