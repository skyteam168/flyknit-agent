using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Flyknit.Core.Lsp;

/// <summary>
/// 员工信任了哪些项目（%APPDATA%\Flyknit\lsp-trust.json）。语言服务会执行项目里的部分代码（TS 插件、构建脚本），
/// 和 VS Code 的「信任此文件夹」一样，没信任的项目不启动语言服务。信任一个文件夹，里面的子文件夹都算。
/// </summary>
public sealed class LspTrust
{
    private readonly string _file;
    private readonly object _gate = new();
    private List<string>? _roots;

    public LspTrust(string file) => _file = file;

    public bool IsTrusted(string folder)
    {
        var f = Norm(folder);
        lock (_gate)
        {
            return Load().Any(r => f.Equals(r, StringComparison.OrdinalIgnoreCase) || f.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Set(string folder, bool trusted)
    {
        var f = Norm(folder);
        lock (_gate)
        {
            var list = Load();
            list.RemoveAll(r => r.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (trusted)
            {
                list.Add(f);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(list));
        }
    }

    private List<string> Load()
    {
        if (_roots is not null)
        {
            return _roots;
        }
        try
        {
            _roots = File.Exists(_file) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _roots = new();
        }
        return _roots;
    }

    private static string Norm(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>
/// 语言服务管理：按「项目根 + 语言」起一个，同一个项目里的文件、几个任务共用；同时最多开几个（多了关最久没用的），
/// 空闲一段时间自动关（语言服务很吃内存）；退出了下次用到再起。没信任的项目、没装的语言不起。
/// </summary>
public sealed class LspHub : IAsyncDisposable
{
    private readonly LspCatalog _catalog;
    private readonly LspTrust _trust;
    private readonly string _dataRoot;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, LspClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _starting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Error)> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="dataRoot">语言服务的缓存目录（%LOCALAPPDATA%\Flyknit\lsp），每个项目一个子目录（jdtls 要）。</param>
    public LspHub(LspCatalog catalog, LspTrust trust, string dataRoot, Action<string> log)
    {
        _catalog = catalog;
        _trust = trust;
        _dataRoot = dataRoot;
        _log = log;
    }

    public LspCatalog Catalog => _catalog;
    public LspTrust Trust => _trust;

    /// <summary>同时最多开几个语言服务（每个几百 MB 起）。</summary>
    public int MaxServers { get; init; } = 4;

    /// <summary>多久没用就关掉。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>启动失败后多久内不再试（免得每敲一个字都去起一次）。</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>启动语言服务（测试里换成假的）。</summary>
    public Func<LspServerSpec, LspCommand, string, string, string, CancellationToken, Task<LspClient>> Starter { get; init; } = LspClient.StartAsync;

    public IReadOnlyCollection<LspClient> Running => _clients.Values.Where(c => !c.Exited).ToList();

    /// <summary>
    /// 这个文件该用的语言服务（没起就起）。拿不到时 Error 是：unsupported（不支持的文件）/ untrusted（项目没信任）/
    /// missing（这台电脑没装）/ disabled（IT 关了）/ 启动失败的原因。
    /// </summary>
    public async Task<(LspClient? Client, string Error)> ForFileAsync(string workspace, string fullPath, CancellationToken ct)
    {
        if (_catalog.ForFile(fullPath) is not { } spec)
        {
            return (null, "unsupported");
        }
        if (!_trust.IsTrusted(workspace))
        {
            return (null, "untrusted");
        }
        var availability = _catalog.Resolve(spec);
        if (availability.Disabled)
        {
            return (null, "disabled");
        }
        var candidates = _catalog.Candidates(spec);
        if (candidates.Count == 0)
        {
            return (null, "missing");
        }
        var root = LspCatalog.RootFor(spec, fullPath, workspace);
        var key = spec.Id + "|" + root;
        if (_clients.TryGetValue(key, out var running) && !running.Exited)
        {
            return (running, "");
        }
        if (_failed.TryGetValue(key, out var failed) && DateTimeOffset.Now - failed.At < RetryAfter)
        {
            return (null, failed.Error);
        }
        var gate = _starting.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(key, out running) && !running.Exited)
            {
                return (running, "");
            }
            await MakeRoomAsync().ConfigureAwait(false);
            var dataDir = Path.Combine(_dataRoot, spec.Id + "-" + Hash(root));
            Directory.CreateDirectory(dataDir);
            // 按顺序试：前一个起不来（比如 TypeScript 7 没有 tsserver 了）就换下一个
            var errors = new List<string>();
            foreach (var (command, exe) in candidates)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    var client = await Starter(spec, command, exe, root, dataDir, timeout.Token).ConfigureAwait(false);
                    _clients[key] = client;
                    _failed.TryRemove(key, out _);
                    _log($"启动 {spec.Name} 语言服务：{exe} {string.Join(' ', command.Args)}，项目 {root}");
                    return (client, "");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    errors.Add($"{Path.GetFileName(exe)}：{ex.Message}");
                }
            }
            var why = $"{spec.Name} 语言服务启动失败：{string.Join("；", errors)}";
            _failed[key] = (DateTimeOffset.Now, why);
            _log(why);
            return (null, why);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>关掉空闲太久的。宿主定时调。</summary>
    public async Task SweepAsync()
    {
        foreach (var (key, client) in _clients.ToArray())
        {
            if (client.Exited || DateTimeOffset.Now - client.LastUsed > IdleTimeout)
            {
                if (_clients.TryRemove(key, out _))
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task MakeRoomAsync()
    {
        while (_clients.Count(c => !c.Value.Exited) >= MaxServers)
        {
            var oldest = _clients.OrderBy(c => c.Value.LastUsed).First();
            if (_clients.TryRemove(oldest.Key, out var client))
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        foreach (var (key, client) in _clients.ToArray())
        {
            if (client.Exited && _clients.TryRemove(key, out _))
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..12].ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        _clients.Clear();
    }
}
