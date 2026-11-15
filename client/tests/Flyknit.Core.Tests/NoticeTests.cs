using System.Text.Json;
using Flyknit.Core.Hosting;
using Flyknit.Core.Notices;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>消息通知（docs/notices.md）：媒体地址改走宿主代理、重要通知只弹一次、老服务端、坏的媒体 ID 不外发。</summary>
public class NoticeTests
{
    private sealed class FakeServer : INoticeService
    {
        public NoticeList? List = new();
        public Notice? Detail;
        public readonly List<string> MediaRequests = new();
        public readonly List<string> Reads = new();

        public Task<NoticeList?> GetNoticesAsync(CancellationToken ct) => Task.FromResult(List);
        public Task<Notice?> GetNoticeAsync(string id, CancellationToken ct) => Task.FromResult(Detail);

        public Task MarkNoticeReadAsync(string id, CancellationToken ct)
        {
            Reads.Add(id);
            if (List is not null)
            {
                foreach (var n in List.Items.Where(n => n.Id == id))
                {
                    n.Read = true;
                }
                List.Unread = List.Items.Count(n => !n.Read);
            }
            return Task.CompletedTask;
        }

        public Task MarkAllNoticesReadAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<NoticeMediaResponse> OpenNoticeMediaAsync(string mediaId, string? range, CancellationToken ct)
        {
            MediaRequests.Add(mediaId + "|" + range);
            return Task.FromResult(new NoticeMediaResponse(200, "image/png", new MemoryStream(new byte[] { 1 }), 1, null));
        }
    }

    private static BridgeArgs Args(object value) => new(JsonSerializer.SerializeToElement(value));

    private static string Json(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public async Task DetailRewritesServerMediaToTheHostProxyOnly()
    {
        var id = new string('a', 32);
        var server = new FakeServer
        {
            Detail = new Notice
            {
                Id = "nt_1",
                BodyHtml = $"<p><img src=\"/api/v1/notices/media/{id}\"><video src=\"/api/v1/notices/media/{id}\" poster=\"/api/v1/notices/media/{id}\" controls></video>"
                    + "<img src=\"https://other.example/x.png\"></p>",
            },
        };
        var feature = new NoticeFeature(server, _ => { }, _ => { });
        var notice = (Notice)(await feature.HandleAsync("notices.get", Args(new { id = "nt_1" }), default))!;
        Assert.Contains($"<img src=\"https://notices.flyknit.local/{id}\">", notice.BodyHtml);
        Assert.Contains($"poster=\"https://notices.flyknit.local/{id}\"", notice.BodyHtml);
        Assert.DoesNotContain("/api/v1/notices/media/", notice.BodyHtml);
        // 不是服务端媒体的地址不会被改成代理地址（服务端也早就清洗掉了）
        Assert.Contains("https://other.example/x.png", notice.BodyHtml);
    }

    [Fact]
    public async Task ImportantUnreadNoticePopsUpOnceAndUnreadIsPushedOnlyWhenChanged()
    {
        var events = new List<string>();
        var server = new FakeServer
        {
            List = new NoticeList
            {
                Unread = 2,
                Items = new() { new Notice { Id = "imp", Important = true }, new Notice { Id = "n2" } },
            },
        };
        var feature = new NoticeFeature(server, e => events.Add(Json(e)), _ => { });
        await feature.RefreshAsync();
        await feature.RefreshAsync();
        Assert.Single(events);
        Assert.Contains("\"popup\":\"imp\"", events[0]);
        Assert.Contains("\"unread\":2", events[0]);

        await feature.HandleAsync("notices.read", Args(new { id = "n2" }), default);
        Assert.Equal(new[] { "n2" }, server.Reads);
        Assert.Equal(2, events.Count);
        Assert.Contains("\"unread\":1", events[1]);
        Assert.Contains("\"popup\":null", events[1]); // 同一条重要通知不再弹
        Assert.Equal(1, feature.Unread);
    }

    [Fact]
    public async Task OldServerWithoutNoticesIsReportedAsUnsupported()
    {
        var feature = new NoticeFeature(new FakeServer { List = null }, _ => { }, _ => { });
        var list = (NoticeList)(await feature.HandleAsync("notices.list", Args(new { }), default))!;
        Assert.Empty(list.Items);
        Assert.False(feature.Supported);
    }

    [Fact]
    public async Task MediaProxyOnlyForwardsWellFormedIds()
    {
        var server = new FakeServer();
        var feature = new NoticeFeature(server, _ => { }, _ => { });
        foreach (var bad in new[] { "", "../../etc/passwd", "ABCDEF0123456789ABCDEF0123456789", new string('a', 31), new string('a', 32) + "/x" })
        {
            var r = await feature.OpenMediaAsync(bad, null, default);
            Assert.Equal(404, r.Status);
        }
        Assert.Empty(server.MediaRequests);
        var ok = await feature.OpenMediaAsync(new string('b', 32), "bytes=0-9", default);
        Assert.Equal(200, ok.Status);
        Assert.Equal(new[] { new string('b', 32) + "|bytes=0-9" }, server.MediaRequests);
    }

    [Fact]
    public async Task RefreshSwallowsServerErrors()
    {
        var warnings = new List<string>();
        var feature = new NoticeFeature(new ThrowingServer(), _ => { }, warnings.Add);
        await feature.RefreshAsync();
        Assert.Single(warnings);
    }

    [Fact]
    public void BridgeMethodsDoNotCollideWithTeams()
    {
        var registry = new BridgeModuleRegistry().Register(new NoticeFeature(new FakeServer(), _ => { }, _ => { }));
        Assert.True(registry.TryGet("notices.list", out _));
        Assert.All(registry.Methods, m => Assert.StartsWith("notices.", m));
    }

    private sealed class ThrowingServer : INoticeService
    {
        public Task<NoticeList?> GetNoticesAsync(CancellationToken ct) => throw new HttpRequestException("连不上");
        public Task<Notice?> GetNoticeAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkNoticeReadAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkAllNoticesReadAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<NoticeMediaResponse> OpenNoticeMediaAsync(string mediaId, string? range, CancellationToken ct) => throw new NotSupportedException();
    }
}
