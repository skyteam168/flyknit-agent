using System.Text;

namespace Flyknit.Setup;

/// <summary>
/// 安装程序的格式：[安装程序外壳 exe][员工端 zip（带开通文件）][32 字节结尾标记]。
///
/// 结尾标记 = 16 字节 "FLYKNIT-SETUP-01" + zip 起始位置 + zip 长度（各 8 字节小端）。
/// 服务端在下载时把它们拼起来（server/app/routers/releases.py 的 setup_trailer），两边要一致。
/// .NET 单文件程序的内容位置记在程序头里，后面追加数据不影响它运行。
/// </summary>
public static class Payload
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("FLYKNIT-SETUP-01");
    public const int TrailerSize = 32;

    public static byte[] Trailer(long offset, long length)
    {
        var bytes = new byte[TrailerSize];
        Magic.CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 8), offset);
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 8), length);
        return bytes;
    }

    /// <summary>找出附在后面的 zip 在哪。没有（这是个空壳外壳，或文件被截断）返回 null。</summary>
    public static (long Offset, long Length)? Locate(Stream file)
    {
        if (file.Length < TrailerSize)
        {
            return null;
        }
        var trailer = new byte[TrailerSize];
        file.Seek(-TrailerSize, SeekOrigin.End);
        file.ReadExactly(trailer);
        if (!trailer.AsSpan(0, 16).SequenceEqual(Magic))
        {
            return null;
        }
        var offset = BitConverter.ToInt64(trailer, 16);
        var length = BitConverter.ToInt64(trailer, 24);
        if (offset <= 0 || length <= 0 || offset + length + TrailerSize != file.Length)
        {
            return null;
        }
        return (offset, length);
    }

    /// <summary>把附在后面的 zip 原样拷出来。</summary>
    public static bool Extract(string exePath, string zipPath)
    {
        using var file = File.OpenRead(exePath);
        if (Locate(file) is not { } where)
        {
            return false;
        }
        file.Seek(where.Offset, SeekOrigin.Begin);
        using var output = File.Create(zipPath);
        var buffer = new byte[1024 * 1024];
        var left = where.Length;
        while (left > 0)
        {
            var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0)
            {
                throw new EndOfStreamException("安装程序文件不完整");
            }
            output.Write(buffer, 0, read);
            left -= read;
        }
        return true;
    }

    /// <summary>解开的 zip 里员工端程序所在的目录（FlyknitBuddy.exe 可能在根上，也可能套了一层文件夹）。</summary>
    public static string? FindRoot(string unpacked)
    {
        if (File.Exists(Path.Combine(unpacked, "FlyknitBuddy.exe")))
        {
            return unpacked;
        }
        return Directory.GetDirectories(unpacked).FirstOrDefault(d => File.Exists(Path.Combine(d, "FlyknitBuddy.exe")));
    }
}
