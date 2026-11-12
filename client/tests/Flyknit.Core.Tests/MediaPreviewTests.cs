using Flyknit.Core.Preview;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>视频 / 音频预览：不读进内存、按令牌地址流式提供、支持 Range 拖进度条。</summary>
public class MediaPreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-media").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Make(string name, long size)
    {
        var path = Path.Combine(_dir, name);
        using var f = File.Create(path);
        f.SetLength(size);
        return path;
    }

    [Theory]
    [InlineData("demo.mp4", PreviewKind.Video, "video/mp4")]
    [InlineData("DEMO.MOV", PreviewKind.Video, "video/mp4")]
    [InlineData("clip.webm", PreviewKind.Video, "video/webm")]
    [InlineData("voice.mp3", PreviewKind.Audio, "audio/mpeg")]
    [InlineData("rec.wav", PreviewKind.Audio, "audio/wav")]
    [InlineData("rec.m4a", PreviewKind.Audio, "audio/mp4")]
    public async Task PlayableFormatsBecomeMediaPreviews(string name, PreviewKind kind, string mime)
    {
        var path = Make(name, 1024);
        var doc = await PreviewRegistry.CreateDefault().LoadAsync(path);
        Assert.Equal(kind, doc.Kind);
        Assert.Equal(mime, doc.Mime);
        Assert.Equal(Path.GetFullPath(path), doc.MediaPath);
        Assert.Null(doc.DataUrl); // 不把内容塞进消息
    }

    [Theory]
    [InlineData("old.avi")]
    [InlineData("screen.wmv")]
    [InlineData("movie.mkv")]
    [InlineData("old.flv")]
    public async Task UnplayableContainersFallBackToFileInfo(string name)
    {
        var registry = PreviewRegistry.CreateDefault();
        var path = Make(name, 1024);
        Assert.False(registry.CanPreview(path));
        var doc = await registry.LoadAsync(path);
        Assert.Equal(PreviewKind.None, doc.Kind);
        Assert.Null(doc.MediaPath);
    }

    [Fact]
    public async Task LargeVideosAreNotRejectedBySizeLimit()
    {
        // 稀疏文件：占不了磁盘，但长度超过 25 MB 上限
        var video = Make("big.mp4", PreviewRegistry.MaxBytes * 4);
        var doc = await PreviewRegistry.CreateDefault().LoadAsync(video);
        Assert.Equal(PreviewKind.Video, doc.Kind);

        // 其他格式照旧受上限限制
        var text = Make("big.txt", PreviewRegistry.MaxBytes + 1);
        Assert.Equal(PreviewKind.None, (await PreviewRegistry.CreateDefault().LoadAsync(text)).Kind);
    }

    [Fact]
    public void TokensAreRandomAndOnlyResolveRegisteredFiles()
    {
        var tokens = new MediaTokens();
        var a = Make("a.mp4", 10);
        var t1 = tokens.Register(a);
        Assert.Matches("^[0-9a-f]{32}$", t1);
        Assert.True(tokens.TryResolve(t1, out var path) && path == Path.GetFullPath(a));
        // 同一个文件沿用同一个令牌
        Assert.Equal(t1, tokens.Register(a));
        // 猜不出来、也不是路径
        Assert.False(tokens.TryResolve("00000000000000000000000000000000", out _));
        Assert.False(tokens.TryResolve(a, out _));
        Assert.False(tokens.TryResolve("../../Windows/win.ini", out _));
        tokens.Clear();
        Assert.False(tokens.TryResolve(t1, out _));
    }

    [Fact]
    public void OldestTokensExpireBeyondCapacity()
    {
        var tokens = new MediaTokens();
        var first = tokens.Register(Path.Combine(_dir, "0.mp4"));
        for (var i = 1; i <= MediaTokens.Capacity; i++)
        {
            tokens.Register(Path.Combine(_dir, $"{i}.mp4"));
        }
        Assert.False(tokens.TryResolve(first, out _));
        // 再注册一次会拿到新令牌
        Assert.NotEqual(first, tokens.Register(Path.Combine(_dir, "0.mp4")));
    }

    [Theory]
    [InlineData(null, 1000, null, null)]
    [InlineData("", 1000, null, null)]
    [InlineData("items=0-10", 1000, null, null)]   // 不认识的单位：给整个文件
    [InlineData("bytes=0-", 1000, 0L, 999L)]
    [InlineData("bytes=0-99", 1000, 0L, 99L)]
    [InlineData("bytes=500-", 1000, 500L, 999L)]
    [InlineData("bytes=900-5000", 1000, 900L, 999L)] // 结束超出：截到文件尾
    [InlineData("bytes=-100", 1000, 900L, 999L)]     // 最后 100 字节
    [InlineData("bytes=-5000", 1000, 0L, 999L)]
    [InlineData("bytes=0-9, 20-29", 1000, 0L, 9L)]   // 多段只取第一段
    public void ParsesRanges(string? header, long length, long? start, long? end)
    {
        var r = HttpRange.Parse(header, length);
        if (start is null)
        {
            Assert.Null(r);
            return;
        }
        Assert.NotNull(r);
        Assert.True(r!.IsValid);
        Assert.Equal(start, r.Start);
        Assert.Equal(end, r.End);
    }

    [Theory]
    [InlineData("bytes=1000-", 1000)]   // 起点在文件外
    [InlineData("bytes=50-10", 1000)]   // 倒过来
    [InlineData("bytes=-0", 1000)]
    [InlineData("bytes=abc-def", 1000)]
    [InlineData("bytes=-1--2", 1000)]
    [InlineData("bytes=5", 1000)]
    [InlineData("bytes=0-", 0)]          // 空文件
    public void InvalidRangesAreRejected(string header, long length)
    {
        var r = HttpRange.Parse(header, length);
        Assert.NotNull(r);
        Assert.False(r!.IsValid);
    }

    [Fact]
    public void BoundedStreamReadsOnlyTheRequestedSlice()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var slice = new BoundedStream(new MemoryStream(bytes), 100, 20);
        Assert.Equal(20, slice.Length);
        var buffer = new byte[64];
        var total = 0;
        int n;
        while ((n = slice.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += n;
        }
        Assert.Equal(20, total);
        Assert.Equal(bytes[100..120], buffer[..20]);

        slice.Seek(15, SeekOrigin.Begin);
        Assert.Equal(5, slice.Read(buffer, 0, 64));
        Assert.Equal(bytes[115..120], buffer[..5]);
    }
}
