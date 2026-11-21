using System.Net;
using System.Text;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 服务端数据脱敏的提示（docs/data-protection.md）：服务端不再拦下请求，而是一律换成占位符照常发出，
/// 响应头 X-Flyknit-Masked-Counts 带各类型数量。员工端把它交给界面显示一条提示，回答照常回来、任务不停。
/// </summary>
public class PrivacyMaskedHeaderTests
{
    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
    }

    private sealed class Sink : IStreamSink
    {
        public readonly List<IReadOnlyDictionary<string, int>> Masked = new();
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnMasked(IReadOnlyDictionary<string, int> counts) => Masked.Add(counts);
    }

    private static FlyknitServerClient Client(string? header) =>
        new(new HttpClient(new FakeHandler(() =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"已发给 zhang@corp.cn\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
            if (header is not null)
            {
                resp.Headers.TryAddWithoutValidation("X-Flyknit-Masked-Counts", header);
            }
            return resp;
        })), "http://flyknit.test", "token");

    private static ChatRequest Request() => new()
    {
        Scene = "agent",
        Messages = new List<ChatMessage> { ChatMessage.User("发邮件") },
        Stream = true,
    };

    [Fact]
    public async Task MaskedCountsReachTheSinkAndTheTurnContinues()
    {
        var sink = new Sink();
        var header = Uri.EscapeDataString("{\"email\": 28, \"id_card\": 1, \"phone\": 0}");
        var turn = await Client(header).CompleteAsync(Request(), sink, CancellationToken.None);
        // 回答照常回来（不是「调用模型失败」）
        Assert.Equal("已发给 zhang@corp.cn", turn.Content);
        Assert.Equal(28, turn.Masked["email"]);
        Assert.Equal(1, turn.Masked["id_card"]);
        Assert.False(turn.Masked.ContainsKey("phone")); // 0 处的不提示
        var only = Assert.Single(sink.Masked);
        Assert.Equal(2, only.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%7Bnot-json")]
    public async Task NoOrBrokenHeaderMeansNoNotice(string? header)
    {
        var sink = new Sink();
        var turn = await Client(header).CompleteAsync(Request(), sink, CancellationToken.None);
        Assert.Equal("已发给 zhang@corp.cn", turn.Content);
        Assert.Empty(turn.Masked);
        Assert.Empty(sink.Masked);
    }
}
