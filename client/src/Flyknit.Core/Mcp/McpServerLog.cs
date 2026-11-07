using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Mcp;

/// <summary>
/// 每个连接器一份日志（logs\mcp\&lt;id&gt;.log）：连上、断开、进程的标准错误输出、每次调用的工具名和耗时。
/// 连接器出问题时 IT 和员工能直接打开看，不用翻整个客户端日志。
///
/// 只记「发生了什么」，不记调用参数和返回内容（里面可能有业务数据）；每一行写之前先打码：
/// Authorization / Cookie 头、Bearer 令牌、token= / password= 这类键值、常见的密钥格式。
/// 超过 <see cref="MaxBytes"/> 就把旧的挪成 .1（只留一份），不会无限变大。
/// </summary>
public sealed partial class McpServerLog
{
    public const long MaxBytes = 1024 * 1024;
    private const int MaxLineChars = 2000;

    private readonly object _gate = new();

    public McpServerLog(string directory, string serverId)
    {
        Directory = directory;
        Path = System.IO.Path.Combine(directory, McpNames.Sanitize(serverId) + ".log");
    }

    public string Directory { get; }
    public string Path { get; }

    public void Write(string message)
    {
        var line = Redact(message.ReplaceLineEndings(" "));
        if (line.Length > MaxLineChars)
        {
            line = line[..MaxLineChars] + "…";
        }
        var text = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine;
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(Path, Path + ".1", overwrite: true);
                }
                File.AppendAllText(Path, text, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写不进去不影响连接器本身
            }
        }
    }

    /// <summary>最后几行（界面上「查看日志」先给一段，完整的再打开文件看）。</summary>
    public string Tail(int maxChars = 8000)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return "";
                }
                var text = File.ReadAllText(Path);
                return text.Length <= maxChars ? text : text[^maxChars..];
            }
            catch (IOException)
            {
                return "";
            }
        }
    }

    // ---------- 打码 ----------

    [GeneratedRegex(@"(?i)\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key|api-key)\s*[:=]\s*[^\r\n]+")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"(?i)([""']?\b[a-z0-9_\-]*(?:token|secret|password|passwd|pwd|api[_-]?key|access[_-]?key|session|cookie)[a-z0-9_\-]*[""']?\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&""'}]+)")]
    private static partial Regex KeyValuePattern();

    // 常见的令牌长相：GitHub、OpenAI 风格、Slack、AWS 访问密钥、JWT
    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9\-_]{16,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})")]
    private static partial Regex TokenPattern();

    /// <summary>把一行里像凭证的部分换成 ***。</summary>
    public static string Redact(string text)
    {
        text = HeaderPattern().Replace(text, m => m.Groups[1].Value + ": ***");
        text = SchemePattern().Replace(text, m => m.Groups[1].Value + " ***");
        text = KeyValuePattern().Replace(text, m => m.Groups[1].Value + "***");
        text = TokenPattern().Replace(text, "***");
        return text;
    }
}
