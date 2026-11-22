using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flyknit.Core.Lsp;

/// <summary>语言服务回的错误（JSON-RPC error）。</summary>
public sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message) : base(message) => Code = code;
    public int Code { get; }
}

/// <summary>
/// LSP 用的 JSON-RPC：每条消息前面是 <c>Content-Length: N\r\n\r\n</c>，后面是 N 个字节的 UTF-8 JSON。
/// 发请求等回复（按 id 对上）、发通知、处理对方发来的请求和通知。连接断了，所有还在等的请求都失败。
/// </summary>
public sealed class JsonRpcConnection : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private Task? _reader;

    /// <param name="input">读对方的输出（语言服务的标准输出）。</param>
    /// <param name="output">写给对方（语言服务的标准输入）。</param>
    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>对方发来的请求：返回结果（null 也是合法结果）。</summary>
    public Func<string, JsonNode?, Task<JsonNode?>>? OnRequest { get; set; }

    /// <summary>对方发来的通知。</summary>
    public Action<string, JsonNode?>? OnNotification { get; set; }

    /// <summary>连接断了（对方退出、读写出错）。</summary>
    public event Action? Closed;

    public bool IsClosed { get; private set; }

    /// <summary>单条消息最大 64MB（防一条坏消息把内存吃光）。</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        if (IsClosed)
        {
            throw new IOException("语言服务已经退出");
        }
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params?.DeepClone() }, ct).ConfigureAwait(false);
            using var reg = ct.Register(() =>
            {
                if (_pending.TryRemove(id, out var t) && t.TrySetCanceled(ct))
                {
                    // 告诉对方不用算了（LSP 的取消通知）；写失败就算了
                    _ = WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "$/cancelRequest", ["params"] = new JsonObject { ["id"] = id } }, CancellationToken.None);
                }
            });
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? @params, CancellationToken ct = default) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params?.DeepClone() }, ct);

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header, ct).ConfigureAwait(false);
            await _output.WriteAsync(body, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close();
            throw new IOException("语言服务已经退出", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var length = await ReadHeaderAsync().ConfigureAwait(false);
                if (length < 0)
                {
                    break;
                }
                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await _input.ReadAsync(body.AsMemory(read, length - read), _cts.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        return;
                    }
                    read += n;
                }
                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(body);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (message is JsonObject obj)
                {
                    Dispatch(obj);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            Close();
        }
    }

    /// <summary>读消息头，返回 Content-Length；流结束返回 -1。</summary>
    private async Task<int> ReadHeaderAsync()
    {
        var line = new StringBuilder();
        var length = -1;
        var one = new byte[1];
        while (true)
        {
            var n = await _input.ReadAsync(one, _cts.Token).ConfigureAwait(false);
            if (n == 0)
            {
                return -1;
            }
            if (one[0] == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    if (length >= 0)
                    {
                        return length;
                    }
                    continue; // 空行出现在头之前：跳过
                }
                var colon = text.IndexOf(':');
                if (colon > 0 && text[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(text[(colon + 1)..].Trim(), out var len))
                {
                    if (len < 0 || len > MaxMessageBytes)
                    {
                        throw new IOException("语言服务发来的消息太大");
                    }
                    length = len;
                }
                continue;
            }
            if (line.Length > 8192)
            {
                throw new IOException("语言服务发来的消息头不对");
            }
            line.Append((char)one[0]);
        }
    }

    private void Dispatch(JsonObject message)
    {
        var hasId = message.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = message["method"]?.GetValue<string>();
        if (method is null && hasId)
        {
            // 回复
            if (TryId(idNode!, out var id) && _pending.TryRemove(id, out var tcs))
            {
                if (message["error"] is JsonObject error)
                {
                    tcs.TrySetException(new JsonRpcException(error["code"]?.GetValue<int>() ?? 0, error["message"]?.GetValue<string>() ?? "语言服务出错"));
                }
                else
                {
                    tcs.TrySetResult(message["result"]?.DeepClone());
                }
            }
            return;
        }
        if (method is null)
        {
            return;
        }
        var @params = message["params"]?.DeepClone();
        if (!hasId)
        {
            try
            {
                OnNotification?.Invoke(method, @params);
            }
            catch (Exception)
            {
                // 处理通知出错不影响连接
            }
            return;
        }
        var replyId = idNode!.DeepClone();
        _ = Task.Run(async () =>
        {
            JsonObject reply;
            try
            {
                var result = OnRequest is null ? null : await OnRequest(method, @params).ConfigureAwait(false);
                reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = replyId, ["result"] = result };
            }
            catch (Exception ex)
            {
                reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = replyId, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message } };
            }
            try
            {
                await WriteAsync(reply, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        });
    }

    private static bool TryId(JsonNode node, out long id)
    {
        id = 0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out long l))
            {
                id = l;
                return true;
            }
            if (v.TryGetValue(out string? s) && long.TryParse(s, out l))
            {
                id = l;
                return true;
            }
        }
        return false;
    }

    private void Close()
    {
        if (IsClosed)
        {
            return;
        }
        IsClosed = true;
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(new IOException("语言服务已经退出"));
        }
        _pending.Clear();
        try
        {
            Closed?.Invoke();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Close();
        _writeLock.Dispose();
    }
}
