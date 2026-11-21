using System.Net;
using System.Text;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 服务端数据脱敏的提示（docs/data-protection.md）：服务端不再拦下请求，而是一律换成占位符照常发出，
/// 响应头 X-Flyknit-Masked-Counts 带各类型数量。员工端把它交给界面显示一条提示，回答照常回来、任务不停。
/// 提示在网关客户端统一发（FlyknitServerClient.Masked），专家、子代理、团队成员的请求带所属对话的 id，同样会提示。
/// </summary>
public class PrivacyMaskedHeaderTests
{
    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
    }

    private static (FlyknitServerClient Client, List<(string Conversation, IReadOnlyDictionary<string, int> Counts)> Seen) Client(string? header)
    {
        var client = Make(header);
        var seen = new List<(string, IReadOnlyDictionary<string, int>)>();
        client.Masked += (id, counts) => seen.Add((id, counts));
        return (client, seen);
    }

    private static FlyknitServerClient Make(string? header) =>
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

    private static ChatRequest Request(string conversationId = "conv-1") => new()
    {
        Scene = "agent",
        ConversationId = conversationId,
        Messages = new List<ChatMessage> { ChatMessage.User("发邮件") },
        Stream = true,
    };

    [Fact]
    public async Task MaskedCountsAreAnnouncedAndTheTurnContinues()
    {
        var (client, seen) = Client(Uri.EscapeDataString("{\"email\": 28, \"id_card\": 1, \"phone\": 0}"));
        var turn = await client.CompleteAsync(Request(), null, CancellationToken.None);
        // 回答照常回来（不是「调用模型失败」）
        Assert.Equal("已发给 zhang@corp.cn", turn.Content);
        Assert.Equal(28, turn.Masked["email"]);
        Assert.Equal(1, turn.Masked["id_card"]);
        Assert.False(turn.Masked.ContainsKey("phone")); // 0 处的不提示
        var only = Assert.Single(seen);
        Assert.Equal("conv-1", only.Conversation);
        Assert.Equal(2, only.Counts.Count);
    }

    [Fact]
    public async Task ChildTaskRequestsAreAnnouncedToTheParentConversation()
    {
        // 专家 / 子代理的循环没有界面的流式接收方（sink 为空），请求带的是主对话的 id
        var (client, seen) = Client(Uri.EscapeDataString("{\"bank_card\": 2}"));
        await client.CompleteAsync(Request("parent-conv"), null, CancellationToken.None);
        var only = Assert.Single(seen);
        Assert.Equal("parent-conv", only.Conversation);
        Assert.Equal(2, only.Counts["bank_card"]);
    }

    [Fact]
    public async Task RequestsWithoutAConversationAreNotAnnounced()
    {
        // 记忆整理、标题这类不属于某个对话的请求：数量记在 ChatTurn 上，但没有对话可以显示提示
        var (client, seen) = Client(Uri.EscapeDataString("{\"email\": 1}"));
        var turn = await client.CompleteAsync(Request(""), null, CancellationToken.None);
        Assert.Equal(1, turn.Masked["email"]);
        Assert.Empty(seen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%7Bnot-json")]
    public async Task NoOrBrokenHeaderMeansNoNotice(string? header)
    {
        var (client, seen) = Client(header);
        var turn = await client.CompleteAsync(Request(), null, CancellationToken.None);
        Assert.Equal("已发给 zhang@corp.cn", turn.Content);
        Assert.Empty(turn.Masked);
        Assert.Empty(seen);
    }
}
