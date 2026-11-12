using System.Text.RegularExpressions;

namespace Flyknit.Agent;

/// <summary>
/// 运维代理自升级里「该不该做」的判断（docs/agent-self-update.md）。不碰文件、不碰服务，单元测试直接链接这个文件。
/// </summary>
public static class SelfUpdatePolicy
{
    /// <summary>新代理启动后最多等这么久连上服务器（写健康标记），等不到就换回原版本。</summary>
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(3);

    /// <summary>更新助手等原来的代理进程退出最多等这么久。</summary>
    public static readonly TimeSpan ExitTimeout = TimeSpan.FromMinutes(2);

    private static readonly Regex Part = new(@"^(\d+(?:\.\d+)*)(?:[-+](.*))?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 比版本号：左边新返回正数，旧返回负数。规则和服务端 services/versions.py 一样：
    /// 按数字段比（0.10 比 0.9 新），带后缀的预发布版比正式版旧，认不出的当 0。
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var (a, sa) = Parse(left);
        var (b, sb) = Parse(right);
        for (var i = 0; i < 4; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0)
            {
                return c;
            }
        }
        // 没有后缀的更新：1.0.0 > 1.0.0-rc1
        if (sa.Length == 0 || sb.Length == 0)
        {
            return (sa.Length == 0 ? 1 : 0) - (sb.Length == 0 ? 1 : 0);
        }
        return string.CompareOrdinal(sa, sb);
    }

    private static (int[] Numbers, string Suffix) Parse(string? version)
    {
        var text = (version ?? "").Trim().TrimStart('v', 'V');
        var m = Part.Match(text);
        var numbers = new int[4];
        if (!m.Success)
        {
            return (numbers, "");
        }
        var parts = m.Groups[1].Value.Split('.');
        for (var i = 0; i < Math.Min(4, parts.Length); i++)
        {
            numbers[i] = int.TryParse(parts[i], out var n) ? n : 0;
        }
        return (numbers, m.Groups[2].Success ? m.Groups[2].Value : "");
    }

    /// <summary>
    /// 要不要换成 candidate：只升不降、相同不动、认不出的版本不换、以前换失败过的版本不再试。
    /// </summary>
    public static bool ShouldUpdate(string current, string? candidate, IReadOnlyCollection<string> skipped)
    {
        if (string.IsNullOrWhiteSpace(candidate) || Compare(candidate, "0.0.0") <= 0)
        {
            return false;
        }
        if (skipped.Contains(candidate.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        return Compare(candidate, current) > 0;
    }

    /// <summary>
    /// 同一个版本换失败几次就不再试。不是一次就放弃：健康检查看的是「3 分钟内连上服务器」，
    /// 正好赶上断网也会回滚；再给一次机会，两次都不行才认定这个版本有问题。
    /// </summary>
    public const int MaxAttempts = 2;

    /// <summary>失败记录（每次回滚记一行版本号）里，已经失败够次数、不再尝试的版本。</summary>
    public static IReadOnlyCollection<string> GivenUp(IEnumerable<string> failures) =>
        failures.Select(f => f.Trim()).Where(f => f.Length > 0)
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MaxAttempts)
            .Select(g => g.Key)
            .ToList();

    /// <summary>FileVersionInfo.ProductVersion 可能带 +提交号，只要前面的版本号。</summary>
    public static string CleanVersion(string? productVersion) => (productVersion ?? "").Split('+')[0].Trim();

    public enum HealthOutcome
    {
        /// <summary>还在等。</summary>
        Waiting,

        /// <summary>新代理连上服务器了：删掉旧版本，收工。</summary>
        Healthy,

        /// <summary>超时没连上（启动失败、崩溃、连不上）：换回原版本。</summary>
        RollBack,
    }

    public static HealthOutcome CheckHealth(bool healthyMarker, TimeSpan elapsed) =>
        healthyMarker ? HealthOutcome.Healthy
        : elapsed >= HealthTimeout ? HealthOutcome.RollBack
        : HealthOutcome.Waiting;
}
