using System.IO.Compression;
using System.Text;

namespace Flyknit.Core.Diagnostics;

/// <summary>反馈里的一张截图。</summary>
public sealed record FeedbackImage(byte[] Data, string Extension, string MediaType);

/// <summary>
/// 「意见反馈」要发给服务端的东西：页面传来的截图（data: URL）解出来，勾了「上传日志」就把最近几天的日志打个包。
/// 和界面、网络无关，单独放这里好测。
/// </summary>
public static class FeedbackPackage
{
    public const int MaxImages = 6;
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public const int MaxContent = 10_000;

    /// <summary>
    /// 解开页面传来的 data:image/...;base64,... 。按文件头认格式（PNG/JPG/GIF/WebP），不认识的、太大的返回 null。
    /// </summary>
    public static FeedbackImage? DecodeImage(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0
            || !dataUrl[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // base64 每 4 个字符 3 个字节，先按长度挡掉明显超大的，免得白解一遍
        if ((long)(dataUrl.Length - comma - 1) * 3 / 4 > MaxImageBytes)
        {
            return null;
        }
        byte[] data;
        try
        {
            data = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (data.Length == 0 || data.Length > MaxImageBytes)
        {
            return null;
        }
        return Sniff(data) is { } type ? new FeedbackImage(data, type.Ext, type.Media) : null;
    }

    private static (string Ext, string Media)? Sniff(byte[] d)
    {
        bool Starts(params byte[] magic) => d.Length >= magic.Length && d.AsSpan(0, magic.Length).SequenceEqual(magic);
        if (Starts(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
        {
            return ("png", "image/png");
        }
        if (Starts(0xFF, 0xD8, 0xFF))
        {
            return ("jpg", "image/jpeg");
        }
        if (Starts((byte)'G', (byte)'I', (byte)'F', (byte)'8'))
        {
            return ("gif", "image/gif");
        }
        if (d.Length > 12 && Starts((byte)'R', (byte)'I', (byte)'F', (byte)'F') && Encoding.ASCII.GetString(d, 8, 4) == "WEBP")
        {
            return ("webp", "image/webp");
        }
        return null;
    }

    /// <summary>
    /// 把最近 <paramref name="days"/> 天的日志（flyknit-yyyyMMdd.log）和一份设备信息打成 zip。
    /// 单个日志太大只留结尾那段——出问题的往往是最近的记录。
    /// </summary>
    public static byte[] BuildLogs(string logsDirectory, DateTime today, IReadOnlyDictionary<string, string> deviceInfo,
        int days = 3, int maxBytesPerFile = 4 * 1024 * 1024)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var info = new StringBuilder();
            foreach (var (key, value) in deviceInfo)
            {
                info.Append(key).Append(": ").AppendLine(value);
            }
            Add(zip, "device.txt", Encoding.UTF8.GetBytes(info.ToString()));

            for (var i = days - 1; i >= 0; i--)
            {
                var name = $"flyknit-{today.Date.AddDays(-i):yyyyMMdd}.log";
                var path = Path.Combine(logsDirectory, name);
                if (File.Exists(path) && ReadTail(path, maxBytesPerFile) is { } bytes)
                {
                    Add(zip, name, bytes);
                }
            }
        }
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        entry.Write(data);
    }

    private static byte[]? ReadTail(string path, int maxBytes)
    {
        try
        {
            // 程序自己可能正在往今天的日志里写，按共享读打开
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skip = Math.Max(0, file.Length - maxBytes);
            file.Seek(skip, SeekOrigin.Begin);
            using var rest = new MemoryStream();
            if (skip > 0)
            {
                rest.Write(Encoding.UTF8.GetBytes($"…（前面 {skip} 字节已省略）{Environment.NewLine}"));
            }
            file.CopyTo(rest);
            return rest.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
