using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>
/// 把文本里像凭证的部分换成 ***：Authorization / Cookie / Set-Cookie 头、Bearer / Basic 令牌、
/// token= / password= / session= 这类键值、常见的密钥格式（GitHub、sk-、Slack、AWS、JWT）。
///
/// 用在两处：连接器日志（McpServerLog）；以及外部来源的工具结果进模型上下文之前（AgentLoop）——
/// 浏览器抓的请求、连接器返回的响应头、命令打出来的 curl -v，里面的 Cookie、令牌不该让模型看到、
/// 也不该跟着对话存进记录和审计。
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "***";

    [GeneratedRegex(@"(?im)\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key|api-key|x-auth-token|x-csrf-token)\s*[:=]\s*[^\r\n]+")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"(?i)([""']?\b[a-z0-9_\-]*(?:token|secret|password|passwd|pwd|api[_-]?key|access[_-]?key|session|cookie)[a-z0-9_\-]*[""']?\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&""'}]+)")]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9\-_]{16,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})")]
    private static partial Regex TokenPattern();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        text = HeaderPattern().Replace(text, m => m.Groups[1].Value + ": " + Mask);
        text = SchemePattern().Replace(text, m => m.Groups[1].Value + " " + Mask);
        text = KeyValuePattern().Replace(text, m => m.Groups[1].Value + Mask);
        text = TokenPattern().Replace(text, Mask);
        return text;
    }

    /// <summary>
    /// 哪些工具的结果进上下文前要打码：外部来源（连接器、浏览器）和命令输出。
    /// 读文件、搜代码不打码——改代码时要看到原文才能精确替换，那是员工自己工作区里的文件。
    /// </summary>
    public static bool AppliesTo(string toolName) =>
        toolName.StartsWith("mcp__", StringComparison.Ordinal)
        || toolName.StartsWith("browser_", StringComparison.Ordinal)
        || toolName is "run_shell" or "shell_output";
}
