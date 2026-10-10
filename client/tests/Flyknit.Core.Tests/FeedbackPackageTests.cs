using System.IO.Compression;
using System.Text;
using Flyknit.Core.Diagnostics;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>意见反馈：截图解码、日志打包。</summary>
public class FeedbackPackageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-feedback").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static string DataUrl(byte[] data, string type = "image/png") => $"data:{type};base64,{Convert.ToBase64String(data)}";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public void ImagesAreRecognisedByTheirContent()
    {
        var png = FeedbackPackage.DecodeImage(DataUrl(Png))!;
        Assert.Equal(("png", "image/png"), (png.Extension, png.MediaType));
        Assert.Equal(Png, png.Data);

        // 声明的类型不算数，看文件头
        var jpg = FeedbackPackage.DecodeImage(DataUrl([0xFF, 0xD8, 0xFF, 0xE0, 0], "image/png"))!;
        Assert.Equal("jpg", jpg.Extension);

        var webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");
        Assert.Equal("webp", FeedbackPackage.DecodeImage(DataUrl(webp, "image/webp"))!.Extension);
    }

    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")] // SVG 里能带脚本，不收
    [InlineData("data:image/png,rawtext")]
    [InlineData("data:image/png;base64,!!!")]
    [InlineData("https://example.com/a.png")]
    [InlineData("data:image/png;base64,")]
    public void AnythingElseIsRejected(string url) => Assert.Null(FeedbackPackage.DecodeImage(url));

    [Fact]
    public void OversizedImagesAreRejected()
    {
        var big = new byte[FeedbackPackage.MaxImageBytes + 1];
        Png.CopyTo(big, 0);
        Assert.Null(FeedbackPackage.DecodeImage(DataUrl(big)));
    }

    [Fact]
    public void LogsOfTheLastFewDaysAreBundledWithDeviceInfo()
    {
        var today = new DateTime(2026, 10, 9, 15, 0, 0);
        File.WriteAllText(Path.Combine(_dir, "flyknit-20261009.log"), "today");
        File.WriteAllText(Path.Combine(_dir, "flyknit-20261007.log"), "two days ago");
        File.WriteAllText(Path.Combine(_dir, "flyknit-20261006.log"), "too old");
        File.WriteAllText(Path.Combine(_dir, "other.txt"), "not a log");

        var zip = FeedbackPackage.BuildLogs(_dir, today, new Dictionary<string, string> { ["version"] = "1.2.3" });
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.Equal(["device.txt", "flyknit-20261007.log", "flyknit-20261009.log"], archive.Entries.Select(e => e.FullName).Order());
        Assert.Contains("version: 1.2.3", Read(archive, "device.txt"));
        Assert.Equal("today", Read(archive, "flyknit-20261009.log"));
    }

    [Fact]
    public void HugeLogsKeepOnlyTheirEnd()
    {
        var path = Path.Combine(_dir, "flyknit-20261009.log");
        File.WriteAllText(path, new string('a', 5000) + "THE END");
        var zip = FeedbackPackage.BuildLogs(_dir, new DateTime(2026, 10, 9), new Dictionary<string, string>(), maxBytesPerFile: 100);
        using var archive = new ZipArchive(new MemoryStream(zip));
        var text = Read(archive, "flyknit-20261009.log");
        Assert.EndsWith("THE END", text);
        Assert.Contains("已省略", text);
        Assert.True(text.Length < 200);
    }

    [Fact]
    public void ALogBeingWrittenCanStillBeRead()
    {
        var path = Path.Combine(_dir, "flyknit-20261009.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("busy"u8);
        writer.Flush();
        var zip = FeedbackPackage.BuildLogs(_dir, new DateTime(2026, 10, 9), new Dictionary<string, string>());
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.Equal("busy", Read(archive, "flyknit-20261009.log"));
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
