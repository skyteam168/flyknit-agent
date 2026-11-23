using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Flyknit.Core.Lsp;

/// <summary>
/// 一个正在跑的语言服务（一个项目根 + 一种语言）：启动、初始化、同步打开的文档（整篇同步，简单可靠）、发请求、收诊断。
/// 语言服务按参数列表启动（不经过 Shell），工作目录是项目根。
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly JsonRpcConnection _rpc;
    private readonly Process? _process;
    private readonly ConcurrentDictionary<string, (int Version, string Text, string LanguageId)> _docs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonArray> _diagnostics = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _docLock = new(1, 1);

    private readonly string _exe;

    private LspClient(LspServerSpec spec, string root, JsonRpcConnection rpc, Process? process, string exe = "")
    {
        _exe = exe;
        Spec = spec;
        Root = root;
        _rpc = rpc;
        _process = process;
        _rpc.OnNotification = OnNotification;
        _rpc.OnRequest = OnRequest;
        _rpc.Closed += () => Exited = true;
    }

    public LspServerSpec Spec { get; }
    public string Root { get; }
    public JsonObject Capabilities { get; private set; } = new();
    public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.Now;
    public bool Exited { get; private set; }

    /// <summary>诊断更新了（uri）。</summary>
    public event Action<string>? DiagnosticsChanged;

    /// <summary>启动语言服务进程并初始化。<paramref name="dataDir"/> 换掉参数里的 {data}。</summary>
    public static async Task<LspClient> StartAsync(LspServerSpec spec, LspCommand command, string exe, string root, string dataDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in command.Args)
        {
            psi.ArgumentList.Add(a.Replace("{data}", dataDir, StringComparison.Ordinal));
        }
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"{spec.Name} 语言服务没有启动");
        // 标准错误是日志，读掉免得缓冲区满了卡住对方
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is not null)
                {
                }
            }
            catch (Exception)
            {
            }
        });
        var rpc = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var client = new LspClient(spec, root, rpc, process, exe);
        try
        {
            await client.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return client;
    }

    /// <summary>用现成的流连接（测试用的假语言服务）。</summary>
    public static async Task<LspClient> ConnectAsync(LspServerSpec spec, string root, Stream fromServer, Stream toServer, CancellationToken ct)
    {
        var client = new LspClient(spec, root, new JsonRpcConnection(fromServer, toServer), null);
        await client.InitializeAsync(ct).ConfigureAwait(false);
        return client;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        _rpc.Start();
        var rootUri = LspPaths.ToUri(Root);
        var result = await _rpc.RequestAsync("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["clientInfo"] = new JsonObject { ["name"] = "FlyknitBuddy" },
            ["rootUri"] = rootUri,
            ["rootPath"] = Root,
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(Root) }),
            ["initializationOptions"] = InitOptions(),
            ["capabilities"] = new JsonObject
            {
                ["general"] = new JsonObject { ["positionEncodings"] = new JsonArray("utf-16") },
                ["workspace"] = new JsonObject
                {
                    ["configuration"] = true,
                    ["workspaceFolders"] = true,
                    ["applyEdit"] = false,
                },
                ["textDocument"] = new JsonObject
                {
                    ["synchronization"] = new JsonObject { ["didSave"] = true, ["dynamicRegistration"] = false },
                    ["completion"] = new JsonObject
                    {
                        ["completionItem"] = new JsonObject
                        {
                            ["snippetSupport"] = true,
                            ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                            ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("documentation", "detail", "additionalTextEdits") },
                        },
                        ["contextSupport"] = true,
                    },
                    ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                    ["signatureHelp"] = new JsonObject
                    {
                        ["signatureInformation"] = new JsonObject
                        {
                            ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                            ["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true },
                        },
                    },
                    ["definition"] = new JsonObject { ["linkSupport"] = true },
                    ["typeDefinition"] = new JsonObject { ["linkSupport"] = true },
                    ["implementation"] = new JsonObject { ["linkSupport"] = true },
                    ["references"] = new JsonObject(),
                    ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                    ["rename"] = new JsonObject { ["prepareSupport"] = true },
                    ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = false },
                    // 拉取式诊断（TypeScript 7 等新的语言服务用这种）
                    ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false, ["relatedDocumentSupport"] = false },
                },
            },
        }, ct).ConfigureAwait(false);
        Capabilities = result?["capabilities"] as JsonObject ?? new JsonObject();
        await _rpc.NotifyAsync("initialized", new JsonObject(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// TS / Vue 的语言服务要知道 TypeScript 在哪：优先用项目自己 node_modules 里的（和项目版本一致），
    /// 没有就用语言服务旁边一起装的（IT 用 npm i -g typescript typescript-language-server 装在同一个地方）。
    /// Vue 不走「TS 插件」的混合模式（不依赖另一个服务）。
    /// </summary>
    private JsonObject? InitOptions()
    {
        if (Spec.Id is not ("typescript" or "vue"))
        {
            return null;
        }
        var lib = FindTypeScriptLib(Root, _exe);
        if (Spec.Id == "typescript")
        {
            return lib is null ? null : new JsonObject { ["tsserver"] = new JsonObject { ["path"] = Path.Combine(lib, "tsserver.js") } };
        }
        return new JsonObject
        {
            ["typescript"] = new JsonObject { ["tsdk"] = lib ?? "" },
            ["vue"] = new JsonObject { ["hybridMode"] = false },
        };
    }

    /// <summary>找 typescript/lib：项目的 node_modules → 语言服务可执行文件附近（npm 全局目录、.bin 的上一级、Unix 的 prefix/lib）。</summary>
    public static string? FindTypeScriptLib(string root, string exe)
    {
        var candidates = new List<string> { Path.Combine(root, "node_modules", "typescript", "lib") };
        if (exe.Length > 0 && Path.GetDirectoryName(exe) is { } dir)
        {
            candidates.Add(Path.Combine(dir, "node_modules", "typescript", "lib"));                 // %APPDATA%\npm\xxx.cmd
            candidates.Add(Path.Combine(dir, "..", "typescript", "lib"));                           // node_modules\.bin\xxx
            candidates.Add(Path.Combine(dir, "..", "lib", "node_modules", "typescript", "lib"));    // /usr/local/bin/xxx
        }
        return candidates.Select(Path.GetFullPath).FirstOrDefault(c => File.Exists(Path.Combine(c, "tsserver.js")));
    }

    // ---------- 文档同步 ----------

    /// <summary>
    /// 让语言服务知道这个文件现在的内容：没打开过就打开，打开过、内容变了就整篇替换。
    /// 编辑器里的（可能还没保存）和磁盘上的都走这里。
    /// </summary>
    public async Task SyncAsync(string fullPath, string text, CancellationToken ct)
    {
        Touch();
        var uri = LspPaths.ToUri(fullPath);
        await _docLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_docs.TryGetValue(uri, out var doc))
            {
                if (doc.Text == text)
                {
                    return;
                }
                var version = doc.Version + 1;
                _docs[uri] = (version, text, doc.LanguageId);
                await _rpc.NotifyAsync("textDocument/didChange", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
                    ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
                }, ct).ConfigureAwait(false);
            }
            else
            {
                var languageId = Spec.LanguageIdFor(Path.GetExtension(fullPath));
                _docs[uri] = (1, text, languageId);
                await _rpc.NotifyAsync("textDocument/didOpen", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = languageId, ["version"] = 1, ["text"] = text },
                }, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _docLock.Release();
        }
    }

    public bool IsOpen(string fullPath) => _docs.ContainsKey(LspPaths.ToUri(fullPath));

    public async Task CloseAsync(string fullPath, CancellationToken ct)
    {
        var uri = LspPaths.ToUri(fullPath);
        if (_docs.TryRemove(uri, out _))
        {
            _diagnostics.TryRemove(uri, out _);
            await _rpc.NotifyAsync("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }, ct).ConfigureAwait(false);
        }
    }

    // ---------- 请求、诊断 ----------

    public Task<JsonNode?> RequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        Touch();
        return _rpc.RequestAsync(method, @params, ct);
    }

    /// <summary>这个文件最近一次的诊断（语言服务推过来的）；没有为空数组。</summary>
    public JsonArray DiagnosticsFor(string fullPath) =>
        _diagnostics.TryGetValue(LspPaths.ToUri(fullPath), out var list) ? (JsonArray)list.DeepClone() : new JsonArray();

    /// <summary>
    /// 这个文件的诊断。支持拉取式诊断的语言服务（声明了 diagnosticProvider）直接问；
    /// 其余的等它推过来（改完内容后要算一会儿）：收到新的或者等满 <paramref name="timeout"/>。
    /// </summary>
    public async Task<JsonArray> WaitDiagnosticsAsync(string fullPath, TimeSpan timeout, CancellationToken ct)
    {
        if (Capabilities["diagnosticProvider"] is not null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout + TimeSpan.FromSeconds(5));
            try
            {
                var report = await RequestAsync("textDocument/diagnostic", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = LspPaths.ToUri(fullPath) },
                }, cts.Token).ConfigureAwait(false);
                if (report?["items"] is JsonArray items)
                {
                    _diagnostics[LspPaths.ToUri(fullPath)] = (JsonArray)items.DeepClone();
                }
                return DiagnosticsFor(fullPath);
            }
            catch (Exception ex) when (ex is JsonRpcException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                return DiagnosticsFor(fullPath);
            }
        }
        var uri = LspPaths.ToUri(fullPath);
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(string u)
        {
            if (u == uri)
            {
                got.TrySetResult();
            }
        }
        DiagnosticsChanged += On;
        try
        {
            await Task.WhenAny(got.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        }
        finally
        {
            DiagnosticsChanged -= On;
        }
        return DiagnosticsFor(fullPath);
    }

    private void OnNotification(string method, JsonNode? @params)
    {
        if (method == "textDocument/publishDiagnostics" && @params?["uri"]?.GetValue<string>() is { } uri)
        {
            _diagnostics[LspPaths.NormalizeUri(uri)] = @params["diagnostics"] as JsonArray is { } list ? (JsonArray)list.DeepClone() : new JsonArray();
            DiagnosticsChanged?.Invoke(LspPaths.NormalizeUri(uri));
        }
    }

    /// <summary>语言服务问我们的事：配置给空（用它自己的默认），注册能力、进度条答应，不替它改文件。</summary>
    private Task<JsonNode?> OnRequest(string method, JsonNode? @params)
    {
        JsonNode? result = method switch
        {
            "workspace/configuration" => new JsonArray((@params?["items"] as JsonArray ?? new JsonArray()).Select(_ => (JsonNode?)null).ToArray()),
            "workspace/workspaceFolders" => new JsonArray(new JsonObject { ["uri"] = LspPaths.ToUri(Root), ["name"] = Path.GetFileName(Root) }),
            "workspace/applyEdit" => new JsonObject { ["applied"] = false, ["failureReason"] = "不由语言服务直接改文件" },
            _ => null,
        };
        return Task.FromResult(result);
    }

    private void Touch() => LastUsed = DateTimeOffset.Now;

    public async ValueTask DisposeAsync()
    {
        if (!Exited)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _rpc.RequestAsync("shutdown", null, cts.Token).ConfigureAwait(false);
                await _rpc.NotifyAsync("exit", null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        _rpc.Dispose();
        if (_process is not null)
        {
            try
            {
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }
            _process.Dispose();
        }
        _docLock.Dispose();
    }
}

/// <summary>本机路径和 LSP 的 file:// 地址互转（Windows 盘符小写、冒号转义，和 VS Code 一样）。</summary>
public static class LspPaths
{
    public static string ToUri(string fullPath)
    {
        var path = Path.GetFullPath(fullPath).Replace('\\', '/');
        if (path.Length >= 2 && path[1] == ':')
        {
            path = "/" + char.ToLowerInvariant(path[0]) + "%3A" + path[2..];
        }
        var segments = path.Split('/').Select(s => s.Contains("%3A", StringComparison.Ordinal) ? s : Uri.EscapeDataString(s));
        return "file://" + string.Join('/', segments);
    }

    /// <summary>file:// 地址 → 本机路径；不是 file:// 返回 null。</summary>
    public static string? FromUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var path = Uri.UnescapeDataString(uri["file://".Length..]);
        if (path.Length >= 3 && path[0] == '/' && path[2] == ':')
        {
            path = char.ToUpperInvariant(path[1]) + path[2..];
            return path.Replace('/', '\\');
        }
        return OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;
    }

    /// <summary>语言服务发来的地址统一成我们自己的写法（盘符大小写、转义可能不一样）。</summary>
    public static string NormalizeUri(string uri) => FromUri(uri) is { } path ? ToUri(path) : uri;
}
