using Flyknit.Core.Agent;
using Flyknit.Core.Agent.SubAgents;
using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>任务预算按真实花费算：缓存命中的输入按一成计；子任务按额度定工作窗口。</summary>
public class BudgetCacheTests
{
    [Fact]
    public void CachedInputCountsAtATenth()
    {
        var usage = new TokenUsage(100_000, 2_000, 90_000);
        Assert.Equal(100_000 - 81_000 + 2_000, usage.BudgetTokens);
        Assert.Equal(102_000, usage.Total); // 原始用量不变（每日额度、统计照旧）
        Assert.Equal(102_000, new TokenUsage(100_000, 2_000).BudgetTokens);
        // 上游报的缓存数比输入还多（不规范）：最多按全部输入算
        Assert.Equal(10_000 + 1_000, new TokenUsage(100_000, 1_000, 500_000).BudgetTokens);
        Assert.Equal(180_000, (usage + new TokenUsage(10, 0, 90_000)).CachedTokens); // 相加时缓存数也累加
    }

    [Fact]
    public void RunBudgetUsesTheDiscountedAmount()
    {
        var budget = new RunBudget { MaxTokens = 200_000 };
        // 同一段 9 万的上下文发了 10 次，后 9 次都命中缓存：按全价是 90 多万，早就「用完」；按实际花费只有十几万
        budget.AddModelCall(new TokenUsage(90_000, 500));
        for (var i = 0; i < 9; i++)
        {
            budget.AddModelCall(new TokenUsage(90_000, 500, 88_000));
        }
        Assert.True(budget.Tokens < 200_000, $"{budget.Tokens}");
        Assert.NotEqual(BudgetVerdict.Stop, budget.Check(out _));
    }

    [Theory]
    [InlineData("{\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":10,\"prompt_tokens_details\":{\"cached_tokens\":800}}}", 800)]
    [InlineData("{\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":10,\"prompt_cache_hit_tokens\":600}}", 600)]
    [InlineData("{\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":10}}", 0)]
    public void StreamUsageReadsCachedTokens(string chunk, int cached)
    {
        var acc = new SseAccumulator();
        acc.FeedChunk(chunk);
        Assert.NotNull(acc.Usage);
        Assert.Equal(1000, acc.Usage!.PromptTokens);
        Assert.Equal(cached, acc.Usage.CachedTokens);
    }

    [Theory]
    [InlineData(400_000, 66_666)]
    [InlineData(60_000, 24_000)]      // 至少 2.4 万
    [InlineData(5_000_000, 131_072)]  // 不超过模型上下文
    public void WorkingWindowScalesWithTheCap(long cap, int window) => Assert.Equal(window, ChildContext.WindowFor(cap));
}
