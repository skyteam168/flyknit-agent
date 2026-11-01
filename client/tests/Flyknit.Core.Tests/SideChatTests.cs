using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>侧聊：把任务对话整理成一段只读记录交给模型，不带工具、不改任务对话。</summary>
public class SideChatTests
{
    [Fact]
    public void TheTaskIsGivenAsAReadOnlyRecordAndTheQuestionComesLast()
    {
        var call = new ToolCall("c1", "list_dir", "{\"path\":\"D:\\\\日报\"}");
        var conversation = new List<ChatMessage>
        {
            ChatMessage.System("任务的系统提示"),
            ChatMessage.User("整理 D 盘的日报"),
            ChatMessage.Assistant("我先看一下目录", new[] { call }, null),
            ChatMessage.ToolResult(call, new string('x', 2000)),
            ChatMessage.Assistant("共有 34 个日报，按月份整理好了", Array.Empty<ToolCall>(), null),
        };
        var side = new List<ChatMessage> { ChatMessage.User("几个月份？"), ChatMessage.Assistant("两个：8 月和 9 月", Array.Empty<ToolCall>(), null) };

        var messages = SideChat.BuildMessages(conversation, side, "  文件放在哪了？ ");

        Assert.Equal(ChatRole.System, messages[0].Role);
        var record = messages[0].Content;
        Assert.Contains("员工：整理 D 盘的日报", record);
        Assert.Contains("AI 调用工具 list_dir", record);
        Assert.Contains("共有 34 个日报", record);
        Assert.DoesNotContain("任务的系统提示", record);             // 任务自己的系统提示不带进来
        Assert.Contains("…（后略）", record);                        // 很长的工具结果截短
        Assert.True(record.Length < 4000);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User }, messages.Select(m => m.Role));
        Assert.Equal("文件放在哪了？", messages[^1].Content);
        Assert.All(messages, m => Assert.Empty(m.ToolCalls));       // 侧聊不调工具
    }

    [Fact]
    public void OnlyTheRecentPartOfALongTaskIsIncluded()
    {
        var conversation = Enumerable.Range(0, 100).Select(i => ChatMessage.User($"第 {i} 句")).ToList();
        var record = SideChat.BuildMessages(conversation, Array.Empty<ChatMessage>(), "?")[0].Content;
        Assert.Contains("更早的部分省略", record);
        Assert.Contains("第 99 句", record);
        Assert.DoesNotContain("第 10 句", record);
    }
}
