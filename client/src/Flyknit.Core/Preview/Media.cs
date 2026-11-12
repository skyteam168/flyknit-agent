using System.Security.Cryptography;

namespace Flyknit.Core.Preview;

/// <summary>
/// 预览不读进内存、由宿主按需流式提供的格式（视频、音频）。
/// 预览调度对它们不套 25 MB 的上限：几百 MB 的视频也只是交出一个地址，播放时浏览器按段来取。
/// </summary>
public interface IStreamingPreviewProvider : IPreviewProvider;

/// <summary>
/// 视频 / 音频：不读文件内容，只给出路径和 MIME。宿主把路径换成一个一次性令牌地址
/// （https://media.flyknit.local/{令牌}），界面用 &lt;video&gt; / &lt;audio&gt; 播放，宿主按 Range 分段返回。
///
/// 只认 WebView2（Chromium）能直接播放的容器；avi / wmv / mkv / flv 这类交给兜底的「文件信息」，用系统播放器打开。
/// </summary>
public sealed class MediaPreviewProvider : ExtensionPreviewProvider, IStreamingPreviewProvider
{
    public override string Name => "音视频";

    private static readonly Dictionary<string, (PreviewKind Kind, string Mime)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp4"] = (PreviewKind.Video, "video/mp4"),
        ["m4v"] = (PreviewKind.Video, "video/mp4"),
        // mov 多数是 H.264，Chromium 按 mp4 能放；放不了时界面会提示用系统播放器打开
        ["mov"] = (PreviewKind.Video, "video/mp4"),
        ["webm"] = (PreviewKind.Video, "video/webm"),
        ["ogv"] = (PreviewKind.Video, "video/ogg"),
        ["mp3"] = (PreviewKind.Audio, "audio/mpeg"),
        ["wav"] = (PreviewKind.Audio, "audio/wav"),
        ["m4a"] = (PreviewKind.Audio, "audio/mp4"),
        ["aac"] = (PreviewKind.Audio, "audio/aac"),
        ["ogg"] = (PreviewKind.Audio, "audio/ogg"),
        ["oga"] = (PreviewKind.Audio, "audio/ogg"),
        ["opus"] = (PreviewKind.Audio, "audio/ogg"),
        ["flac"] = (PreviewKind.Audio, "audio/flac"),
    };

    protected override IReadOnlySet<string> Extensions { get; } = Types.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string? MimeOf(string path) =>
        Types.TryGetValue(Path.GetExtension(path).TrimStart('.'), out var t) ? t.Mime : null;

    public override Task<PreviewDocument> LoadAsync(string path, CancellationToken ct)
    {
        var (kind, mime) = Types[Ext(path)];
        var info = new FileInfo(path);
        return Task.FromResult(new PreviewDocument(kind)
        {
            MediaPath = info.FullName,
            Mime = mime,
            Notice = PreviewRegistry.Format(info.Length),
        });
    }
}

/// <summary>
/// 媒体地址的令牌表：页面只拿到 https://media.flyknit.local/{令牌}，拿不到、也拼不出任意路径。
/// 令牌随机、只在本次运行的内存里，最多记最近 <see cref="Capacity"/> 个（再多就把最早的作废）。
/// </summary>
public sealed class MediaTokens
{
    public const int Capacity = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();

    public string Register(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_byPath.TryGetValue(full, out var existing))
            {
                // 再次预览同一个文件：沿用原来的令牌，挪到最新
                _order.Remove(existing);
                _order.AddLast(existing);
                return existing;
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _byToken[token] = full;
            _byPath[full] = token;
            _order.AddLast(token);
            while (_order.Count > Capacity)
            {
                var oldest = _order.First!.Value;
                _order.RemoveFirst();
                _byPath.Remove(_byToken[oldest]);
                _byToken.Remove(oldest);
            }
            return token;
        }
    }

    public bool TryResolve(string token, out string path)
    {
        lock (_gate)
        {
            return _byToken.TryGetValue(token, out path!);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byToken.Clear();
            _byPath.Clear();
            _order.Clear();
        }
    }
}

/// <summary>HTTP Range 请求头（bytes=起-止）。视频拖进度条时浏览器靠它分段取。</summary>
public static class HttpRange
{
    /// <summary>
    /// 解析 Range 头。返回 null 表示没有 Range（整个文件，200）；Invalid 表示格式对但范围不合法（416）。
    /// 多段（bytes=0-1,5-9）只取第一段：浏览器播放媒体不会这样请求，没必要支持 multipart。
    /// </summary>
    public static RangeResult? Parse(string? header, long length)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }
        var h = header.Trim();
        if (!h.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null; // 不认识的单位：按没有 Range 处理，给整个文件
        }
        var spec = h[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0 || length <= 0)
        {
            return RangeResult.Invalid;
        }
        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();
        long start, end;
        if (left.Length == 0)
        {
            // bytes=-500：最后 500 个字节
            if (!long.TryParse(right, out var suffix) || suffix <= 0)
            {
                return RangeResult.Invalid;
            }
            start = Math.Max(0, length - suffix);
            end = length - 1;
        }
        else
        {
            if (!long.TryParse(left, out start) || start < 0 || start >= length)
            {
                return RangeResult.Invalid;
            }
            if (right.Length == 0)
            {
                end = length - 1;
            }
            else if (!long.TryParse(right, out end) || end < start)
            {
                return RangeResult.Invalid;
            }
            end = Math.Min(end, length - 1);
        }
        return new RangeResult(start, end);
    }
}

public sealed record RangeResult(long Start, long End)
{
    public static readonly RangeResult Invalid = new(-1, -1);

    public bool IsValid => Start >= 0 && End >= Start;
    public long Length => End - Start + 1;
}

/// <summary>只暴露底层流里 [start, start+length) 这一段的只读流（给 206 分段响应用）。关闭时一起关掉底层流。</summary>
public sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public BoundedStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
        _inner.Seek(start, SeekOrigin.Begin);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }
        var n = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        _position = Math.Clamp(target, 0, _length);
        _inner.Seek(_start + _position, SeekOrigin.Begin);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
