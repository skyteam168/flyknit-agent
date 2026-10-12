using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Mcp;

/// <summary>服务端 GET api/v1/client/runtimes 的返回：公司服务器提供的便携运行环境和 npm / PyPI 镜像。</summary>
public sealed class McpRuntimeManifest
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("npm_registry")] public string NpmRegistry { get; set; } = "";
    [JsonPropertyName("pypi_index")] public string PypiIndex { get; set; } = "";
    [JsonPropertyName("runtimes")] public List<McpRuntimeInfo> Runtimes { get; set; } = new();

    public static McpRuntimeManifest Parse(string json) =>
        JsonSerializer.Deserialize<McpRuntimeManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
}

public sealed class McpRuntimeInfo
{
    /// <summary>node / uv / python。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>zip / tar.gz。</summary>
    [JsonPropertyName("archive")] public string Archive { get; set; } = "zip";

    /// <summary>压缩包里要去掉的顶层目录，空表示没有。</summary>
    [JsonPropertyName("root")] public string Root { get; set; } = "";

    /// <summary>要加进 PATH 的子目录（相对去掉 root 之后），"" 表示根目录本身。</summary>
    [JsonPropertyName("bin")] public List<string> Bin { get; set; } = new() { "" };

    /// <summary>服务端还没缓存时可能是空的。</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>运行环境从哪儿来：正式是 FlyknitServerClient，测试里换成假的。</summary>
public interface IMcpRuntimeSource
{
    /// <summary>服务端没有这个接口（旧版本）返回 null。</summary>
    Task<McpRuntimeManifest?> GetMcpRuntimesAsync(CancellationToken ct);

    /// <summary>下载压缩包到 destination，返回响应头里的 X-Runtime-Sha256（没有就是 null）。</summary>
    Task<string?> DownloadMcpRuntimeAsync(string name, string destination, CancellationToken ct);
}

/// <summary>交给 stdio 子进程的运行环境：PATH 前面加的目录 + 环境变量。连接器自己的 env 优先。</summary>
public sealed record McpRuntimeEnvironment
{
    public static McpRuntimeEnvironment Empty { get; } = new();

    public IReadOnlyList<string> PathDirs { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 托管的 MCP 运行环境。员工电脑上一般没有 Node.js / uv / Python，也上不了外网，
/// 而厂商的 stdio 连接器大多写的是 npx / uvx。这里按命令判断要哪些运行环境，
/// 从公司服务器下载便携版装到 %LOCALAPPDATA%\Flyknit\runtimes\{name}-{version}，
/// 再把 npm / PyPI 指到公司服务器的镜像。服务端没开这个功能就什么都不做，照旧用这台电脑自己的。
/// </summary>
public sealed class McpRuntimeManager
{
    private const string Marker = ".complete";
    private static readonly TimeSpan ManifestTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ManifestFailTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly IMcpRuntimeSource _source;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _manifestLock = new(1, 1);
    private McpRuntimeManifest? _manifest;
    private DateTime _manifestAt = DateTime.MinValue;
    private TimeSpan _manifestTtl;

    public McpRuntimeManager(IMcpRuntimeSource source, string root)
    {
        _source = source;
        _root = root;
    }

    public string Root => _root;

    /// <summary>
    /// 这个命令要哪些运行环境：npx / npm / node / corepack 要 node；uvx / uv 要 uv 和 python；
    /// python / pip 要 python。写了完整路径的不管（厂商自己指定了程序）。
    /// </summary>
    public static IReadOnlyList<string> Needs(string command)
    {
        var c = (command ?? "").Trim().Trim('"');
        if (c.Length == 0 || Path.IsPathRooted(c) || c.Contains('/') || c.Contains('\\'))
        {
            return Array.Empty<string>();
        }
        c = c.ToLowerInvariant();
        foreach (var ext in new[] { ".cmd", ".exe", ".bat" })
        {
            if (c.EndsWith(ext, StringComparison.Ordinal))
            {
                c = c[..^ext.Length];
                break;
            }
        }
        return c switch
        {
            "npx" or "npm" or "node" or "corepack" => new[] { "node" },
            "uvx" or "uv" => new[] { "uv", "python" },
            "python" or "python3" or "pip" or "pip3" => new[] { "python" },
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>命令找不到时给员工看的话。</summary>
    public static string MissingMessage(string command)
    {
        var needs = Needs(command);
        var what = needs.Contains("uv") ? ("uv / Python", "uv")
            : needs.Contains("node") ? ("Node.js", "Node.js")
            : needs.Contains("python") ? ("Python", "Python")
            : default;
        return what == default
            ? $"这台电脑上找不到 {command}，连接器启动不了。请联系 IT 在这台电脑上安装它"
            : $"这台电脑没有 {command}（{what.Item1}），连接器启动不了。请联系 IT：在管理后台打开「MCP 运行环境」后重试，或在这台电脑上安装 {what.Item2}";
    }

    public static string DisplayName(string name) => name.ToLowerInvariant() switch
    {
        "node" => "Node.js",
        "python" => "Python",
        _ => name,
    };

    /// <summary>
    /// 准备好这个连接器要的运行环境。服务端没开（或旧服务端、暂时连不上）返回空环境，照旧用这台电脑自己的。
    /// 下载、解压失败抛 McpException。
    /// </summary>
    public async Task<McpRuntimeEnvironment> EnsureAsync(McpServerConfig config, Action<string>? progress, CancellationToken ct)
    {
        if (config.Transport != "stdio")
        {
            return McpRuntimeEnvironment.Empty;
        }
        var manifest = await GetManifestAsync(ct);
        if (manifest is not { Enabled: true })
        {
            return McpRuntimeEnvironment.Empty;
        }

        var installed = new Dictionary<string, (McpRuntimeInfo Info, string Dir)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Needs(config.Command))
        {
            var info = manifest.Runtimes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (info is null)
            {
                continue; // 服务端没提供这一个，用这台电脑自己的
            }
            installed[name] = (info, await InstallAsync(info, progress, ct));
        }
        return BuildEnvironment(manifest, _root, installed.Values.ToList());
    }

    /// <summary>算出 PATH 目录和环境变量。</summary>
    internal static McpRuntimeEnvironment BuildEnvironment(
        McpRuntimeManifest manifest, string root, IReadOnlyList<(McpRuntimeInfo Info, string Dir)> installed)
    {
        var dirs = new List<string>();
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pythonExe = null;
        foreach (var (info, dir) in installed)
        {
            var bins = info.Bin.Count > 0 ? info.Bin : new List<string> { "" };
            foreach (var bin in bins)
            {
                var full = BinDir(dir, bin);
                if (full is not null && !dirs.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(full);
                }
            }
            if (info.Name.Equals("python", StringComparison.OrdinalIgnoreCase))
            {
                pythonExe = FindPython(bins.Select(b => BinDir(dir, b)).OfType<string>().ToList());
            }
        }

        if (manifest.NpmRegistry.Length > 0)
        {
            env["NPM_CONFIG_REGISTRY"] = manifest.NpmRegistry;
        }
        env["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";
        env["NPM_CONFIG_FUND"] = "false";
        env["NPM_CONFIG_AUDIT"] = "false";
        env["NPM_CONFIG_CACHE"] = Path.Combine(root, "npm-cache");
        env["npm_config_yes"] = "true";

        if (manifest.PypiIndex.Length > 0)
        {
            env["UV_DEFAULT_INDEX"] = manifest.PypiIndex;
            env["UV_INDEX_URL"] = manifest.PypiIndex;
            env["PIP_INDEX_URL"] = manifest.PypiIndex;
        }
        if (pythonExe is not null)
        {
            env["UV_PYTHON"] = pythonExe;
            env["UV_PYTHON_DOWNLOADS"] = "never";
        }
        env["UV_CACHE_DIR"] = Path.Combine(root, "uv-cache");

        // 电脑上设了代理（HTTP_PROXY）时，npm、uv 会连公司服务器也走代理，内网地址常常走不通：把服务器加进 NO_PROXY
        var hosts = new[] { manifest.NpmRegistry, manifest.PypiIndex }
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hosts.Count > 0)
        {
            var existing = Environment.GetEnvironmentVariable("NO_PROXY") ?? Environment.GetEnvironmentVariable("no_proxy") ?? "";
            var noProxy = string.Join(",", existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Concat(hosts).Distinct(StringComparer.OrdinalIgnoreCase));
            env["NO_PROXY"] = noProxy;
            env["no_proxy"] = noProxy;
        }
        env["PYTHONUTF8"] = "1";
        env["PYTHONIOENCODING"] = "utf-8";
        return new McpRuntimeEnvironment { PathDirs = dirs, Env = env };
    }

    private static string? BinDir(string dir, string bin)
    {
        var rel = (bin ?? "").Replace('\\', '/').Trim('/');
        if (rel.Split('/').Any(p => p == ".."))
        {
            return null;
        }
        return rel.Length == 0 ? dir : Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string FindPython(IReadOnlyList<string> bins)
    {
        foreach (var name in new[] { "python.exe", "python3", "python" })
        {
            foreach (var bin in bins)
            {
                var candidate = Path.Combine(bin, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return Path.Combine(bins.FirstOrDefault() ?? "", "python.exe");
    }

    /// <summary>清单在内存里缓存 10 分钟；拉不到当作这次没开，1 分钟后再试。</summary>
    public async Task<McpRuntimeManifest?> GetManifestAsync(CancellationToken ct)
    {
        await _manifestLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _manifestAt < _manifestTtl)
            {
                return _manifest;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ManifestTimeout);
            try
            {
                _manifest = await _source.GetMcpRuntimesAsync(cts.Token);
                _manifestTtl = ManifestTtl;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _manifest = null;
                _manifestTtl = ManifestFailTtl;
            }
            _manifestAt = DateTime.UtcNow;
            return _manifest;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>装好一个运行环境，返回它的目录。同一个运行环境同时只下载一次。</summary>
    public async Task<string> InstallAsync(McpRuntimeInfo info, Action<string>? progress, CancellationToken ct)
    {
        var name = Safe(info.Name);
        var dirName = $"{name}-{Safe(info.Version)}";
        var target = Path.Combine(_root, dirName);
        if (File.Exists(Path.Combine(target, Marker)))
        {
            return target;
        }

        var gate = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var id = Guid.NewGuid().ToString("N")[..8];
        var archive = Path.Combine(_root, $".{dirName}.{id}.download");
        var staging = Path.Combine(_root, $".{dirName}.{id}.tmp");
        var display = DisplayName(name);
        try
        {
            if (File.Exists(Path.Combine(target, Marker)))
            {
                return target; // 别人刚装好
            }
            Directory.CreateDirectory(_root);
            var size = info.Size > 0 ? $"，约 {Math.Max(1, (int)Math.Round(info.Size / 1048576.0))} MB" : "";
            progress?.Invoke($"正在准备 {display} 运行环境（首次使用{size}）…");

            string? headerSha;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(DownloadTimeout);
                try
                {
                    headerSha = await _source.DownloadMcpRuntimeAsync(name, archive, cts.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new McpException($"下载 {display} 运行环境超时，请稍后重试", ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not McpException)
                {
                    throw new McpException($"下载 {display} 运行环境失败：{ex.Message}。请稍后重试，或联系 IT", ex);
                }
            }

            var actual = await Sha256Async(archive, ct);
            foreach (var expected in new[] { info.Sha256, headerSha })
            {
                if (!string.IsNullOrWhiteSpace(expected) && !expected.Trim().Equals(actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new McpException($"下载的 {display} 运行环境校验不通过（文件不完整或被改过），请稍后重试");
                }
            }

            progress?.Invoke($"正在解压 {display} 运行环境…");
            try
            {
                Extract(archive, info.Archive, info.Root, staging);
            }
            catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
            {
                throw new McpException($"解压 {display} 运行环境失败：{ex.Message}", ex);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, Marker), DateTimeOffset.Now.ToString("O"), CancellationToken.None);

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true); // 上次装到一半留下的
            }
            Directory.Move(staging, target);
            DeleteOldVersions(name, dirName);
            return target;
        }
        catch (IOException) when (File.Exists(Path.Combine(target, Marker)))
        {
            return target; // 另一个进程抢先装好了
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"安装 {display} 运行环境失败：{ex.Message}", ex);
        }
        finally
        {
            TryDelete(archive);
            TryDeleteDir(staging);
            gate.Release();
        }
    }

    /// <summary>解压到 staging，去掉顶层目录 root；防止压缩包里的 ../ 跑到外面去。</summary>
    internal static void Extract(string archive, string format, string root, string staging)
    {
        Directory.CreateDirectory(staging);
        var stagingFull = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var prefix = (root ?? "").Replace('\\', '/').Trim('/');
        var files = 0;

        string? Destination(string entryName)
        {
            var n = entryName.Replace('\\', '/');
            while (n.StartsWith("./", StringComparison.Ordinal))
            {
                n = n[2..];
            }
            n = n.TrimStart('/');
            if (prefix.Length > 0)
            {
                if (!n.StartsWith(prefix + "/", StringComparison.Ordinal))
                {
                    return null;
                }
                n = n[(prefix.Length + 1)..];
            }
            n = n.TrimEnd('/');
            if (n.Length == 0)
            {
                return null;
            }
            var full = Path.GetFullPath(Path.Combine(staging, n.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        switch (format.Trim().ToLowerInvariant())
        {
            case "zip":
                using (var zip = ZipFile.OpenRead(archive))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (Destination(entry.FullName) is not { } dest)
                        {
                            continue;
                        }
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                        {
                            Directory.CreateDirectory(dest);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        entry.ExtractToFile(dest, overwrite: true);
                        files++;
                    }
                }
                break;
            case "tar.gz" or "tgz":
                using (var file = File.OpenRead(archive))
                using (var gz = new GZipStream(file, CompressionMode.Decompress))
                using (var tar = new TarReader(gz))
                {
                    while (tar.GetNextEntry() is { } entry)
                    {
                        if (Destination(CutAtNul(entry.Name)) is not { } dest)
                        {
                            continue;
                        }
                        switch (entry.EntryType)
                        {
                            case TarEntryType.Directory:
                                Directory.CreateDirectory(dest);
                                break;
                            case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                                using (var output = File.Create(dest))
                                {
                                    entry.DataStream?.CopyTo(output);
                                }
                                if (!OperatingSystem.IsWindows())
                                {
                                    try { File.SetUnixFileMode(dest, entry.Mode); } catch (Exception) { /* 权限设不上不要紧 */ }
                                }
                                files++;
                                break;
                            case TarEntryType.SymbolicLink when !OperatingSystem.IsWindows():
                                // Windows 版的包里没有链接；其他系统上只建指向包内的链接
                                var linkTarget = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dest)!, CutAtNul(entry.LinkName)));
                                if (linkTarget.StartsWith(stagingFull, StringComparison.Ordinal))
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                                    File.CreateSymbolicLink(dest, CutAtNul(entry.LinkName));
                                }
                                break;
                        }
                    }
                }
                break;
            default:
                throw new McpException($"不认识的运行环境压缩格式：{format}");
        }
        if (files == 0)
        {
            throw new McpException("运行环境压缩包是空的，或者顶层目录和服务端说的不一样");
        }
    }

    /// <summary>
    /// tar 头里的文件名以 \0 结尾，后面可能还留着上一个名字的残字节（python-build-standalone 的包就是这样）。
    /// .NET 8 的 TarReader 不截断，原样带出来，拼路径时就报「Null character in path」。
    /// </summary>
    internal static string CutAtNul(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }
        var i = name.IndexOf('\0');
        return i < 0 ? name : name[..i];
    }

    /// <summary>新版本装好了，删掉同一运行环境的旧版本。正在用的删不掉就算了，下次再删。</summary>
    private void DeleteOldVersions(string name, string keep)
    {
        var pattern = new Regex("^" + Regex.Escape(name) + "-[0-9]", RegexOptions.IgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                var dirName = Path.GetFileName(dir);
                if (dirName.Equals(keep, StringComparison.OrdinalIgnoreCase) || !pattern.IsMatch(dirName))
                {
                    continue;
                }
                TryDelete(Path.Combine(dir, Marker)); // 先去掉标记，删到一半也不会被当成装好的
                TryDeleteDir(dir);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响使用
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }

    /// <summary>名字、版本号拿来当目录名：只留安全的字符。</summary>
    private static string Safe(string s)
    {
        var cleaned = Regex.Replace(s ?? "", @"[^A-Za-z0-9._+-]", "_").Trim('.');
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉不要紧
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 删不掉不要紧
        }
    }
}
