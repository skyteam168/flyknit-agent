using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>输入框的「增强提示词」：只改写不回答，按模式和专家补要素，结果去掉多余的包装。</summary>
public class PromptEnhancerTests
{
    private sealed class Gateway : IChatGateway
    {
        private readonly string _reply;
        public ChatRequest? Last { get; private set; }
        public Gateway(string reply) => _reply = reply;

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new ChatTurn { Content = _reply });
        }
    }

    [Fact]
    public async Task ReturnsOnlyTheRewrittenText()
    {
        var gateway = new Gateway("<think>想一想</think>\n改写后：“请把 D:\\日报 下本月的 Excel 汇总成一张表。”");
        var result = await new PromptEnhancer(gateway).EnhanceAsync(
            new PromptEnhanceInput("汇总下日报", ConversationMode.Agent, "zh-CN"), CancellationToken.None);
        Assert.Equal("请把 D:\\日报 下本月的 Excel 汇总成一张表。", result);
        Assert.Null(gateway.Last!.Tools);
        Assert.False(gateway.Last.Stream);
        Assert.Contains("汇总下日报", gateway.Last.Messages[^1].Content);
    }

    [Fact]
    public async Task CodeFencesAreUnwrapped()
    {
        var gateway = new Gateway("```\n1. 打开报表\n2. 按车间汇总\n```");
        var result = await new PromptEnhancer(gateway).EnhanceAsync(
            new PromptEnhanceInput("汇总报表", ConversationMode.Chat, "zh-CN"), CancellationToken.None);
        Assert.Equal("1. 打开报表\n2. 按车间汇总", result);
    }

    [Fact]
    public void PromptFollowsTheModeAndTheExpert()
    {
        var agent = PromptEnhancer.SystemPrompt(new PromptEnhanceInput("x", ConversationMode.Agent, "zh-CN", "MES生产分析专家", "负责：产量分析"));
        Assert.Contains("办事", agent);
        Assert.Contains("MES生产分析专家", agent);
        Assert.Contains("负责：产量分析", agent);
        Assert.Contains("不要回答问题", agent);

        var chat = PromptEnhancer.SystemPrompt(new PromptEnhanceInput("x", ConversationMode.Chat, "vi-VN"));
        Assert.DoesNotContain("专家", chat.Split("规则")[0]);
    }

    [Fact]
    public async Task EmptyOrHugeInputIsRefused()
    {
        var enhancer = new PromptEnhancer(new Gateway("x"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            enhancer.EnhanceAsync(new PromptEnhanceInput("   ", ConversationMode.Agent, "zh-CN"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            enhancer.EnhanceAsync(new PromptEnhanceInput(new string('字', PromptEnhancer.MaxInputChars + 1), ConversationMode.Agent, "zh-CN"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PromptEnhancer(new Gateway("  ")).EnhanceAsync(new PromptEnhanceInput("hi", ConversationMode.Chat, "zh-CN"), CancellationToken.None));
    }
}
