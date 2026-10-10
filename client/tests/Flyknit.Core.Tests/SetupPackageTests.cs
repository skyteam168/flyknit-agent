using System.IO.Compression;
using System.Text.Json.Nodes;
using Flyknit.Setup;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>安装程序：外壳 + 员工端 zip + 结尾标记；代理配置怎么合并。</summary>
public class SetupPackageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-setup").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] AZip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("FlyknitBuddy/FlyknitBuddy.exe").Open());
            w.Write("exe");
        }
        return buffer.ToArray();
    }

    /// <summary>和服务端 setup_trailer 拼的一模一样。</summary>
    private string BuildSetup(byte[] stub, byte[] zip)
    {
        var path = Path.Combine(_dir, "FlyknitBuddy-Setup.exe");
        File.WriteAllBytes(path, [.. stub, .. zip, .. Payload.Trailer(stub.Length, zip.Length)]);
        return path;
    }

    [Fact]
    public void ThePackageIsReadBackFromTheEndOfTheExe()
    {
        var zip = AZip();
        var setup = BuildSetup("MZ-stub-bytes"u8.ToArray(), zip);
        var output = Path.Combine(_dir, "out.zip");
        Assert.True(Payload.Extract(setup, output));
        Assert.Equal(zip, File.ReadAllBytes(output));

        var unpacked = Path.Combine(_dir, "files");
        ZipFile.ExtractToDirectory(output, unpacked);
        Assert.Equal(Path.Combine(unpacked, "FlyknitBuddy"), Payload.FindRoot(unpacked));
    }

    [Fact]
    public void TheTrailerMatchesTheServerFormat()
    {
        // server/app/routers/releases.py: b"FLYKNIT-SETUP-01" + offset(8, little) + length(8, little)
        var t = Payload.Trailer(0x0102, 0x0304);
        Assert.Equal("FLYKNIT-SETUP-01"u8.ToArray(), t[..16]);
        Assert.Equal(new byte[] { 0x02, 0x01, 0, 0, 0, 0, 0, 0, 0x04, 0x03, 0, 0, 0, 0, 0, 0 }, t[16..]);
    }

    [Fact]
    public void ABareStubOrATruncatedDownloadHasNoPackage()
    {
        var bare = Path.Combine(_dir, "bare.exe");
        File.WriteAllBytes(bare, new byte[200]);
        Assert.False(Payload.Extract(bare, Path.Combine(_dir, "x.zip")));

        var full = File.ReadAllBytes(BuildSetup("stub"u8.ToArray(), AZip()));
        var cut = Path.Combine(_dir, "cut.exe");
        File.WriteAllBytes(cut, [.. full[..^40], .. full[^32..]]);   // 中间少了一截
        Assert.False(Payload.Extract(cut, Path.Combine(_dir, "y.zip")));
    }

    [Fact]
    public void ANewAgentConfigGetsTheServerTicketAndClientFolder()
    {
        var json = JsonNode.Parse(AgentSettings.Merge(null, "http://10.0.0.5:8000/", "tk-1", @"C:\Program Files\FlyknitBuddy"))!;
        Assert.Equal("http://10.0.0.5:8000", (string?)json["server_url"]);
        Assert.Equal("tk-1", (string?)json["ticket"]);
        Assert.Equal(@"C:\Program Files\FlyknitBuddy", (string?)json["client_dir"]);
        Assert.Equal(0, (int)json["agent_id"]!);
        Assert.Equal("", (string?)json["token"]);
    }

    [Fact]
    public void ReinstallingKeepsTheAgentIdentityOnTheSameServer()
    {
        const string old = """{"server_url": "http://10.0.0.5:8000", "enrollment_key": "k", "agent_id": 7, "token": "secret"}""";
        var same = JsonNode.Parse(AgentSettings.Merge(old, "http://10.0.0.5:8000", "tk-2", "C:\\x"))!;
        Assert.Equal((7, "secret", "k", "tk-2"), ((int)same["agent_id"]!, (string?)same["token"], (string?)same["enrollment_key"], (string?)same["ticket"]));

        // 换了服务器：身份作废，用新凭证重新注册
        var moved = JsonNode.Parse(AgentSettings.Merge(old, "http://10.0.0.9:8000", "tk-3", "C:\\x"))!;
        Assert.Equal((0, ""), ((int)moved["agent_id"]!, (string?)moved["token"]));

        // 原来的文件坏了也照样能装
        Assert.Equal("tk-4", (string?)JsonNode.Parse(AgentSettings.Merge("{oops", "http://a", "tk-4", "C:\\x"))!["ticket"]);
    }
}
