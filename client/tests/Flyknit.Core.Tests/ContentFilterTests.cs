using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

public class ContentFilterTests
{
    [Theory]
    [InlineData("Output data may contain inappropriate content.")]
    [InlineData("Input data may contain inappropriate content.")]
    [InlineData("DataInspectionFailed: the request is blocked")]
    [InlineData("content_filter triggered")]
    [InlineData("命中内容审核，请调整后重试")]
    public void ModerationErrorsAreRecognised(string message)
    {
        Assert.True(ContentFilter.IsBlocked(new GatewayException(message)));
    }

    [Theory]
    [InlineData("HTTP 500")]
    [InlineData("无法连接 Flyknit 服务器：超时")]
    [InlineData("rate limit exceeded")]
    public void OtherErrorsAreNotTreatedAsModeration(string message)
    {
        Assert.False(ContentFilter.IsBlocked(new GatewayException(message)));
    }

    [Fact]
    public void RedactReplacesTheMostRecentLargeToolOutput()
    {
        var call1 = new ToolCall("c1", "read_file", "{}");
        var call2 = new ToolCall("c2", "read_file", "{}");
        var history = new List<ChatMessage>
        {
            ChatMessage.System("系统"),
            ChatMessage.User("读一下这两个文件"),
            ChatMessage.ToolResult(call1, new string('旧', 500)),
            ChatMessage.ToolResult(call2, new string('新', 500)),
        };

        Assert.True(ContentFilter.Redact(history));
        Assert.Equal(ContentFilter.Placeholder, history[3].Content);   // 先换最近的那条
        Assert.StartsWith("旧", history[2].Content);                    // 早一条先留着

        Assert.True(ContentFilter.Redact(history));
        Assert.Equal(ContentFilter.Placeholder, history[2].Content);

        // 都换完了就没得换了，再重试也是同样结果
        Assert.False(ContentFilter.Redact(history));
    }

    [Fact]
    public void RedactKeepsTheToolCallIdSoTheNextRequestStaysValid()
    {
        var call = new ToolCall("call_abc", "read_file", "{}");
        var history = new List<ChatMessage> { ChatMessage.ToolResult(call, new string('x', 400)) };

        ContentFilter.Redact(history);

        // tool 消息必须对得上之前的 tool_call_id，否则下一轮请求会被模型服务判为非法
        Assert.Equal("call_abc", history[0].ToolCallId);
        Assert.Equal("read_file", history[0].ToolName);
        Assert.Equal(ChatRole.Tool, history[0].Role);
    }

    [Fact]
    public void ShortOutputsAndAlreadyRedactedOnesAreSkipped()
    {
        var call = new ToolCall("c", "list_dir", "{}");
        var history = new List<ChatMessage>
        {
            ChatMessage.ToolResult(call, "只有两行内容"),                 // 太短，换了也没用
            ChatMessage.ToolResult(call, ContentFilter.Placeholder),     // 已经换过
        };
        Assert.False(ContentFilter.Redact(history));
    }

    [Fact]
    public void TheExplanationTellsTheUserWhatToDoNotJustTheEnglishError()
    {
        var text = ContentFilter.Explain(new GatewayException("Output data may contain inappropriate content."));
        Assert.Contains("内容审核", text);
        Assert.Contains("换一个模型", text);
        Assert.Contains("Output data may contain inappropriate content.", text); // 原文也留着，便于排查

        Assert.Contains("授权", ContentFilter.Explain(new GatewayException("unauthorized", 401)));
        Assert.Contains("额度", ContentFilter.Explain(new GatewayException("too many requests", 429)));
        Assert.Contains("临时故障", ContentFilter.Explain(new GatewayException("boom", 503)));
    }
}
