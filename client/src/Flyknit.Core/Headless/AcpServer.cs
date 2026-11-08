using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Headless;

/// <summary>
/// ACP（Agent Client Protocol，协议版本 1）服务：JSON-RPC 2.0，经标准输入输出、一行一条消息。
/// Zed、JetBrains（以及装了 ACP 插件的 VS Code）把 flyknit acp 当成外部 AI 助手启动，在 IDE 里对话、看工具调用。
///
/// 安全：
/// - 工具在这台电脑上执行，闸门和员工端一样（见 <see cref="HeadlessSession"/>）；需要确认时向 IDE 发 session/request_permission，
///   IDE 弹自己的确认框。「总是允许」只在员工端也能设「以后自动执行」的操作上出现；高危操作只给「允许一次 / 拒绝」，
///   IDE 回了没给过的选项一律按拒绝处理。
/// - IDE 在 session/new 里带来的 MCP 服务器不接：连接器只认 IT 在后台配的（员工不能自建，不变量 6）。
/// - 标准输出只写协议消息；日志写标准错误。
///
/// 会话保存（给了 <see cref="AcpSessionStore"/> 时）：每一轮结束存到本机，IDE 关掉、插件重启后可以在会话列表里
/// 找回来接着聊（session/list、session/load）。重新打开时权限档、安全设置按当时的配置来。
/// </summary>
public sealed class AcpServer
{
    public const int ProtocolVersion = 1;

    private readonly HeadlessEnvironment? _env;
    private readonly string _unavailable;
    private readonly PermissionMode _permission;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Action<string> _log;
    private readonly Func<Task>? _afterPrompt;
    private readonly string _version;
    private readonly AcpSessionStore? _store;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new(StringComparer.Ordinal);
    private readonly List<Task> _running = new();
    private int _nextId;

    /// <param name="env">为空表示连不上（没登录、IT 没开开关等）：照样回 initialize，开会话时把 <paramref name="unavailable"/> 告诉 IDE</param>
    public AcpServer(HeadlessEnvironment? env, PermissionMode permission, TextReader input, TextWriter output,
        Action<string>? log = null, Func<Task>? afterPrompt = null, string version = "0.1.0", string unavailable = "",
        AcpSessionStore? store = null)
    {
        _store = store;
        _env = env;
        _unavailable = unavailable.Length > 0 ? unavailable : "FlyknitBuddy 没有准备好";
        _permission = permission == PermissionMode.Full ? PermissionMode.Workspace : permission;
        _input = input;
        _output = output;
        _log = log ?? (_ => { });
        _afterPrompt = afterPrompt;
        _version = version;
    }

    /// <summary>一直读到输入结束（IDE 关掉了这个进程的标准输入）。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }
            if (line.Trim().Length == 0)
            {
                continue;
            }
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                await SendErrorAsync(null, -32700, "不是合法的 JSON");
                continue;
            }
            if (message is not JsonObject obj)
            {
                await SendErrorAsync(null, -32600, "消息要是 JSON 对象");
                continue;
            }
            // 字段类型不对（比如 method 是数字）按无效请求回，不能让一条坏消息把整个服务弄崩
            string? method = null;
            if (obj["method"] is { } m && !(m is JsonValue mv && mv.TryGetValue(out method)))
            {
                await SendErrorAsync(obj["id"], -32600, "method 要是字符串");
                continue;
            }
            var id = obj["id"];
            if (method is null)
            {
                // IDE 对我们发出的请求（确认）的回复
                if (id is not null && _pending.TryRemove(IdKey(id), out var waiter))
                {
                    waiter.TrySetResult(obj["error"] is null ? obj["result"] : null);
                }
                continue;
            }
            if (id is null)
            {
                HandleNotification(method, obj["params"] as JsonObject);
                continue;
            }
            // 请求并行处理：session/prompt 要跑很久，期间还要收取消通知和确认回复
            var task = Task.Run(() => HandleRequestAsync(id.DeepClone(), method, obj["params"] as JsonObject, ct), CancellationToken.None);
            lock (_running)
            {
                _running.RemoveAll(t => t.IsCompleted);
                _running.Add(task);
            }
        }
        // 输入结束：停下正在跑的，等它们收尾
        foreach (var session in _sessions.Values)
        {
            session.Cancel();
        }
        Task[] left;
        lock (_running)
        {
            left = _running.ToArray();
        }
        await Task.WhenAll(left);
    }

    private void HandleNotification(string method, JsonObject? p)
    {
        if (method == "session/cancel" && p?["sessionId"] is JsonValue v && v.TryGetValue(out string? sid) && _sessions.TryGetValue(sid, out var session))
        {
            _log($"IDE 取消了会话 {sid} 的这一轮");
            session.Cancel();
        }
    }

    private async Task HandleRequestAsync(JsonNode id, string method, JsonObject? p, CancellationToken ct)
    {
        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(),
                "authenticate" => new JsonObject(),
                "session/new" => NewSession(p),
                "session/prompt" => await PromptAsync(p, ct),
                "session/list" => ListSessions(p),
                "session/load" => await LoadSessionAsync(p),
                _ => throw new AcpError(-32601, $"不支持的方法：{method}"),
            };
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (AcpError e)
        {
            await SendErrorAsync(id, e.Code, e.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log($"处理 {method} 出错：{ex}");
            await SendErrorAsync(id, -32603, ex.Message);
        }
    }

    private JsonObject Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["agentCapabilities"] = new JsonObject
        {
            ["loadSession"] = _store is not null,
            ["promptCapabilities"] = new JsonObject { ["image"] = false, ["audio"] = false, ["embeddedContext"] = true },
            // 会话列表（session/list）：IDE 侧边栏据此列出以前的会话
            ["sessionCapabilities"] = _store is not null ? new JsonObject { ["list"] = new JsonObject() } : new JsonObject(),
        },
        ["authMethods"] = new JsonArray(),
        ["agentInfo"] = new JsonObject { ["name"] = "flyknit", ["title"] = "FlyknitBuddy", ["version"] = _version },
    };

    private JsonObject NewSession(JsonObject? p)
    {
        if (_env is null)
        {
            throw new AcpError(-32000, _unavailable);
        }
        var cwd = p?["cwd"]?.GetValue<string>() ?? throw new AcpError(-32602, "缺少 cwd");
        if (!Path.IsPathRooted(cwd) || !Directory.Exists(cwd))
        {
            throw new AcpError(-32602, $"工作目录不存在：{cwd}");
        }
        if (p?["mcpServers"] is JsonArray { Count: > 0 } mcp)
        {
            _log($"IDE 带来了 {mcp.Count} 个 MCP 服务器，未接入：FlyknitBuddy 的连接器只认 IT 在管理后台配置的");
        }
        var session = new HeadlessSession(_env, cwd, _permission, "acp-" + Guid.NewGuid().ToString("N")[..12]);
        _sessions[session.Id] = new AcpSession(session);
        _log($"新会话 {session.Id}，工作区 {session.Workspace}");
        return new JsonObject { ["sessionId"] = session.Id };
    }

    private async Task<JsonObject> PromptAsync(JsonObject? p, CancellationToken ct)
    {
        var sid = p?["sessionId"]?.GetValue<string>() ?? throw new AcpError(-32602, "缺少 sessionId");
        if (!_sessions.TryGetValue(sid, out var s))
        {
            throw new AcpError(-32602, $"没有这个会话：{sid}");
        }
        var text = PromptText(p?["prompt"] as JsonArray);
        if (text.Trim().Length == 0)
        {
            throw new AcpError(-32602, "prompt 里没有文字");
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!s.Begin(cts))
        {
            throw new AcpError(-32603, "这个会话上一轮还没结束");
        }
        try
        {
            var observer = new AcpObserver(this, sid);
            var confirm = new AcpConfirm(this, sid, observer.FlushAsync);
            s.FirstPrompt ??= text;
            var result = await s.Session.PromptAsync(text, confirm, observer, cts.Token);
            await observer.FlushAsync();
            return new JsonObject { ["stopReason"] = StopReason(result.StopReason) };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new JsonObject { ["stopReason"] = "cancelled" };
        }
        finally
        {
            // 每一轮结束都存一次（被取消的也存：停下时已经给发出的调用补了结果）
            _store?.Save(s.Session, s.FirstPrompt ?? text, s.CreatedAt, _log);
            s.End();
            if (_afterPrompt is not null)
            {
                await _afterPrompt();
            }
        }
    }

    /// <summary>以前的会话（最近的在前）。给了 cwd 只列这个项目的；一页 <see cref="ListPageSize"/> 个，cursor 是下一页的起点。</summary>
    private JsonObject ListSessions(JsonObject? p)
    {
        if (_store is null)
        {
            throw new AcpError(-32601, "不支持的方法：session/list");
        }
        var cwd = p?["cwd"] is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : null;
        var start = p?["cursor"] is JsonValue kv && kv.TryGetValue<string>(out var k) && int.TryParse(k, out var n) && n > 0 ? n : 0;
        var all = _store.List(cwd);
        var page = all.Skip(start).Take(ListPageSize).ToList();
        var result = new JsonObject
        {
            ["sessions"] = new JsonArray(page.Select(s => (JsonNode)new JsonObject
            {
                ["sessionId"] = s.Id,
                ["cwd"] = s.Cwd,
                ["title"] = s.Title,
                ["updatedAt"] = s.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            }).ToArray()),
        };
        if (start + page.Count < all.Count)
        {
            result["nextCursor"] = (start + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return result;
    }

    public const int ListPageSize = 50;

    /// <summary>
    /// 打开一个以前的会话：把历史一条条推给 IDE 显示（session/update），再回结果；之后的 session/prompt 接着这段历史聊。
    /// 工作区用保存时的；权限档、安全设置按现在的配置。
    /// </summary>
    private async Task<JsonObject> LoadSessionAsync(JsonObject? p)
    {
        if (_store is null)
        {
            throw new AcpError(-32601, "不支持的方法：session/load");
        }
        if (_env is null)
        {
            throw new AcpError(-32000, _unavailable);
        }
        var sid = p?["sessionId"] is JsonValue sv && sv.TryGetValue<string>(out var id) ? id : throw new AcpError(-32602, "缺少 sessionId");
        if (_sessions.TryGetValue(sid, out var open) && open.Running)
        {
            throw new AcpError(-32603, "这个会话正在进行，等这一轮结束再打开");
        }
        var saved = _store.Load(sid) ?? throw new AcpError(-32602, $"没有这个会话：{sid}");
        if (!Directory.Exists(saved.Cwd))
        {
            throw new AcpError(-32602, $"这个会话的工作目录已经不在了：{saved.Cwd}");
        }
        var session = new HeadlessSession(_env, saved.Cwd, _permission, saved.Id);
        session.Restore(saved.Messages, saved.Plan, saved.LoadedTools);
        _sessions[sid] = new AcpSession(session) { CreatedAt = saved.CreatedAt, FirstPrompt = saved.Title };
        _log($"打开以前的会话 {sid}（{session.History.Count} 条消息），工作区 {session.Workspace}");
        foreach (var update in Replay(session.History))
        {
            await NotifyAsync(sid, update);
        }
        return new JsonObject();
    }

    /// <summary>历史换成 IDE 能显示的更新：员工的话、回答、工具调用和结果（思考过程不重放）。</summary>
    public static IEnumerable<JsonObject> Replay(IReadOnlyList<ChatMessage> history)
    {
        static JsonObject Chunk(string kind, string text) =>
            new() { ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } };
        var results = history.Where(m => m.Role == ChatRole.Tool && m.ToolCallId is not null)
            .GroupBy(m => m.ToolCallId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        foreach (var m in history)
        {
            switch (m.Role)
            {
                case ChatRole.User when m.Content.Length > 0:
                    yield return Chunk("user_message_chunk", m.Content);
                    break;
                case ChatRole.Assistant:
                    if (m.Content.Length > 0)
                    {
                        yield return Chunk("agent_message_chunk", m.Content);
                    }
                    foreach (var call in m.ToolCalls)
                    {
                        var update = new JsonObject
                        {
                            ["sessionUpdate"] = "tool_call",
                            ["toolCallId"] = call.Id,
                            ["title"] = call.Name,
                            ["kind"] = ToolKind(call.Name),
                            ["status"] = "completed",
                            ["rawInput"] = RawInput(call.ArgumentsJson),
                        };
                        if (results.TryGetValue(call.Id, out var r))
                        {
                            var output = r.Content.Length > ReplayOutputChars ? r.Content[..ReplayOutputChars] + "\n…（只显示前一部分）" : r.Content;
                            update["content"] = new JsonArray(Text(output));
                        }
                        yield return update;
                    }
                    break;
            }
        }
    }

    /// <summary>重放时每个工具结果给 IDE 看的长度（只是展示，模型那边是完整的）。</summary>
    private const int ReplayOutputChars = 2000;

    /// <summary>IDE 发来的内容块拼成一段话：文字原样；引用的文件写成路径；带内容的文件附在后面（模型要读整文件可以自己用 read_file）。</summary>
    public static string PromptText(JsonArray? blocks)
    {
        var parts = new List<string>();
        foreach (var block in blocks ?? new JsonArray())
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text":
                    parts.Add(block["text"]?.GetValue<string>() ?? "");
                    break;
                case "resource_link":
                    parts.Add($"[文件：{FilePath(block["uri"]?.GetValue<string>() ?? "")}]");
                    break;
                case "resource" when block["resource"] is JsonObject r:
                    var uri = FilePath(r["uri"]?.GetValue<string>() ?? "");
                    if (r["text"]?.GetValue<string>() is { } body)
                    {
                        parts.Add($"<引用的文件 path=\"{uri}\">\n{body}\n</引用的文件>");
                    }
                    else
                    {
                        parts.Add($"[文件：{uri}]");
                    }
                    break;
            }
        }
        return string.Join("\n\n", parts);
    }

    private static string FilePath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : uri;

    public static string StopReason(AgentStopReason reason) => reason switch
    {
        AgentStopReason.Cancelled => "cancelled",
        AgentStopReason.MaxSteps or AgentStopReason.Budget => "max_turn_requests",
        // 出错、空转、失败太多：原因已经作为回答发给 IDE 了，这一轮按结束处理
        _ => "end_turn",
    };

    public static string ToolKind(string name) => name switch
    {
        "read_file" or "list_dir" or "load_skill" => "read",
        "search_files" or "grep" or "search_skills" => "search",
        "write_file" or "edit_file" or "multi_edit" => "edit",
        "delete_path" => "delete",
        "update_plan" => "think",
        _ when name == "run_shell" || name.StartsWith("git_", StringComparison.Ordinal) => "execute",
        _ => "other",
    };

    // ---------- 发消息 ----------

    internal Task NotifyAsync(string sessionId, JsonObject update) =>
        SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = update },
        });

    /// <summary>向 IDE 发请求并等回复（确认）。IDE 回错误或连接断了返回 null。</summary>
    internal async Task<JsonNode?> RequestAsync(string method, JsonObject p, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id.ToString()] = waiter;
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p });
        using var reg = ct.Register(() => waiter.TrySetCanceled(ct));
        try
        {
            return await waiter.Task;
        }
        finally
        {
            _pending.TryRemove(id.ToString(), out _);
        }
    }

    private static string IdKey(JsonNode id) => id.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : id.ToJsonString();

    private Task SendErrorAsync(JsonNode? id, int code, string message) =>
        SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    private async Task SendAsync(JsonObject message)
    {
        var text = message.ToJsonString();
        await _writeLock.WaitAsync();
        try
        {
            await _output.WriteLineAsync(text);
            await _output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private sealed class AcpError : Exception
    {
        public AcpError(int code, string message) : base(message) => Code = code;
        public int Code { get; }
    }

    /// <summary>一个 IDE 会话：同一时间只跑一轮。</summary>
    private sealed class AcpSession
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _current;

        public AcpSession(HeadlessSession session) => Session = session;

        public HeadlessSession Session { get; }

        /// <summary>会话什么时候开的、第一句话是什么（保存时用作标题）。</summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
        public string? FirstPrompt { get; set; }

        public bool Running
        {
            get
            {
                lock (_gate)
                {
                    return _current is not null;
                }
            }
        }

        public bool Begin(CancellationTokenSource cts)
        {
            lock (_gate)
            {
                if (_current is not null)
                {
                    return false;
                }
                _current = cts;
                return true;
            }
        }

        public void End()
        {
            lock (_gate)
            {
                _current = null;
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                try
                {
                    _current?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    /// <summary>
    /// 确认交给 IDE。选项：允许一次、拒绝；能设「以后自动执行」的操作（非高危、有可记规则、IT 没要求每次确认）才多一个「总是允许」。
    /// IDE 取消、回了没给过的选项、连接断了：都按拒绝。
    /// </summary>
    internal sealed class AcpConfirm : IConfirmationHandler
    {
        public const string AllowOnce = "allow_once";
        public const string AllowAlways = "allow_always";
        public const string RejectOnce = "reject_once";

        private readonly AcpServer _server;
        private readonly string _sessionId;
        private readonly Func<Task>? _flush;

        /// <param name="flush">先把之前的回答、工具调用推完，确认框不会跑到它们前面</param>
        public AcpConfirm(AcpServer server, string sessionId, Func<Task>? flush = null)
        {
            _server = server;
            _sessionId = sessionId;
            _flush = flush;
        }

        public static bool OffersAlways(PolicyDecision d) => NonInteractiveConfirm.CanAutoApprove(d);

        public static JsonArray Options(PolicyDecision d)
        {
            var options = new JsonArray { Option(AllowOnce, "允许这一次", "allow_once") };
            if (OffersAlways(d))
            {
                options.Add(Option(AllowAlways, "总是允许（同类操作以后自动执行）", "allow_always"));
            }
            options.Add(Option(RejectOnce, "拒绝", "reject_once"));
            return options;

            static JsonObject Option(string id, string name, string kind) => new() { ["optionId"] = id, ["name"] = name, ["kind"] = kind };
        }

        /// <summary>IDE 的回复对应哪个选择。没给过的选项按拒绝。</summary>
        public static ConfirmChoice Map(JsonNode? response, PolicyDecision d)
        {
            if (response?["outcome"] is not JsonObject outcome || outcome["outcome"]?.GetValue<string>() != "selected")
            {
                return ConfirmChoice.Reject;
            }
            return outcome["optionId"]?.GetValue<string>() switch
            {
                AllowOnce => ConfirmChoice.AllowOnce,
                AllowAlways when OffersAlways(d) => ConfirmChoice.AllowAlways,
                _ => ConfirmChoice.Reject,
            };
        }

        public async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            var p = new JsonObject
            {
                ["sessionId"] = _sessionId,
                ["toolCall"] = new JsonObject
                {
                    ["toolCallId"] = request.Call.Id,
                    ["title"] = request.Summary,
                    ["kind"] = ToolKind(request.Call.Name),
                    ["status"] = "pending",
                    ["rawInput"] = RawInput(request.Call.ArgumentsJson),
                    ["content"] = request.Decision.Reason.Length > 0 || request.Rationale.Length > 0
                        ? new JsonArray(Text(string.Join("\n", new[] { request.Rationale, request.Decision.Reason }.Where(x => x.Length > 0))))
                        : null,
                },
                ["options"] = Options(request.Decision),
            };
            try
            {
                if (_flush is not null)
                {
                    await _flush();
                }
                var response = await _server.RequestAsync("session/request_permission", p, ct);
                return Map(response, request.Decision);
            }
            catch (OperationCanceledException)
            {
                return ConfirmChoice.Reject;
            }
        }
    }

    internal static JsonNode? RawInput(string argumentsJson)
    {
        try
        {
            return JsonNode.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    internal static JsonObject Text(string text) =>
        new() { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } };

    /// <summary>运行过程推给 IDE：回答 / 思考的片段、工具调用开始和结束、计划。</summary>
    private sealed class AcpObserver : IAgentObserver
    {
        /// <summary>工具结果给 IDE 看的长度上限（完整结果在模型那边，这里只是展示）。</summary>
        private const int MaxOutput = 4000;

        private readonly AcpServer _server;
        private readonly string _sid;
        private readonly object _gate = new();

        /// <summary>按顺序一条条发：回答的片段乱了序 IDE 里就是乱码。</summary>
        private Task _chain = Task.CompletedTask;
        private bool _streamed;

        /// <summary>
        /// 回答 / 思考的片段攒一会儿再发：模型每吐几个字就来一个片段，一条条发过去 IDE 每条都要重新渲染，
        /// 思考一长界面就卡。攒 <see cref="BatchDelay"/> 或 <see cref="MaxBatchChars"/> 个字发一次，
        /// 消息数少一两个数量级，看起来还是连续的。别的更新（工具调用、计划）发之前先把攒着的发掉，顺序不变。
        /// </summary>
        internal static TimeSpan BatchDelay { get; set; } = TimeSpan.FromMilliseconds(60);
        private const int MaxBatchChars = 2000;
        private string? _pendingKind;
        private readonly StringBuilder _pending = new();
        private Timer? _timer;

        public AcpObserver(AcpServer server, string sid)
        {
            _server = server;
            _sid = sid;
        }

        private void Push(JsonObject update)
        {
            lock (_gate)
            {
                FlushPendingLocked();
                EnqueueLocked(update);
            }
        }

        private void EnqueueLocked(JsonObject update) =>
            _chain = _chain.ContinueWith(_ => _server.NotifyAsync(_sid, update), TaskScheduler.Default).Unwrap();

        /// <summary>回答 / 思考的片段：和前面同一种就接着攒，换了种类先把攒着的发掉。</summary>
        private void Append(string kind, string text)
        {
            if (text.Length == 0)
            {
                return;
            }
            lock (_gate)
            {
                if (_pendingKind is not null && _pendingKind != kind)
                {
                    FlushPendingLocked();
                }
                _pendingKind = kind;
                _pending.Append(text);
                if (_pending.Length >= MaxBatchChars)
                {
                    FlushPendingLocked();
                }
                else
                {
                    _timer ??= new Timer(_ =>
                    {
                        lock (_gate)
                        {
                            FlushPendingLocked();
                        }
                    }, null, BatchDelay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void FlushPendingLocked()
        {
            _timer?.Dispose();
            _timer = null;
            if (_pendingKind is { } kind && _pending.Length > 0)
            {
                EnqueueLocked(Chunk(kind, _pending.ToString()));
            }
            _pending.Clear();
            _pendingKind = null;
        }

        /// <summary>这一轮的更新都发出去了再回 session/prompt 的结果。</summary>
        public Task FlushAsync()
        {
            lock (_gate)
            {
                FlushPendingLocked();
                return _chain;
            }
        }

        public void OnContent(string delta)
        {
            _streamed = true;
            Append("agent_message_chunk", delta);
        }

        public void OnReasoning(string delta) => Append("agent_thought_chunk", delta);

        public void OnAssistantMessage(ChatMessage message)
        {
            // 正常的回答已经一段段推过了；模型服务出错时的说明不走流式，这里补发
            if (!_streamed && message.Content.Length > 0)
            {
                Push(Chunk("agent_message_chunk", message.Content));
            }
            _streamed = false;
        }

        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision)
        {
            _streamed = false;
            var update = new JsonObject
            {
                ["sessionUpdate"] = "tool_call",
                ["toolCallId"] = call.Id,
                ["title"] = summary,
                ["kind"] = ToolKind(call.Name),
                ["status"] = "in_progress",
                ["rawInput"] = RawInput(call.ArgumentsJson),
            };
            if (PathOf(call) is { } path)
            {
                update["locations"] = new JsonArray(new JsonObject { ["path"] = path });
            }
            Push(update);
        }

        public void OnToolFinished(ToolCall call, ToolResult result, string decision)
        {
            var output = result.Output.Length > MaxOutput ? result.Output[..MaxOutput] + "\n…（只显示前一部分）" : result.Output;
            Push(new JsonObject
            {
                ["sessionUpdate"] = "tool_call_update",
                ["toolCallId"] = call.Id,
                ["status"] = result.Ok ? "completed" : "failed",
                ["content"] = new JsonArray(Text(output)),
            });
        }

        public void OnToolMessage(ChatMessage message)
        {
        }

        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) =>
            Push(new JsonObject
            {
                ["sessionUpdate"] = "plan",
                ["entries"] = new JsonArray(plan.Select(p => (JsonNode)new JsonObject
                {
                    ["content"] = p.Step,
                    ["priority"] = "medium",
                    ["status"] = p.Status is "in_progress" or "completed" ? p.Status : "pending",
                }).ToArray()),
            });

        public void OnNotice(string text) => Push(Chunk("agent_thought_chunk", text));

        private static JsonObject Chunk(string kind, string text) =>
            new() { ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } };

        /// <summary>改文件的工具带上路径，IDE 可以跳过去看。</summary>
        private static string? PathOf(ToolCall call)
        {
            try
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                    && Path.IsPathRooted(p.GetString()) ? p.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
