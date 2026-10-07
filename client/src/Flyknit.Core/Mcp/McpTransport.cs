using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flyknit.Core.Mcp;

/// <summary>MCP 连接出错。消息直接给用户看，所以写人话。</summary>
public class McpException : Exception
{
    public McpException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>对方要登录（HTTP 401）。WwwAuthenticate 里可能带着 OAuth 元数据的地址。</summary>
public sealed class McpAuthRequiredException : McpException
{
    public string? WwwAuthenticate { get; }

    public McpAuthRequiredException(string message, string? wwwAuthenticate) : base(message)
    {
        WwwAuthenticate = wwwAuthenticate;
    }
}

/// <summary>JSON-RPC 错误（对方明确回了 error）。</summary>
public sealed class McpRpcException : McpException
{
    public int Code { get; }

    public McpRpcException(int code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>MCP 的传输层：发请求、等回应、收通知。三种连接方式共用请求和回应的配对逻辑。</summary>
public abstract class McpTransport : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;

    /// <summary>对方发来的通知（method, params），例如 notifications/tools/list_changed。</summary>
    public event Action<string, JsonNode?>? Notification;

    /// <summary>连接断了（进程退出、流关闭）。参数是原因。</summary>
    public event Action<string>? Closed;

    public virtual Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>initialize 之后告诉传输层协商出的协议版本（HTTP 要在之后每个请求头里带上）。</summary>
    public virtual void SetProtocolVersion(string version) { }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await SendAsync(message, id, cts.Token);
            using (cts.Token.Register(() => tcs.TrySetCanceled(cts.Token)))
            {
                try
                {
                    return await tcs.Task;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 告诉对方别做了，免得它还在后台跑
                    _ = NotifySafeAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "timeout" });
                    throw new McpException($"{method} 超时（{timeout.TotalSeconds:0} 秒没有回应）");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"{method} 超时（{timeout.TotalSeconds:0} 秒没有回应）");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }
        return SendAsync(message, null, ct);
    }

    private async Task NotifySafeAsync(string method, JsonNode parameters)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await NotifyAsync(method, parameters, cts.Token);
        }
        catch (Exception)
        {
            // 尽力而为
        }
    }

    /// <param name="requestId">请求的 id；通知和回应为 null。</param>
    protected abstract Task SendAsync(JsonObject message, long? requestId, CancellationToken ct);

    /// <summary>收到一条消息：回应交给等着的请求，请求和通知交给上层。</summary>
    protected void Dispatch(JsonNode? message)
    {
        if (message is JsonArray batch)
        {
            foreach (var item in batch)
            {
                Dispatch(item);
            }
            return;
        }
        if (message is not JsonObject obj)
        {
            return;
        }
        var method = obj["method"]?.GetValue<string>();
        var idNode = obj["id"];
        if (method is null)
        {
            // 回应
            if (idNode is null || !TryGetId(idNode, out var id) || !_pending.TryGetValue(id, out var tcs))
            {
                return;
            }
            if (obj["error"] is JsonObject error)
            {
                var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
                var text = error["message"]?.ToString() ?? "未知错误";
                tcs.TrySetException(new McpRpcException(code, text));
            }
            else
            {
                tcs.TrySetResult(obj["result"]?.DeepClone());
            }
            return;
        }
        if (idNode is not null)
        {
            // 对方发来的请求。我们只认 ping；其余（采样、征询）一律回「不支持」，对方会自己降级
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone() };
            if (method == "ping")
            {
                reply["result"] = new JsonObject();
            }
            else
            {
                reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"FlyknitBuddy 不支持 {method}" };
            }
            _ = SendReplySafeAsync(reply);
            return;
        }
        Notification?.Invoke(method, obj["params"]?.DeepClone());
    }

    private async Task SendReplySafeAsync(JsonObject reply)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await SendAsync(reply, null, cts.Token);
        }
        catch (Exception)
        {
            // 对方断了就算了
        }
    }

    private static bool TryGetId(JsonNode node, out long id)
    {
        id = 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out id))
            {
                return true;
            }
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out id))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>连接没了：所有还在等的请求立刻失败，不要让它们一直等到超时。</summary>
    protected void FailAll(string reason)
    {
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(new McpException(reason));
        }
        Closed?.Invoke(reason);
    }

    public abstract ValueTask DisposeAsync();
}

/// <summary>
/// Streamable HTTP（MCP 2025-03-26 起的标准做法）：每条消息一个 POST，
/// 回应可能是一个 JSON，也可能是一条 SSE 流；initialize 时对方给 Mcp-Session-Id，之后每次都带上。
/// </summary>
public sealed class StreamableHttpTransport : McpTransport
{
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> _headers;
    private string? _sessionId;
    private string? _protocolVersion;

    public StreamableHttpTransport(HttpClient http, Uri url, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> headers)
    {
        _http = http;
        _url = url;
        _headers = headers;
    }

    public override void SetProtocolVersion(string version) => _protocolVersion = version;

    protected override async Task SendAsync(JsonObject message, long? requestId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        await ApplyHeadersAsync(req, ct);

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid) && sid.FirstOrDefault() is { Length: > 0 } session)
            {
                _sessionId = session;
            }
            await EnsureOkAsync(resp, ct);
            if (requestId is null || resp.StatusCode == HttpStatusCode.Accepted)
            {
                return;
            }
            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // 读流直到拿到这次请求的回应；期间夹带的通知和对方请求照常分发
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                await foreach (var data in SseReader.ReadAsync(stream, ct))
                {
                    var node = TryParse(data.Data);
                    Dispatch(node);
                    if (node is JsonObject o && o["method"] is null && o["id"] is JsonValue v && v.TryGetValue<long>(out var got) && got == requestId)
                    {
                        break;
                    }
                }
            }
            else
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                var node = TryParse(text) ?? throw new McpException("对方返回的不是 MCP 消息，地址可能填错了");
                Dispatch(node);
            }
        }
        finally
        {
            resp.Dispose();
        }
    }

    private async Task ApplyHeadersAsync(HttpRequestMessage req, CancellationToken ct)
    {
        foreach (var (key, value) in await _headers(ct))
        {
            req.Headers.TryAddWithoutValidation(key, value);
        }
        if (_sessionId is not null)
        {
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }
        if (_protocolVersion is not null)
        {
            req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        }
    }

    internal static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }
        var code = (int)resp.StatusCode;
        if (code == 401)
        {
            var challenge = resp.Headers.WwwAuthenticate.Count > 0 ? string.Join(", ", resp.Headers.WwwAuthenticate.Select(h => h.ToString())) : null;
            throw new McpAuthRequiredException("需要登录：密钥不对或已过期", challenge);
        }
        if (code == 403)
        {
            throw new McpException("对方拒绝了（HTTP 403）：这个账号没有权限，或者密钥不对");
        }
        var body = "";
        try
        {
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            // 读不到正文就不读了
        }
        body = body.Length > 200 ? body[..200] : body;
        throw new McpHttpStatusException(code, $"HTTP {code}{(body.Length > 0 ? "：" + body : "")}");
    }

    internal static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        // 规范建议客户端主动结束会话；对方不支持（405）也没关系
        if (_sessionId is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var req = new HttpRequestMessage(HttpMethod.Delete, _url);
                await ApplyHeadersAsync(req, cts.Token);
                using var _ = await _http.SendAsync(req, cts.Token);
            }
            catch (Exception)
            {
                // 尽力而为
            }
        }
        FailAll("连接已关闭");
    }
}

/// <summary>HTTP 状态码错误（401/403 以外的）。</summary>
public sealed class McpHttpStatusException : McpException
{
    public int StatusCode { get; }

    public McpHttpStatusException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// 旧版 HTTP+SSE（2024-11-05）：先 GET 一条 SSE 长连接，对方在 endpoint 事件里告诉我们往哪儿 POST，
/// 回应都从那条长连接里回来。不少厂商还停在这一版。
/// </summary>
public sealed class LegacySseTransport : McpTransport
{
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> _headers;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<Uri> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _reader;

    public LegacySseTransport(HttpClient http, Uri url, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> headers)
    {
        _http = http;
        _url = url;
        _headers = headers;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var (key, value) in await _headers(ct))
        {
            req.Headers.TryAddWithoutValidation(key, value);
        }
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            await StreamableHttpTransport.EnsureOkAsync(resp, ct);
        }
        catch
        {
            resp.Dispose();
            req.Dispose();
            throw;
        }
        _reader = Task.Run(() => ReadLoopAsync(req, resp), CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using (timeout.Token.Register(() => _endpoint.TrySetException(new McpException("对方没有告诉我们往哪儿发消息（没有 endpoint 事件）"))))
        {
            await _endpoint.Task;
        }
    }

    private async Task ReadLoopAsync(HttpRequestMessage req, HttpResponseMessage resp)
    {
        var reason = "对方关闭了连接";
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token);
            await foreach (var ev in SseReader.ReadAsync(stream, _cts.Token))
            {
                if (ev.Event == "endpoint")
                {
                    _endpoint.TrySetResult(new Uri(_url, ev.Data.Trim()));
                }
                else
                {
                    Dispatch(StreamableHttpTransport.TryParse(ev.Data));
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "连接已关闭";
        }
        catch (Exception ex)
        {
            reason = $"连接中断：{ex.Message}";
        }
        finally
        {
            resp.Dispose();
            req.Dispose();
            _endpoint.TrySetException(new McpException(reason));
            FailAll(reason);
        }
    }

    protected override async Task SendAsync(JsonObject message, long? requestId, CancellationToken ct)
    {
        var endpoint = await _endpoint.Task;
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var (key, value) in await _headers(ct))
        {
            req.Headers.TryAddWithoutValidation(key, value);
        }
        using var resp = await _http.SendAsync(req, ct);
        await StreamableHttpTransport.EnsureOkAsync(resp, ct);
    }

    public override async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_reader is not null)
        {
            try
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // 关不掉就不等了
            }
        }
        _cts.Dispose();
    }
}

/// <summary>text/event-stream 解析。</summary>
public static class SseReader
{
    public sealed record SseEvent(string Event, string Data);

    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var name = "message";
        var hasData = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }
            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new SseEvent(name, data.ToString());
                }
                data.Clear();
                name = "message";
                hasData = false;
                continue;
            }
            if (line.StartsWith(':'))
            {
                continue; // 注释 / 心跳
            }
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }
            if (field == "data")
            {
                if (hasData)
                {
                    data.Append('\n');
                }
                data.Append(value);
                hasData = true;
            }
            else if (field == "event")
            {
                name = value;
            }
        }
        if (hasData)
        {
            yield return new SseEvent(name, data.ToString());
        }
    }
}
