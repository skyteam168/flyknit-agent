using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Gateway;
using Flyknit.Core.Mcp;
using Flyknit.Core.Tools;

namespace Flyknit.Client.Services;

/// <summary>
/// MCP 连接器：管理员在后台上架厂商，员工在「连接器」里点「连接」，
/// 对方的工具就出现在 AI 的工具列表里（mcp__厂商__工具）。
///
/// 和技能完全分开：
/// - 存储：连接信息在 %APPDATA%\Flyknit\mcp-connections.json（密钥、令牌用 DPAPI 加密），技能在 skills\ 目录；
/// - 工具：MCP 工具一律带 mcp__ 前缀，不会顶掉 load_skill / search_skills 或任何内置工具；
/// - 失败：一个连接器连不上只影响它自己，技能、别的连接器、对话照常。
///
/// 员工只能连管理员上架的连接器，不能自己加——和 Claude Code 的 managed-mcp 独占模式一样。
/// </summary>
public sealed class McpService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AppSettings _settings;
    private readonly FlyknitServerClient _server;
    private readonly ToolRegistry _tools;
    private readonly string _clientVersion;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, Runtime> _runtime = new();
    private readonly CancellationTokenSource _life = new();
    private HttpClient _http;
    private List<McpVendor> _vendors = new();
    private DateTime _vendorsAt;
    private Store _store;

    /// <summary>某个连接器的状态变了（连上、断开、出错），界面据此刷新那张卡片。</summary>
    public event Action<string>? Changed;

    public McpService(AppSettings settings, FlyknitServerClient server, ToolRegistry tools, string clientVersion)
    {
        _settings = settings;
        _server = server;
        _tools = tools;
        _clientVersion = clientVersion;
        _http = CreateHttp(settings);
        _store = Store.Load();
    }

    public static string StoreFile => Path.Combine(AppPaths.Root, "mcp-connections.json");

    private static HttpClient CreateHttp(AppSettings settings)
    {
        // 重定向自己处理：.NET 自动跟随时会丢掉 Authorization，登录拿到的令牌一跳转就没了（见 McpRedirectHandler）
        var handler = ProxyFactory.CreateHandler(settings);
        handler.AllowAutoRedirect = false;
        var http = new HttpClient(new McpRedirectHandler(handler));
        // 超时由每次请求自己控制：旧版 SSE 的长连接、要跑几分钟的工具都不能被 100 秒的默认值掐断
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FlyknitBuddy");
        return http;
    }

    /// <summary>代理设置改了：换一个 HttpClient，已连上的远程连接器重连一次。</summary>
    public void ApplyProxy()
    {
        var old = _http;
        _http = CreateHttp(_settings);
        foreach (var id in _runtime.Where(p => p.Value.Status == McpStatus.Connected && p.Value.Transport != "stdio").Select(p => p.Key).ToList())
        {
            _ = ReconnectAsync(id, "网络代理已切换");
        }
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => old.Dispose());
    }

    // ---------- 启动与同步 ----------

    /// <summary>启动时把上次连着的连接器连回来（后台进行，不挡启动）。</summary>
    public async Task StartAsync()
    {
        await SyncAsync();
    }

    /// <summary>
    /// 和服务端对一遍：下架、删掉的断开；管理员改了地址或密钥模板的重连；该连着却没连着的连上。
    /// 拉配置成功后（含 IT 改了配置的即时推送）都会调一次。
    /// </summary>
    public async Task SyncAsync()
    {
        try
        {
            await RefreshVendorsAsync(force: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"拉取 MCP 连接器列表失败：{ex.Message}");
            return;
        }
        var vendors = Vendors();
        foreach (var (id, saved) in _store.Snapshot())
        {
            if (!saved.Enabled)
            {
                continue;
            }
            var vendor = vendors.FirstOrDefault(v => v.Id == id);
            var rt = _runtime.GetOrAdd(id, _ => new Runtime());
            if (vendor is null)
            {
                if (rt.Connection is not null || rt.Status != McpStatus.Disconnected)
                {
                    Log.Info($"MCP 连接器 {id} 已被管理员下架，断开");
                    await DisconnectCoreAsync(id, McpStatus.Disconnected, "管理员已下架这个连接器");
                }
                continue;
            }
            var fingerprint = vendor.ConnectionFingerprint();
            if (rt.Status == McpStatus.Connected && rt.Fingerprint == fingerprint)
            {
                continue;
            }
            if (rt.Status is McpStatus.Connecting or McpStatus.Authorizing)
            {
                continue;
            }
            if (rt.Status == McpStatus.NeedsAuth && rt.Fingerprint == fingerprint)
            {
                continue; // 等用户重新登录，别反复弹浏览器
            }
            _ = ConnectInBackgroundAsync(id, fingerprint == rt.Fingerprint ? "恢复连接" : "配置已更新");
        }
    }

    private async Task ConnectInBackgroundAsync(string id, string why)
    {
        try
        {
            Log.Info($"MCP 连接器 {id}：{why}");
            await ConnectAsync(id, null, interactive: false, _life.Token);
        }
        catch (Exception ex)
        {
            Log.Warn($"MCP 连接器 {id} 连接失败：{ex.Message}");
        }
    }

    private async Task RefreshVendorsAsync(bool force)
    {
        if (!force && DateTime.Now - _vendorsAt < TimeSpan.FromMinutes(2) && _vendors.Count > 0)
        {
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var list = await _server.GetMcpVendorsAsync(cts.Token);
        lock (_gate)
        {
            _vendors = list;
            _vendorsAt = DateTime.Now;
        }
    }

    private List<McpVendor> Vendors()
    {
        lock (_gate)
        {
            return _vendors.ToList();
        }
    }

    // ---------- 给界面的列表 ----------

    public async Task<List<McpVendorView>> ListAsync(bool refresh)
    {
        try
        {
            await RefreshVendorsAsync(force: refresh);
        }
        catch (Exception ex) when (Vendors().Count > 0)
        {
            Log.Warn($"刷新 MCP 连接器列表失败，先用上次的：{ex.Message}");
        }
        return Vendors().Select(View).ToList();
    }

    public McpVendorView? Get(string id) => Vendors().FirstOrDefault(v => v.Id == id) is { } v ? View(v) : null;

    private McpVendorView View(McpVendor v)
    {
        var rt = _runtime.TryGetValue(v.Id, out var r) ? r : null;
        var saved = _store.Get(v.Id);
        var values = saved?.Values ?? new Dictionary<string, string>();
        return new McpVendorView
        {
            Id = v.Id,
            Name = v.Name,
            Description = v.Description,
            Detail = v.Detail,
            Icon = v.Icon,
            Publisher = v.Publisher,
            Category = v.Category,
            Homepage = v.Homepage,
            Transport = v.Transport,
            Auth = v.Auth,
            Examples = v.Examples,
            Fields = v.Fields.Select(f => new McpFieldView
            {
                Key = f.Key,
                Label = string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label,
                Secret = f.Secret,
                Required = f.Required,
                Placeholder = f.Placeholder,
                Help = f.Help,
                // 管理员预填了的员工不用填，也不给看
                Preset = v.Preset.TryGetValue(f.Key, out var p) && !string.IsNullOrEmpty(p),
                HasValue = values.TryGetValue(f.Key, out var mine) && !string.IsNullOrEmpty(mine),
                // 密钥不回显；不是密钥的（例如团队名）回显方便修改
                Value = !f.Secret && values.TryGetValue(f.Key, out var plain) ? plain : "",
            }).ToList(),
            NeedsInput = McpTemplate.Missing(v, Merge(v, values)).ToList(),
            Status = (rt?.Status ?? McpStatus.Disconnected).ToString().ToLowerInvariant(),
            Error = rt?.Error ?? "",
            Enabled = saved?.Enabled ?? false,
            ServerName = rt?.Connection?.Client.ServerName ?? "",
            TransportUsed = rt?.Transport ?? "",
            ConnectedAt = rt?.ConnectedAt,
            Tools = (rt?.Connection?.Tools ?? Array.Empty<McpToolInfo>()).Select(t => new McpToolView
            {
                Name = t.Name,
                Title = t.Title,
                Description = t.Description.Length > 300 ? t.Description[..300] + "…" : t.Description,
                ReadOnly = t.ReadOnly,
            }).ToList(),
        };
    }

    private static Dictionary<string, string> Merge(McpVendor v, IReadOnlyDictionary<string, string> userValues)
    {
        var all = new Dictionary<string, string>(v.Preset, StringComparer.Ordinal);
        foreach (var (k, val) in userValues)
        {
            if (!string.IsNullOrEmpty(val))
            {
                all[k] = val;
            }
        }
        return all;
    }

    // ---------- 连接 / 断开 ----------

    /// <summary>
    /// 连接。values 是员工这次填的值（空字符串表示「沿用上次填的」）；
    /// interactive=true 时需要登录就打开浏览器，后台重连时不弹。
    /// </summary>
    public async Task<McpVendorView> ConnectAsync(string id, IReadOnlyDictionary<string, string>? values, bool interactive, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        var rt = _runtime.GetOrAdd(id, _ => new Runtime());
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        rt.Attempt = attempt;
        try
        {
            if (!Vendors().Any(v => v.Id == id))
            {
                await RefreshVendorsAsync(force: true);
            }
            var vendor = Vendors().FirstOrDefault(v => v.Id == id) ?? throw new McpException("这个连接器已经被管理员下架了");

            // 先把这次填的值记下来（加密），空的表示沿用
            var saved = _store.Get(id) ?? new SavedConnection();
            if (values is not null)
            {
                foreach (var (k, v) in values)
                {
                    if (!string.IsNullOrWhiteSpace(v) && vendor.Fields.Any(f => f.Key == k))
                    {
                        saved.Values[k] = v.Trim();
                    }
                }
            }
            var merged = Merge(vendor, saved.Values);
            var missing = McpTemplate.Missing(vendor, merged);
            if (missing.Count > 0)
            {
                var labels = vendor.Fields.Where(f => missing.Contains(f.Key)).Select(f => string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label);
                throw new McpException($"请先填写：{string.Join("、", labels)}");
            }
            saved.Enabled = true;
            _store.Put(id, saved);

            await DropConnectionAsync(id, rt);
            Set(id, rt, McpStatus.Connecting, "");

            var config = McpTemplate.Resolve(vendor, saved.Values);
            McpConnection connection;
            try
            {
                connection = await OpenAsync(vendor, config, saved, interactive, rt, attempt.Token);
            }
            catch (McpAuthRequiredException ex)
            {
                rt.Fingerprint = vendor.ConnectionFingerprint();
                Set(id, rt, McpStatus.NeedsAuth,
                    WantsBrowserLogin(vendor, ex) ? "需要登录，点「连接」在浏览器里登录"
                    : vendor.Auth == "fields" ? $"{ex.Message}，请检查填写的密钥"
                    : $"{ex.Message}。这个服务需要凭证，请让 IT 在后台设置登录方式");
                throw;
            }

            rt.Connection = connection;
            rt.Transport = connection.TransportUsed;
            rt.Fingerprint = vendor.ConnectionFingerprint();
            rt.ConnectedAt = DateTimeOffset.Now;
            rt.Retries = 0;
            connection.Closed += reason => OnClosed(id, connection, reason);
            connection.ToolsChanged += () => Register(id, vendor, connection);
            Register(id, vendor, connection);
            Set(id, rt, McpStatus.Connected, "");
            Log.Info($"MCP 连接器 {id} 已连接（{connection.TransportUsed}，{connection.Tools.Count} 个工具）");
            return View(vendor);
        }
        catch (OperationCanceledException) when (!_life.IsCancellationRequested)
        {
            Set(id, rt, McpStatus.Disconnected, "");
            throw new McpException("已取消");
        }
        catch (McpAuthRequiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is McpException ? ex.Message : $"连接失败：{ex.Message}";
            Set(id, rt, McpStatus.Failed, message);
            throw new McpException(message, ex);
        }
        finally
        {
            rt.Attempt = null;
            gate.Release();
        }
    }

    private async Task<McpConnection> OpenAsync(McpVendor vendor, McpServerConfig config, SavedConnection saved, bool interactive, Runtime rt, CancellationToken ct)
    {
        // 「填写密钥」的用自己的头；其余的有登录令牌就带上（标成「不用登录」但对方其实要 OAuth 的，登录过一次后也能用）
        Func<CancellationToken, Task<string?>>? bearer = vendor.Auth != "fields" ? token => BearerAsync(vendor.Id, token) : null;
        try
        {
            return await McpConnection.ConnectAsync(config, _http, _clientVersion, bearer, _settings.ResolveWorkspace(null), ct);
        }
        catch (McpAuthRequiredException ex) when (interactive && WantsBrowserLogin(vendor, ex))
        {
            // 没登录过或者登录过期：在浏览器里登录一次，回来再连
            Set(vendor.Id, rt, McpStatus.Authorizing, "请在打开的浏览器里登录并授权");
            await AuthorizeAsync(vendor, new Uri(config.Url), ex.WwwAuthenticate, ct);
            try
            {
                return await McpConnection.ConnectAsync(config, _http, _clientVersion, bearer, _settings.ResolveWorkspace(null), ct);
            }
            catch (McpAuthRequiredException again)
            {
                // 刚登录完拿到的令牌还被拒：不是用户的问题，是令牌和这个服务对不上（受众、权限范围、地址），
                // 把对方的原话和细节都记下来，界面上说清楚，别让人以为是没登录成功
                Log.Warn($"MCP 连接器 {vendor.Id}：登录后令牌仍被拒绝。地址 {config.Url}；WWW-Authenticate：{again.WwwAuthenticate ?? "（无）"}");
                throw new McpException(
                    "浏览器里登录成功了，但对方仍然拒绝这次连接" +
                    (again.Reason.Length > 0 ? $"（对方说：{again.Reason}）" : "") +
                    "。多半是这个服务要求的权限范围（scope）或 client_id 需要 IT 在后台配置，请把这句话发给 IT。", again);
            }
        }
    }

    /// <summary>
    /// 要不要走浏览器登录：后台标了「浏览器授权登录」的当然要；标了「不用登录」、对方却回了带 OAuth
    /// 元数据的 401 的，说明后台没标对，也照样走——员工不该因为后台少勾一项就连不上。
    /// </summary>
    private static bool WantsBrowserLogin(McpVendor vendor, McpAuthRequiredException ex) =>
        vendor.Auth == "oauth"
        || (vendor.Auth == "none" && vendor.Transport != "stdio"
            && (ex.WwwAuthenticate ?? "").Contains("resource_metadata", StringComparison.OrdinalIgnoreCase));

    /// <summary>当前可用的 access_token，快过期了先刷新。</summary>
    private async Task<string?> BearerAsync(string id, CancellationToken ct)
    {
        var saved = _store.Get(id);
        var tokens = saved?.Tokens;
        if (tokens is null || tokens.AccessToken.Length == 0)
        {
            return null;
        }
        if (tokens.NeedsRefresh(DateTimeOffset.Now))
        {
            tokens = await McpOAuth.RefreshAsync(_http, tokens, DateTimeOffset.Now, ct);
            saved!.Tokens = tokens;
            _store.Put(id, saved);
        }
        return tokens.AccessToken;
    }

    private async Task AuthorizeAsync(McpVendor vendor, Uri url, string? challenge, CancellationToken ct)
    {
        using var receiver = new LoopbackReceiver();
        vendor.OAuth.TryGetValue("client_id", out var clientId);
        vendor.OAuth.TryGetValue("scopes", out var scopes);
        var auth = await McpOAuth.BeginAsync(_http, url, challenge, receiver.RedirectUri, clientId, scopes, ct);
        Process.Start(new ProcessStartInfo(auth.AuthorizeUrl.ToString()) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        IReadOnlyDictionary<string, string> query;
        try
        {
            query = await receiver.WaitAsync(auth.State,
                NativeStrings.T("mcp.oauthDone"), NativeStrings.T("mcp.oauthFailed"), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException("5 分钟内没有完成登录，请重新连接");
        }
        if (query.TryGetValue("error", out var error))
        {
            throw new McpException($"登录没有完成：{(query.TryGetValue("error_description", out var d) ? d : error)}");
        }
        if (!query.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw new McpException("登录回调里没有授权码");
        }
        var tokens = await McpOAuth.ExchangeAsync(_http, auth, code, DateTimeOffset.Now, ct);
        var saved = _store.Get(vendor.Id) ?? new SavedConnection { Enabled = true };
        saved.Tokens = tokens;
        _store.Put(vendor.Id, saved);
    }

    /// <summary>用户在界面上点了「取消」（例如浏览器登录不想做了）。</summary>
    public void Cancel(string id)
    {
        if (_runtime.TryGetValue(id, out var rt))
        {
            rt.Attempt?.Cancel();
        }
    }

    /// <summary>断开。forget=true 连填过的密钥和登录令牌一起删掉。</summary>
    public async Task<McpVendorView?> DisconnectAsync(string id, bool forget)
    {
        Cancel(id);
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (forget)
            {
                _store.Remove(id);
            }
            else if (_store.Get(id) is { } saved)
            {
                saved.Enabled = false;
                _store.Put(id, saved);
            }
            await DisconnectCoreAsync(id, McpStatus.Disconnected, "");
        }
        finally
        {
            gate.Release();
        }
        return Get(id);
    }

    private async Task DisconnectCoreAsync(string id, McpStatus status, string message)
    {
        var rt = _runtime.GetOrAdd(id, _ => new Runtime());
        await DropConnectionAsync(id, rt);
        Set(id, rt, status, message);
    }

    private async Task DropConnectionAsync(string id, Runtime rt)
    {
        var prefix = McpNames.ServerPrefix(id);
        _tools.RemoveWhere(t => t is McpTool m && m.ServerId == id || t.Name.StartsWith(prefix, StringComparison.Ordinal));
        var connection = rt.Connection;
        rt.Connection = null;
        rt.Transport = "";
        rt.ConnectedAt = null;
        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"关闭 MCP 连接 {id} 出错：{ex.Message}");
            }
        }
    }

    private void Register(string id, McpVendor vendor, McpConnection connection)
    {
        var tools = connection.Tools.Select(t => (ITool)new McpTool(id, vendor.Name, t, connection.CallAsync)).ToList();
        _tools.Replace(t => t is McpTool m && m.ServerId == id, tools);
        Changed?.Invoke(id);
    }

    /// <summary>
    /// 连接断了：远程的按 1、2、4、8、16 秒退避重连五次（和 Claude Code 一样），
    /// 本机进程不自动重启——它退出多半是自己出了问题，反复拉起只会反复失败。
    /// </summary>
    private void OnClosed(string id, McpConnection connection, string reason)
    {
        if (!_runtime.TryGetValue(id, out var rt) || rt.Connection != connection || _life.IsCancellationRequested)
        {
            return; // 是我们自己关的，或者已经换了新连接
        }
        Log.Warn($"MCP 连接器 {id} 断开：{reason}");
        _tools.RemoveWhere(t => t is McpTool m && m.ServerId == id);
        rt.Connection = null;
        if (rt.Transport == "stdio" || rt.Retries >= 5)
        {
            Set(id, rt, McpStatus.Failed, $"连接断开：{reason}");
            return;
        }
        var delay = TimeSpan.FromSeconds(Math.Pow(2, rt.Retries++));
        Set(id, rt, McpStatus.Connecting, "连接断开，正在重连…");
        _ = Task.Delay(delay, _life.Token).ContinueWith(_ => ReconnectAsync(id, reason), TaskScheduler.Default);
    }

    private async Task ReconnectAsync(string id, string why)
    {
        if (_life.IsCancellationRequested || _store.Get(id) is not { Enabled: true })
        {
            return;
        }
        var retries = _runtime.TryGetValue(id, out var rt) ? rt.Retries : 0;
        try
        {
            await ConnectAsync(id, null, interactive: false, _life.Token);
        }
        catch (Exception ex)
        {
            Log.Warn($"MCP 连接器 {id} 重连失败（{why}）：{ex.Message}");
            if (_runtime.TryGetValue(id, out var r) && retries < 5 && r.Status == McpStatus.Failed)
            {
                r.Retries = retries + 1;
                _ = Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retries + 1)), _life.Token)
                    .ContinueWith(_ => ReconnectAsync(id, why), TaskScheduler.Default);
            }
        }
    }

    private void Set(string id, Runtime rt, McpStatus status, string error)
    {
        rt.Status = status;
        rt.Error = error;
        Changed?.Invoke(id);
    }

    /// <summary>系统提示词里要说的已连接服务。</summary>
    public IReadOnlyList<McpPromptInfo> PromptInfo()
    {
        var names = Vendors().ToDictionary(v => v.Id, v => v.Name);
        return _runtime
            .Where(p => p.Value.Status == McpStatus.Connected && p.Value.Connection is not null)
            .Select(p => new McpPromptInfo(
                p.Key,
                names.TryGetValue(p.Key, out var n) ? n : p.Key,
                p.Value.Connection!.Tools.Count,
                p.Value.Connection.Client.Instructions))
            .OrderBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public void Dispose()
    {
        _life.Cancel();
        foreach (var (id, rt) in _runtime)
        {
            try
            {
                DropConnectionAsync(id, rt).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // 退出时尽力而为
            }
        }
        _http.Dispose();
    }

    // ---------- 运行时状态 ----------

    private sealed class Runtime
    {
        public McpStatus Status { get; set; } = McpStatus.Disconnected;
        public string Error { get; set; } = "";
        public McpConnection? Connection { get; set; }
        public string Transport { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public DateTimeOffset? ConnectedAt { get; set; }
        public int Retries { get; set; }
        public CancellationTokenSource? Attempt { get; set; }
    }

    // ---------- 本机存储 ----------

    public sealed class SavedConnection
    {
        public bool Enabled { get; set; }

        /// <summary>员工填的值（内存里是明文，落盘时逐项 DPAPI 加密）。</summary>
        public Dictionary<string, string> Values { get; set; } = new();

        public McpTokens? Tokens { get; set; }
    }

    /// <summary>mcp-connections.json。密钥和令牌逐项用 DPAPI 包起来，拷到别的电脑、别的账号上解不开。</summary>
    private sealed class Store
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SavedConnection> _items;

        private Store(Dictionary<string, SavedConnection> items)
        {
            _items = items;
        }

        private sealed class FileShape
        {
            public Dictionary<string, FileEntry> Connections { get; set; } = new();
        }

        private sealed class FileEntry
        {
            public bool Enabled { get; set; }
            public Dictionary<string, string> Values { get; set; } = new();
            public string Tokens { get; set; } = "";
        }

        public static Store Load()
        {
            var items = new Dictionary<string, SavedConnection>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(StoreFile))
                {
                    var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(StoreFile), Json) ?? new FileShape();
                    foreach (var (id, entry) in shape.Connections)
                    {
                        var tokensJson = DataProtection.Unprotect(entry.Tokens);
                        items[id] = new SavedConnection
                        {
                            Enabled = entry.Enabled,
                            Values = entry.Values.ToDictionary(p => p.Key, p => DataProtection.Unprotect(p.Value)),
                            Tokens = string.IsNullOrEmpty(tokensJson) ? null : JsonSerializer.Deserialize<McpTokens>(tokensJson),
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"读取 MCP 连接记录失败，按没有连接处理：{ex.Message}");
            }
            return new Store(items);
        }

        public SavedConnection? Get(string id)
        {
            lock (_gate)
            {
                return _items.TryGetValue(id, out var s) ? Clone(s) : null;
            }
        }

        public List<(string Id, SavedConnection Saved)> Snapshot()
        {
            lock (_gate)
            {
                return _items.Select(p => (p.Key, Clone(p.Value))).ToList();
            }
        }

        public void Put(string id, SavedConnection saved)
        {
            lock (_gate)
            {
                _items[id] = Clone(saved);
                Save();
            }
        }

        public void Remove(string id)
        {
            lock (_gate)
            {
                _items.Remove(id);
                Save();
            }
        }

        private static SavedConnection Clone(SavedConnection s) => new()
        {
            Enabled = s.Enabled,
            Values = new Dictionary<string, string>(s.Values),
            Tokens = s.Tokens,
        };

        private void Save()
        {
            try
            {
                var shape = new FileShape();
                foreach (var (id, s) in _items)
                {
                    shape.Connections[id] = new FileEntry
                    {
                        Enabled = s.Enabled,
                        Values = s.Values.ToDictionary(p => p.Key, p => DataProtection.Protect(p.Value)),
                        Tokens = s.Tokens is null ? "" : DataProtection.Protect(JsonSerializer.Serialize(s.Tokens)),
                    };
                }
                Directory.CreateDirectory(AppPaths.Root);
                var tmp = StoreFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(shape, Json));
                File.Move(tmp, StoreFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"保存 MCP 连接记录失败：{ex.Message}");
            }
        }
    }
}

public enum McpStatus
{
    Disconnected,
    Connecting,
    Authorizing,
    Connected,
    NeedsAuth,
    Failed,
}

/// <summary>界面上的一张连接器卡片。</summary>
public sealed class McpVendorView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Category { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string Transport { get; set; } = "";
    public string Auth { get; set; } = "";
    public List<string> Examples { get; set; } = new();
    public List<McpFieldView> Fields { get; set; } = new();
    public List<string> NeedsInput { get; set; } = new();

    /// <summary>disconnected / connecting / authorizing / connected / needsauth / failed</summary>
    public string Status { get; set; } = "disconnected";
    public string Error { get; set; } = "";

    /// <summary>员工是不是打开了这个连接器（连不上也算打开着，下次启动会再试）。</summary>
    public bool Enabled { get; set; }
    public string ServerName { get; set; } = "";
    public string TransportUsed { get; set; } = "";
    public DateTimeOffset? ConnectedAt { get; set; }
    public List<McpToolView> Tools { get; set; } = new();
}

public sealed class McpFieldView
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Secret { get; set; }
    public bool Required { get; set; }
    public string Placeholder { get; set; } = "";
    public string Help { get; set; } = "";
    public bool Preset { get; set; }
    public bool HasValue { get; set; }
    public string Value { get; set; } = "";
}

public sealed class McpToolView
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool ReadOnly { get; set; }
}
