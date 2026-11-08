using System.Text.Json;
using Flyknit.Core.Git;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>聊天里 PR / Issue 链接的悬停卡片：认链接、解析 gh 的结果、缓存。</summary>
public sealed class RefCardTests
{
    [Theory]
    [InlineData("https://github.com/skyteam168/flyknit-agent/pull/100", "pr", 100)]
    [InlineData("https://github.com/skyteam168/flyknit-agent/pull/99/files", "pr", 99)]
    [InlineData("https://github.com/skyteam168/flyknit-agent/issues/7#issuecomment-1", "issue", 7)]
    [InlineData("https://GitHub.com/a/b.c/pull/1?x=1", "pr", 1)]
    public void ParsesPullRequestAndIssueLinks(string url, string kind, int number)
    {
        var link = RefLink.Parse(url);
        Assert.NotNull(link);
        Assert.Equal(kind, link!.Kind);
        Assert.Equal(number, link.Number);
        Assert.Equal("github.com", link.Host);
    }

    [Theory]
    [InlineData("http://github.com/a/b/pull/1")]
    [InlineData("https://github.com/a/b")]
    [InlineData("https://github.com/a/b/pull/0")]
    [InlineData("https://github.com/a/b/pull/abc")]
    [InlineData("https://github.com/a/b/commit/1")]
    [InlineData("https://github.com/a/b/pull/1 && calc")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsAnythingElse(string? url) => Assert.Null(RefLink.Parse(url));

    [Fact]
    public void CanonicalUrlDropsExtraPath()
    {
        var link = RefLink.Parse("https://github.com/o/r/pull/5/files?diff=split")!;
        Assert.Equal("https://github.com/o/r/pull/5", link.Url);
        Assert.Equal("https://github.com/o/r/issues/6", RefLink.Parse("https://github.com/o/r/issues/6")!.Url);
    }

    [Fact]
    public void ParsesMergedPullRequest()
    {
        var link = RefLink.Parse("https://github.com/skyteam168/flyknit-agent/pull/99")!;
        using var doc = JsonDocument.Parse("""
            {"number":99,"title":"按部门隔离","url":"https://github.com/skyteam168/flyknit-agent/pull/99","state":"MERGED","isDraft":false,
             "author":{"login":"skyteam168"},"additions":1995,"deletions":245,"changedFiles":114,
             "createdAt":"2026-11-07T01:00:00Z","mergedAt":"2026-11-07T09:00:00Z","closedAt":"2026-11-07T09:00:00Z"}
            """);
        var card = RefCard.Parse(link, doc.RootElement);
        Assert.Equal("merged", card.State);
        Assert.Equal("skyteam168/flyknit-agent", card.Repo);
        Assert.Equal(99, card.Number);
        Assert.Equal("skyteam168", card.Author);
        Assert.Equal(1995, card.Additions);
        Assert.Equal(245, card.Deletions);
        Assert.Equal(114, card.Files);
        Assert.Equal("2026-11-07T09:00:00Z", card.At);
    }

    [Fact]
    public void OpenDraftUsesCreatedAt()
    {
        var link = RefLink.Parse("https://github.com/o/r/pull/3")!;
        using var doc = JsonDocument.Parse("""{"number":3,"state":"OPEN","isDraft":true,"createdAt":"2026-11-08T01:00:00Z","closedAt":null}""");
        var card = RefCard.Parse(link, doc.RootElement);
        Assert.Equal("draft", card.State);
        Assert.Equal("2026-11-08T01:00:00Z", card.At);
        Assert.Equal("https://github.com/o/r/pull/3", card.Url);
    }

    [Fact]
    public void ParsesClosedIssueWithComments()
    {
        var link = RefLink.Parse("https://github.com/o/r/issues/8")!;
        using var doc = JsonDocument.Parse("""
            {"number":8,"title":"t","state":"CLOSED","author":{"login":"x"},"comments":[{},{}],
             "createdAt":"2026-11-01T00:00:00Z","closedAt":"2026-11-02T00:00:00Z"}
            """);
        var card = RefCard.Parse(link, doc.RootElement);
        Assert.Equal("issue", card.Kind);
        Assert.Equal("closed", card.State);
        Assert.Equal(2, card.Comments);
        Assert.Equal("2026-11-02T00:00:00Z", card.At);
    }

    [Fact]
    public async Task CacheQueriesOncePerLinkAndExpires()
    {
        var now = DateTimeOffset.Parse("2026-11-08T00:00:00Z");
        var calls = 0;
        var cache = new RefCardCache((l, _) =>
        {
            calls++;
            return Task.FromResult<RefCard?>(new RefCard { Number = l.Number });
        }, () => now);
        var link = RefLink.Parse("https://github.com/o/r/pull/1")!;
        await cache.GetAsync(link, default);
        // 同一个 PR 换个子页面地址也算同一个
        await cache.GetAsync(RefLink.Parse("https://github.com/o/r/pull/1/files")!, default);
        Assert.Equal(1, calls);
        now += RefCardCache.Fresh + TimeSpan.FromSeconds(1);
        await cache.GetAsync(link, default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailuresBecomeNoCard()
    {
        var cache = new RefCardCache((_, _) => throw new GhUnavailableException(GhCli.MissingMessage));
        Assert.Null(await cache.GetAsync(RefLink.Parse("https://github.com/o/r/pull/1")!, default));
    }
}
