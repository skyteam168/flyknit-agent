using System.Text.RegularExpressions;

namespace Flyknit.Core.Memory;

/// <summary>敏感信息检查的结论。</summary>
public sealed record SensitiveCheck(string Text, bool Rejected, IReadOnlyList<string> Findings)
{
    public bool Masked => !Rejected && Findings.Count > 0;
}

/// <summary>
/// 写入长期记忆前的敏感信息检查。复盘提示词里虽然叫模型别记密码，但模型不一定听，所以在代码里再拦一道：
/// 密码、密钥、令牌、私钥、验证码这类一旦泄露就能直接用的，整条不记；
/// 身份证号、银行卡号、手机号这类个人信息，打码后再记（保留“有这么个号”的事实，不保留号码本身）。
/// 只看“值”，不看“词”：像“登录密码需要先用公钥加密”这种讲做法的句子照常记。
/// </summary>
public static class SensitiveScanner
{
    private static readonly (string Name, Regex Pattern)[] Secrets =
    {
        ("私钥", new Regex(@"BEGIN [A-Z ]*PRIVATE KEY", RegexOptions.Compiled)),
        ("密码", new Regex(@"(密码|口令|password|passwd|pwd)\s*(?:是|为|[:：=])\s*[""'“]?[A-Za-z0-9!@#$%^&*._+\-~]{4,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("验证码", new Regex(@"(验证码|动态码|OTP)\s*(?:是|为|[:：=])?\s*\d{4,8}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("API 密钥", new Regex(@"\b(?:sk|pk|rk)-[A-Za-z0-9_\-]{16,}|\bgh[pousr]_[A-Za-z0-9]{20,}|\bAKIA[0-9A-Z]{16}\b|\bxox[abpr]-[A-Za-z0-9\-]{10,}|\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled)),
        ("令牌", new Regex(@"\bBearer\s+[A-Za-z0-9\-._~+/]{20,}=*|\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}", RegexOptions.Compiled)),
        ("密钥", new Regex(@"(api[_\- ]?key|secret|access[_\- ]?key|client[_\- ]?secret|token|密钥)\s*(?:是|为|[:：=])\s*[""'“]?[A-Za-z0-9_\-./+=]{16,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    };

    private static readonly Regex IdCard = new(@"(?<![0-9A-Za-z])\d{17}[\dXx](?![0-9A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"(?<![0-9A-Za-z.])\d(?:[ -]?\d){15,18}(?![0-9A-Za-z.])", RegexOptions.Compiled);
    private static readonly Regex Mobile = new(@"(?<!\d)(?:\+?86[ -]?)?1[3-9]\d[ -]?\d{4}[ -]?\d{4}(?!\d)", RegexOptions.Compiled);

    public static SensitiveCheck Check(string text)
    {
        var findings = new List<string>();
        foreach (var (name, pattern) in Secrets)
        {
            if (pattern.IsMatch(text))
            {
                findings.Add(name);
            }
        }
        if (findings.Count > 0)
        {
            return new SensitiveCheck(text, true, findings);
        }

        var masked = IdCard.Replace(text, m =>
        {
            if (!IsIdCard(m.Value)) return m.Value;
            findings.Add("身份证号");
            return m.Value[..4] + new string('*', 10) + m.Value[^4..];
        });
        masked = Digits.Replace(masked, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (!Luhn(digits)) return m.Value;
            findings.Add("银行卡号");
            return digits[..4] + " **** **** " + digits[^4..];
        });
        masked = Mobile.Replace(masked, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            digits = digits.Length > 11 ? digits[^11..] : digits;
            findings.Add("手机号");
            return digits[..3] + "****" + digits[^4..];
        });
        return new SensitiveCheck(masked, false, findings);
    }

    /// <summary>18 位身份证的校验位。随便一串 18 位数字（订单号、物料号）不会被当成身份证。</summary>
    public static bool IsIdCard(string s)
    {
        if (s.Length != 18) return false;
        int[] weights = { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
        const string check = "10X98765432";
        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            if (!char.IsDigit(s[i])) return false;
            sum += (s[i] - '0') * weights[i];
        }
        var month = int.Parse(s.Substring(10, 2));
        var day = int.Parse(s.Substring(12, 2));
        return month is >= 1 and <= 12 && day is >= 1 and <= 31 && char.ToUpperInvariant(s[17]) == check[sum % 11];
    }

    public static bool Luhn(string digits)
    {
        if (digits.Length is < 16 or > 19) return false;
        var sum = 0;
        var dbl = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (dbl)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }
}
