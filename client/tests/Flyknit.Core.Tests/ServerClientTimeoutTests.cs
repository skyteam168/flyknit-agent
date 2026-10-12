using System.Net;
using System.Text;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>模型网关的超时、断流、截断和非 JSON 错误体。</summary>
public class ServerClientTimeoutTests
{
    /// <summary>把几段 SSE 文本依次吐出来，吐完以后按 <see cref="End"/> 决定：正常结束、一直不动、或者抛 IO 错误。</summary>
    private sealed class ScriptedStream : Stream
    {
        public enum End { Close, Hang, Break }

        private readonly Queue<byte[]> _chunks;
        private readonly End _end;
        private byte[] _current = Array.Empty<byte>();
        private int _offset;

        public ScriptedStream(IEnumerable<string> chunks, End end)
        {
            _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));
            _end = end;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
        {
            if (_offset >= _current.Length)
            {
                if (_chunks.Count == 0)
                {
                    switch (_end)
                    {
                        case End.Hang:
                            await Task.Delay(Timeout.Infinite, ct);
                            return 0;
                        case End.Break:
                            throw new IOException("connection reset by peer");
                        default:
                            return 0;
                    }
                }
                _current = _chunks.Dequeue();
                _offset = 0;
            }
            var n = Math.Min(destination.Length, _current.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(destination);
            _offset += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _respond(ct);
    }

    private static FlyknitServerClient Client(Func<CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new FakeHandler(respond)), "http://flyknit.test", "token")
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
            StreamIdleTimeout = TimeSpan.FromMilliseconds(300),
            RequestTimeout = TimeSpan.FromMilliseconds(300),
        };

    private static FlyknitServerClient Streaming(ScriptedStream.End end, params string[] chunks) =>
        Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ScriptedStream(chunks, end)),
        }));

    private static ChatRequest Request(bool stream = true) => new()
    {
        Scene = "agent",
        Messages = new List<ChatMessage> { ChatMessage.User("你好") },
        Stream = stream,
    };

    private const string Hello = "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n";
    private const string Stop = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";

    [Fact]
    public async Task CompleteStreamWithDoneSucceeds()
    {
        var turn = await Streaming(ScriptedStream.End.Close, Hello, Stop, "data: [DONE]\n\n").CompleteAsync(Request(), null, CancellationToken.None);
        Assert.Equal("你好", turn.Content);
        Assert.Equal("stop", turn.FinishReason);
    }

    [Fact]
    public async Task FinishReasonWithoutDoneIsStillComplete()
    {
        var turn = await Streaming(ScriptedStream.End.Close, Hello, Stop).CompleteAsync(Request(), null, CancellationToken.None);
        Assert.Equal("你好", turn.Content);
    }

    [Fact]
    public async Task SilentConnectionHitsIdleTimeout()
    {
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            Streaming(ScriptedStream.End.Hang, Hello).CompleteAsync(Request(), null, CancellationToken.None));
        Assert.Equal("连接模型服务超时，请检查网络后重试", ex.Message);
        Assert.True(ex.IsNetworkError);
    }

    [Fact]
    public async Task UserCancelStaysCancellation()
    {
        using var cts = new CancellationTokenSource();
        var client = Streaming(ScriptedStream.End.Hang, Hello);
        client.StreamIdleTimeout = TimeSpan.FromSeconds(30);
        cts.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync(Request(), null, cts.Token));
    }

    [Fact]
    public async Task NoResponseHeadersHitsResponseTimeout()
    {
        var client = Client(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.CompleteAsync(Request(), null, CancellationToken.None));
        Assert.Equal("连接模型服务超时，请检查网络后重试", ex.Message);
    }

    [Fact]
    public async Task NonStreamingCallsAlsoTimeOut()
    {
        var client = Client(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.GetConfigAsync(CancellationToken.None));
        Assert.True(ex.IsNetworkError);
    }

    [Fact]
    public async Task BrokenStreamBecomesGatewayException()
    {
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            Streaming(ScriptedStream.End.Break, Hello).CompleteAsync(Request(), null, CancellationToken.None));
        Assert.Equal("与服务器的连接中断了，请重试", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task GarbledChunkBecomesGatewayException()
    {
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            Streaming(ScriptedStream.End.Close, Hello, "data: {\"choices\":[{\"delta\n\n").CompleteAsync(Request(), null, CancellationToken.None));
        Assert.Equal("与服务器的连接中断了，请重试", ex.Message);
    }

    [Fact]
    public async Task StreamEndingEarlyIsNotTreatedAsComplete()
    {
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            Streaming(ScriptedStream.End.Close, Hello).CompleteAsync(Request(), null, CancellationToken.None));
        Assert.Equal("回答被中断了，请重试", ex.Message);
    }

    [Fact]
    public async Task ModelErrorChunkKeepsItsMessage()
    {
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            Streaming(ScriptedStream.End.Close, "data: {\"error\":{\"message\":\"Output data may contain inappropriate content.\"}}\n\n")
                .CompleteAsync(Request(), null, CancellationToken.None));
        Assert.Contains("inappropriate content", ex.Message);
    }

    [Fact]
    public async Task HtmlErrorPageGivesShortMessage()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><head><title>502 Bad Gateway</title></head><body><center><h1>502 Bad Gateway</h1></center><hr><center>nginx</center></body></html>", Encoding.UTF8, "text/html"),
        }));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.CompleteAsync(Request(), null, CancellationToken.None));
        Assert.Equal(502, ex.StatusCode);
        Assert.DoesNotContain("<", ex.Message);
        Assert.Contains("502", ex.Message);
        Assert.True(ex.Message.Length < 40);
    }

    [Fact]
    public async Task JsonErrorBodyStillUsesServerMessage()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"detail\":\"今日额度已用完\"}", Encoding.UTF8, "application/json"),
        }));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.CompleteAsync(Request(), null, CancellationToken.None));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("今日额度已用完", ex.Message);
    }

    [Fact]
    public async Task ConnectionRefusedIsMarkedAsNetworkError()
    {
        var client = Client(_ => throw new HttpRequestException("No connection could be made"));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.CompleteAsync(Request(), null, CancellationToken.None));
        Assert.True(ex.IsNetworkError);
    }
}
