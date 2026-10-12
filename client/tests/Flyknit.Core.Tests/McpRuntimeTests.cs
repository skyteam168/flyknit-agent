using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Flyknit.Core.Gateway;
using Flyknit.Core.Mcp;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>托管的 MCP 运行环境：按命令选运行环境、从服务器下载解压、环境变量、命令查找、标准错误解码。</summary>
public class McpRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flyknit-rt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    // ---------- 假服务器 ----------

    private sealed class FakeServer : HttpMessageHandler
    {
        public string Manifest = "";
        public int ManifestStatus = 200;
        public readonly Dictionary<string, byte[]> Archives = new();
        public readonly Dictionary<string, string> HeaderSha = new();
        public int Downloads;
        public int ManifestCalls;
        public TimeSpan Delay = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/client/runtimes")
            {
                Interlocked.Increment(ref ManifestCalls);
                return new HttpResponseMessage((HttpStatusCode)ManifestStatus) { Content = new StringContent(Manifest, Encoding.UTF8, "application/json") };
            }
            const string prefix = "/api/v1/client/runtimes/";
            if (path.StartsWith(prefix) && path.EndsWith("/download"))
            {
                var name = path[prefix.Length..^"/download".Length];
                Interlocked.Increment(ref Downloads);
                await Task.Delay(Delay, ct);
                var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archives[name]) };
                if (HeaderSha.TryGetValue(name, out var sha))
                {
                    resp.Headers.Add("X-Runtime-Sha256", sha);
                }
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static FlyknitServerClient Client(FakeServer server) => new(new HttpClient(server), "http://srv.local:8009", "tok");

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                if (!name.EndsWith('/'))
                {
                    using var w = new StreamWriter(entry.Open());
                    w.Write(content);
                }
            }
        }
        return ms.ToArray();
    }

    private static byte[] TarGz(params (string Name, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            foreach (var (name, content) in files)
            {
                if (name.EndsWith('/'))
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));
                    continue;
                }
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
            }
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static readonly byte[] NodeZip = Zip(
        ("node-v22.12.0-win-x64/", ""),
        ("node-v22.12.0-win-x64/node.exe", "node"),
        ("node-v22.12.0-win-x64/npx.cmd", "@echo npx"),
        ("node-v22.12.0-win-x64/node_modules/npm/index.js", "npm"),
        ("stray.txt", "不在顶层目录里，应该被跳过"));

    private static readonly byte[] UvZip = Zip(("uv.exe", "uv"), ("uvx.exe", "uvx"));

    private static readonly byte[] PythonTar = TarGz(
        ("python/", ""),
        ("python/python.exe", "py"),
        ("python/Scripts/pip.exe", "pip"),
        ("python/Lib/os.py", "# os"));

    private static string ManifestJson(string nodeVersion = "22.12.0", string nodeSha = "", bool enabled = true) => $$"""
        {"enabled": {{(enabled ? "true" : "false")}},
         "npm_registry": "http://srv.local:8009/api/v1/mirror/npm/",
         "pypi_index": "http://srv.local:8009/api/v1/mirror/pypi/simple/",
         "runtimes": [
           {"name":"node","version":"{{nodeVersion}}","archive":"zip","root":"node-v22.12.0-win-x64","bin":[""],"sha256":"{{nodeSha}}","size":31457280},
           {"name":"uv","version":"0.5.11","archive":"zip","root":"","bin":[""],"sha256":"","size":0},
           {"name":"python","version":"3.12.8","archive":"tar.gz","root":"python","bin":["","Scripts"],"sha256":"","size":0}]}
        """;

    private FakeServer Server(string? manifest = null)
    {
        var server = new FakeServer { Manifest = manifest ?? ManifestJson() };
        server.Archives["node"] = NodeZip;
        server.Archives["uv"] = UvZip;
        server.Archives["python"] = PythonTar;
        return server;
    }

    private static McpServerConfig Stdio(string command, Dictionary<string, string>? env = null) => new()
    {
        Id = "x", Name = "X", Transport = "stdio", Command = command, Env = env ?? new Dictionary<string, string>(),
    };

    // ---------- 选运行环境 ----------

    [Fact]
    public void RuntimesAreChosenByCommandName()
    {
        Assert.Equal(new[] { "node" }, McpRuntimeManager.Needs("npx"));
        Assert.Equal(new[] { "node" }, McpRuntimeManager.Needs("NPX.CMD"));
        Assert.Equal(new[] { "node" }, McpRuntimeManager.Needs("node.exe"));
        Assert.Equal(new[] { "node" }, McpRuntimeManager.Needs("corepack"));
        Assert.Equal(new[] { "uv", "python" }, McpRuntimeManager.Needs("uvx"));
        Assert.Equal(new[] { "uv", "python" }, McpRuntimeManager.Needs("uv.exe"));
        Assert.Equal(new[] { "python" }, McpRuntimeManager.Needs("python3"));
        Assert.Equal(new[] { "python" }, McpRuntimeManager.Needs("pip"));
        Assert.Empty(McpRuntimeManager.Needs(@"C:\tools\npx.cmd"));
        Assert.Empty(McpRuntimeManager.Needs("/usr/bin/node"));
        Assert.Empty(McpRuntimeManager.Needs("docker"));
        Assert.Empty(McpRuntimeManager.Needs(""));

        Assert.Contains("Node.js", McpRuntimeManager.MissingMessage("npx"));
        Assert.Contains("MCP 运行环境", McpRuntimeManager.MissingMessage("npx"));
        Assert.Contains("uv", McpRuntimeManager.MissingMessage("uvx"));
        Assert.Contains("找不到 docker", McpRuntimeManager.MissingMessage("docker"));
    }

    [Fact]
    public void ManifestParses()
    {
        var m = McpRuntimeManifest.Parse(ManifestJson());
        Assert.True(m.Enabled);
        Assert.Equal("http://srv.local:8009/api/v1/mirror/npm/", m.NpmRegistry);
        Assert.Equal("http://srv.local:8009/api/v1/mirror/pypi/simple/", m.PypiIndex);
        Assert.Equal(3, m.Runtimes.Count);
        var py = m.Runtimes[2];
        Assert.Equal(("python", "3.12.8", "tar.gz", "python"), (py.Name, py.Version, py.Archive, py.Root));
        Assert.Equal(new[] { "", "Scripts" }, py.Bin);
        Assert.Equal(31457280, m.Runtimes[0].Size);
    }

    // ---------- 安装 ----------

    [Fact]
    public async Task NodeIsInstalledFromTheServerAndReused()
    {
        var server = Server();
        var messages = new List<string>();
        var manager = new McpRuntimeManager(Client(server), _root);
        var env = await manager.EnsureAsync(Stdio("npx"), messages.Add, CancellationToken.None);

        var dir = Path.Combine(_root, "node-22.12.0");
        Assert.Equal(new[] { dir }, env.PathDirs);
        Assert.True(File.Exists(Path.Combine(dir, "node.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "npx.cmd")));
        Assert.True(File.Exists(Path.Combine(dir, "node_modules", "npm", "index.js")));
        Assert.True(File.Exists(Path.Combine(dir, ".complete")));
        Assert.False(File.Exists(Path.Combine(dir, "stray.txt")));
        Assert.False(Directory.Exists(Path.Combine(dir, "node-v22.12.0-win-x64")));
        Assert.Contains(messages, m => m.Contains("Node.js") && m.Contains("约 30 MB"));
        Assert.Equal(new[] { "node-22.12.0" }, Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));

        Assert.Equal("http://srv.local:8009/api/v1/mirror/npm/", env.Env["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("true", env.Env["npm_config_yes"]);
        Assert.Equal("false", env.Env["NPM_CONFIG_UPDATE_NOTIFIER"]);
        Assert.Equal(Path.Combine(_root, "npm-cache"), env.Env["NPM_CONFIG_CACHE"]);

        // 换一个实例（相当于重启客户端）：有 .complete 就不再下载
        var again = await new McpRuntimeManager(Client(server), _root).EnsureAsync(Stdio("npx.cmd"), null, CancellationToken.None);
        Assert.Equal(env.PathDirs, again.PathDirs);
        Assert.Equal(1, server.Downloads);
    }

    [Fact]
    public async Task UvxGetsUvAndPythonWithBinDirs()
    {
        var server = Server();
        var env = await new McpRuntimeManager(Client(server), _root).EnsureAsync(Stdio("uvx"), null, CancellationToken.None);
        var uv = Path.Combine(_root, "uv-0.5.11");
        var py = Path.Combine(_root, "python-3.12.8");
        Assert.Equal(new[] { uv, py, Path.Combine(py, "Scripts") }, env.PathDirs);
        Assert.True(File.Exists(Path.Combine(uv, "uvx.exe")));
        Assert.True(File.Exists(Path.Combine(py, "Scripts", "pip.exe")));
        Assert.True(File.Exists(Path.Combine(py, "Lib", "os.py")));
        Assert.Equal(Path.Combine(py, "python.exe"), env.Env["UV_PYTHON"]);
        Assert.Equal("never", env.Env["UV_PYTHON_DOWNLOADS"]);
        Assert.Equal("http://srv.local:8009/api/v1/mirror/pypi/simple/", env.Env["UV_DEFAULT_INDEX"]);
        Assert.Equal("http://srv.local:8009/api/v1/mirror/pypi/simple/", env.Env["UV_INDEX_URL"]);
        Assert.Equal("http://srv.local:8009/api/v1/mirror/pypi/simple/", env.Env["PIP_INDEX_URL"]);
        Assert.Equal(Path.Combine(_root, "uv-cache"), env.Env["UV_CACHE_DIR"]);
        Assert.Equal("utf-8", env.Env["PYTHONIOENCODING"]);
        Assert.Equal("1", env.Env["PYTHONUTF8"]);
        Assert.Equal(2, server.Downloads);
    }

    [Fact]
    public async Task OtherCommandsStillGetTheMirrorButNothingIsDownloaded()
    {
        var server = Server();
        var env = await new McpRuntimeManager(Client(server), _root).EnsureAsync(Stdio(@"C:\tools\npx.cmd"), null, CancellationToken.None);
        Assert.Empty(env.PathDirs);
        Assert.Equal("http://srv.local:8009/api/v1/mirror/npm/", env.Env["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("http://srv.local:8009/api/v1/mirror/pypi/simple/", env.Env["PIP_INDEX_URL"]);
        Assert.False(env.Env.ContainsKey("UV_PYTHON"));
        Assert.Equal(0, server.Downloads);

        // http 连接器什么都不做
        Assert.Same(McpRuntimeEnvironment.Empty,
            await new McpRuntimeManager(Client(server), _root).EnsureAsync(new McpServerConfig { Id = "h", Name = "H", Url = "http://a" }, null, CancellationToken.None));
    }

    [Fact]
    public async Task OldServerOrDisabledMeansNoManagedRuntime()
    {
        var old = Server();
        old.ManifestStatus = 404;
        Assert.Same(McpRuntimeEnvironment.Empty, await new McpRuntimeManager(Client(old), _root).EnsureAsync(Stdio("npx"), null, CancellationToken.None));

        var off = Server(ManifestJson(enabled: false));
        Assert.Same(McpRuntimeEnvironment.Empty, await new McpRuntimeManager(Client(off), _root).EnsureAsync(Stdio("npx"), null, CancellationToken.None));

        var broken = Server("not json");
        Assert.Same(McpRuntimeEnvironment.Empty, await new McpRuntimeManager(Client(broken), _root).EnsureAsync(Stdio("npx"), null, CancellationToken.None));
        Assert.Equal(0, old.Downloads + off.Downloads + broken.Downloads);
    }

    [Fact]
    public async Task ManifestIsCached()
    {
        var server = Server();
        var manager = new McpRuntimeManager(Client(server), _root);
        await manager.EnsureAsync(Stdio("npx"), null, CancellationToken.None);
        await manager.EnsureAsync(Stdio("npx"), null, CancellationToken.None);
        Assert.Equal(1, server.ManifestCalls);
    }

    [Fact]
    public async Task ShaMismatchIsRejected()
    {
        var server = Server(ManifestJson(nodeSha: new string('0', 64)));
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            new McpRuntimeManager(Client(server), _root).EnsureAsync(Stdio("npx"), null, CancellationToken.None));
        Assert.Contains("校验不通过", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_root));

        // 清单里的对得上、响应头里的对不上，也不装
        var header = Server(ManifestJson(nodeSha: Sha(NodeZip)));
        header.HeaderSha["node"] = new string('f', 64);
        await Assert.ThrowsAsync<McpException>(() =>
            new McpRuntimeManager(Client(header), _root).EnsureAsync(Stdio("npx"), null, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(_root));

        // 都对得上就装（大小写不敏感）
        var good = Server(ManifestJson(nodeSha: Sha(NodeZip).ToUpperInvariant()));
        good.HeaderSha["node"] = Sha(NodeZip);
        var env = await new McpRuntimeManager(Client(good), _root).EnsureAsync(Stdio("npx"), null, CancellationToken.None);
        Assert.Single(env.PathDirs);
    }

    [Fact]
    public async Task ConcurrentCallsShareOneDownload()
    {
        var server = Server();
        server.Delay = TimeSpan.FromMilliseconds(300);
        var manager = new McpRuntimeManager(Client(server), _root);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => manager.EnsureAsync(Stdio("npx"), null, CancellationToken.None)));
        Assert.Equal(1, server.Downloads);
        Assert.All(results, r => Assert.Equal(Path.Combine(_root, "node-22.12.0"), r.PathDirs.Single()));
    }

    [Fact]
    public async Task ANewVersionReplacesTheOldOne()
    {
        await new McpRuntimeManager(Client(Server()), _root).EnsureAsync(Stdio("npx"), null, CancellationToken.None);
        await new McpRuntimeManager(Client(Server(ManifestJson(nodeVersion: "22.13.0"))), _root).EnsureAsync(Stdio("npx"), null, CancellationToken.None);
        Assert.Equal(new[] { "node-22.13.0" }, Directory.GetDirectories(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void ArchivesCannotEscapeTheTargetDirectory()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "evil.zip");
        File.WriteAllBytes(archive, Zip(("../evil.txt", "x"), ("ok.txt", "y")));
        var staging = Path.Combine(_root, "out");
        McpRuntimeManager.Extract(archive, "zip", "", staging);
        Assert.True(File.Exists(Path.Combine(staging, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));

        // 顶层目录对不上，一个文件都没解出来：报错，不当成装好了
        Assert.Throws<McpException>(() => McpRuntimeManager.Extract(archive, "zip", "node-v1", Path.Combine(_root, "out2")));
    }

    [Fact]
    public void TarNamesWithBytesAfterTheNulTerminatorExtract()
    {
        // python-build-standalone 的包：名字字段 \0 之后还留着上一个名字的残字节，.NET 8 读出来会带上它们
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "py.tar.gz");
        File.WriteAllBytes(archive, RawUstarGz(("python/DLLs/_bz2.pyd\0bz2.pyd", "a"), ("python/python.exe\0xe", "b")));
        var staging = Path.Combine(_root, "out");
        McpRuntimeManager.Extract(archive, "tar.gz", "python", staging);
        Assert.Equal("a", File.ReadAllText(Path.Combine(staging, "DLLs", "_bz2.pyd")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(staging, "python.exe")));
    }

    /// <summary>手写 ustar 头，名字字段原样写进去（TarWriter 不让写 \0）。</summary>
    private static byte[] RawUstarGz(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var data = Encoding.UTF8.GetBytes(content);
                var h = new byte[512];
                void Put(int at, string text) => Encoding.ASCII.GetBytes(text).CopyTo(h, at);
                Encoding.UTF8.GetBytes(name).CopyTo(h, 0);
                Put(100, "0000644\0");
                Put(108, "0000000\0");
                Put(116, "0000000\0");
                Put(124, Convert.ToString(data.Length, 8).PadLeft(11, '0') + "\0");
                Put(136, "00000000000\0");
                Put(148, "        ");
                h[156] = (byte)'0';
                Put(257, "ustar\0");
                Put(263, "00");
                Put(148, Convert.ToString(h.Sum(b => b), 8).PadLeft(6, '0') + "\0 ");
                gz.Write(h);
                gz.Write(data);
                gz.Write(new byte[(512 - data.Length % 512) % 512]);
            }
            gz.Write(new byte[1024]);
        }
        return ms.ToArray();
    }

    // ---------- 子进程环境、命令查找 ----------

    [Fact]
    public void VendorEnvOverridesRuntimeEnvAndPathIsPrepended()
    {
        var sep = Path.PathSeparator;
        var env = new Dictionary<string, string?> { ["Path"] = "/usr/bin", ["HOME"] = "/home/a" };
        var runtime = new McpRuntimeEnvironment
        {
            PathDirs = new[] { "/rt/node", "/rt/py" },
            Env = new Dictionary<string, string> { ["NPM_CONFIG_REGISTRY"] = "http://mirror/", ["PYTHONUTF8"] = "1" },
        };
        StdioTransport.ApplyEnvironment(env, runtime, new Dictionary<string, string> { ["NPM_CONFIG_REGISTRY"] = "http://vendor/" });
        Assert.Equal($"/rt/node{sep}/rt/py{sep}/usr/bin", env["Path"]);
        Assert.False(env.ContainsKey("PATH"));
        Assert.Equal("http://vendor/", env["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("1", env["PYTHONUTF8"]);
        Assert.Equal("/home/a", env["HOME"]);
    }

    [Fact]
    public void ManagedShimsAreFoundAndRunWithFullPath()
    {
        const string shim = @"C:\Users\John Smith\AppData\Local\Flyknit\runtimes\node-22.12.0\npx.cmd";
        string? Find(string f) => f == "npx.cmd" ? shim : null;
        var (file, args) = StdioTransport.ResolveCommand("npx", new[] { "-y", "@scope/pkg" }, windows: true, Find);
        Assert.Equal("cmd.exe", file);
        Assert.Equal(new[] { "/d", "/s", "/c", shim, "-y", "@scope/pkg" }, args);
        Assert.Equal($"/d /s /c \"\"{shim}\" -y @scope/pkg\"", StdioTransport.CmdArguments(args.Skip(3).ToList()));

        const string uvx = @"C:\rt\uv-0.5.11\uvx.exe";
        Assert.Equal(uvx, StdioTransport.ResolveCommand("uvx", new[] { "mcp-server-x" }, windows: true, f => f == "uvx.exe" ? uvx : null).File);

        Assert.True(StdioTransport.CanFind("npx", windows: true, Find));
        Assert.False(StdioTransport.CanFind("npx", windows: true, _ => null));
        Assert.True(StdioTransport.CanFind(@"C:\x\server.exe", windows: true, _ => null));
        Assert.False(StdioTransport.CanFind("npx", windows: false, _ => null));

        Assert.Equal("\"a b\"", StdioTransport.QuoteArgument("a b"));
        Assert.Equal("\"\"", StdioTransport.QuoteArgument(""));
        Assert.Equal("\"say \\\"hi\\\"\"", StdioTransport.QuoteArgument("say \"hi\""));
        Assert.Equal("\"C:\\a b\\\\\"", StdioTransport.QuoteArgument("C:\\a b\\"));
    }

    [Fact]
    public async Task AMissingCommandIsReportedBeforeStarting()
    {
        // 起进程之前就查：找不到直接说清楚，不去起一个必然失败的 cmd.exe
        var config = Stdio("definitely-missing-" + Guid.NewGuid().ToString("N")[..6]);
        await using var transport = new StdioTransport(config, null, McpRuntimeEnvironment.Empty);
        var ex = await Assert.ThrowsAsync<McpException>(() => transport.StartAsync(CancellationToken.None));
        Assert.Contains("找不到", ex.Message);
    }

    // ---------- 标准错误解码 ----------

    [Fact]
    public void StderrIsDecodedAsUtf8OrOemCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string message = "'npx' 不是内部或外部命令，也不是可运行的程序";
        var gbk = Encoding.GetEncoding(936).GetBytes(message);
        Assert.Equal(message, ConsoleText.FromLatin1(Encoding.Latin1.GetString(gbk), 936));

        var utf8 = Encoding.UTF8.GetBytes("npm ERR! 找不到包 ✓");
        Assert.Equal("npm ERR! 找不到包 ✓", ConsoleText.FromLatin1(Encoding.Latin1.GetString(utf8), 936));

        Assert.Equal("plain ascii", ConsoleText.FromLatin1("plain ascii", 936));
        Assert.Equal(message, ConsoleText.Decode(gbk, 936));
    }
}
