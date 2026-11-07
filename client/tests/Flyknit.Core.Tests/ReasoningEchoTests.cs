using System.Text.Json.Nodes;
using Flyknit.Core.Chat;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>思考内容回传：只看数据，不按厂商写死。</summary>
public class ReasoningEchoTests
{
    private static ToolCall Call(string id) => new(id, "list_dir", "{}");

    private static JsonObject At(JsonArray messages, int i) => messages[i]!.AsObject();

    [Fact]
    public void ModelWithoutReasoningGetsNoExtraField()
    {
        var messages = OpenAiSerializer.ToMessages(new[]
        {
            ChatMessage.User("查一下"),
            ChatMessage.Assistant("", new[] { Call("c1") }),
            ChatMessage.ToolResult(Call("c1"), "a.txt"),
        });
        Assert.False(At(messages, 1).ContainsKey("reasoning_content"));
    }

    [Fact]
    public void ToolCallTurnsCarryReasoningBack()
    {
        var messages = OpenAiSerializer.ToMessages(new[]
        {
            ChatMessage.User("查一下"),
            ChatMessage.Assistant("", new[] { Call("c1") }, "先列目录"),
            ChatMessage.ToolResult(Call("c1"), "a.txt"),
            // 这一步模型没给思考：有的厂商缺字段就 400，填空字符串
            ChatMessage.Assistant("", new[] { Call("c2") }),
            ChatMessage.ToolResult(Call("c2"), "b.txt"),
        });
        Assert.Equal("先列目录", At(messages, 1)["reasoning_content"]!.GetValue<string>());
        Assert.Equal("", At(messages, 3)["reasoning_content"]!.GetValue<string>());
        Assert.False(At(messages, 2).ContainsKey("reasoning_content"));
    }

    [Fact]
    public void FinalAnswerReasoningIsNotSentBack()
    {
        var messages = OpenAiSerializer.ToMessages(new[]
        {
            ChatMessage.User("你好"),
            ChatMessage.Assistant("你好呀", null, "用户在打招呼"),
            ChatMessage.User("再见"),
        });
        Assert.False(At(messages, 1).ContainsKey("reasoning_content"));
    }
}
