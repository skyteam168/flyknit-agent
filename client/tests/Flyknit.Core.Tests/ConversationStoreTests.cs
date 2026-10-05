using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Storage;
using Xunit;

namespace Flyknit.Core.Tests;

public class ConversationStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-db").FullName;
    private readonly ConversationStore _store;

    public ConversationStoreTests()
    {
        _store = new ConversationStore(Path.Combine(_dir, "history.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void SavesAndLoadsMessagesInOrder()
    {
        var conv = _store.Create(ConversationMode.Agent);
        var call = new ToolCall("call_1", "read_file", "{\"path\":\"a.txt\"}");
        _store.AddMessages(conv.Id, new[]
        {
            ChatMessage.System("不会被保存"),
            ChatMessage.User("读文件", new[] { new Attachment { FileName = "a.txt", LocalPath = "D:\\a.txt", Mime = "text/plain", Size = 3 } }),
            ChatMessage.Assistant("", new[] { call }),
            ChatMessage.ToolResult(call, "abc"),
            ChatMessage.Assistant("内容是 abc"),
        });

        var messages = _store.GetMessages(conv.Id);
        Assert.Equal(4, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("a.txt", messages[0].Attachments[0].FileName);
        Assert.Equal("read_file", messages[1].ToolCalls[0].Name);
        Assert.Equal("call_1", messages[2].ToolCallId);
        Assert.Equal(3, _store.Get(conv.Id)!.MessageCount); // 用户 1 条 + 助手 2 条（含工具调用那条），不计 tool 消息
    }

    [Fact]
    public void ManualTitleIsNotOverwrittenByAutoTitle()
    {
        var conv = _store.Create(ConversationMode.Chat);
        Assert.True(_store.SetAutoTitle(conv.Id, "自动标题"));
        _store.Rename(conv.Id, "我的标题");
        Assert.False(_store.SetAutoTitle(conv.Id, "新的自动标题"));
        Assert.Equal("我的标题", _store.Get(conv.Id)!.Title);
    }

    [Fact]
    public void DeleteMovesToTrashAndRestoreBringsBack()
    {
        var a = _store.Create(ConversationMode.Chat, "A");
        var b = _store.Create(ConversationMode.Chat, "B");
        _store.Delete(a.Id);
        Assert.Equal(new[] { "B" }, _store.List().Select(c => c.Title));
        Assert.Equal(new[] { "A" }, _store.List(trash: true).Select(c => c.Title));
        _store.Restore(a.Id);
        Assert.Equal(2, _store.List().Count);
        _store.Purge(b.Id);
        Assert.Null(_store.Get(b.Id));
    }

    [Fact]
    public void PinnedFirstThenMostRecent()
    {
        var a = _store.Create(ConversationMode.Chat, "A");
        var b = _store.Create(ConversationMode.Chat, "B");
        _store.AddMessages(a.Id, new[] { ChatMessage.User("hi") });
        Assert.Equal("A", _store.List()[0].Title);
        _store.SetPinned(b.Id, true);
        Assert.Equal("B", _store.List()[0].Title);
    }

    [Fact]
    public void SearchMatchesTitleAndContent()
    {
        var a = _store.Create(ConversationMode.Chat, "越南语翻译");
        var b = _store.Create(ConversationMode.Chat, "其他");
        _store.AddMessages(b.Id, new[] { ChatMessage.User("帮我分析 100% 的良品率") });
        Assert.Equal(a.Id, Assert.Single(_store.List("越南")).Id);
        Assert.Equal(b.Id, Assert.Single(_store.List("100%")).Id);
    }

    [Fact]
    public void ModelSelectionIsStoredPerConversation()
    {
        var a = _store.Create(ConversationMode.Agent, "", modelId: 5);
        Assert.Equal(5, _store.Get(a.Id)!.ModelId);
        _store.SetModel(a.Id, null);
        Assert.Null(_store.Get(a.Id)!.ModelId);
        // 重新打开数据库（迁移可重复执行）
        var again = new ConversationStore(Path.Combine(_dir, "history.db"));
        Assert.Single(again.List());
    }

    [Fact]
    public void WorkspaceAndPermissionArePerConversation()
    {
        var a = _store.Create(ConversationMode.Agent, "", null, "D:\\agentwork", Flyknit.Core.Security.PermissionMode.ReadOnly);
        var loaded = _store.Get(a.Id)!;
        Assert.Equal("D:\\agentwork", loaded.Workspace);
        Assert.Equal(Flyknit.Core.Security.PermissionMode.ReadOnly, loaded.Permission);
        _store.SetWorkspace(a.Id, "E:\\work");
        _store.SetPermission(a.Id, Flyknit.Core.Security.PermissionMode.Full);
        loaded = _store.Get(a.Id)!;
        Assert.Equal("E:\\work", loaded.Workspace);
        Assert.Equal(Flyknit.Core.Security.PermissionMode.Full, loaded.Permission);
    }

    [Fact]
    public void FeedbackAndTruncationForRegenerateAndEdit()
    {
        var conv = _store.Create(ConversationMode.Chat);
        var q1 = ChatMessage.User("问题一");
        var a1 = ChatMessage.Assistant("回答一");
        var q2 = ChatMessage.User("问题二");
        var a2 = ChatMessage.Assistant("回答二");
        _store.AddMessages(conv.Id, new[] { q1, a1, q2, a2 });

        _store.SetFeedback(a1.Id, 1);
        _store.SetFeedback(a2.Id, -5);
        var messages = _store.GetMessages(conv.Id);
        Assert.Equal(1, messages[1].Feedback);
        Assert.Equal(-1, messages[3].Feedback);
        Assert.Null(messages[0].Feedback);

        // 重新生成：删除最后一个问题之后的回答
        Assert.Equal(1, _store.DeleteMessagesFrom(conv.Id, q2.Id, inclusive: false));
        Assert.Equal(3, _store.GetMessages(conv.Id).Count);

        // 编辑第一个问题：连同它及之后的消息一起删除
        Assert.Equal(3, _store.DeleteMessagesFrom(conv.Id, q1.Id, inclusive: true));
        Assert.Empty(_store.GetMessages(conv.Id));
        Assert.Equal(-1, _store.DeleteMessagesFrom(conv.Id, "missing", inclusive: true));

        // 删除后继续追加，顺序正确
        _store.AddMessages(conv.Id, new[] { ChatMessage.User("新问题") });
        Assert.Equal("新问题", Assert.Single(_store.GetMessages(conv.Id)).Content);
    }

    [Fact]
    public void SummaryAndUsageArePersisted()
    {
        var conv = _store.Create(ConversationMode.Agent);
        var q = ChatMessage.User("问题");
        var a = ChatMessage.Assistant("回答");
        a.ModelName = "qwen3.8-max";
        a.PromptTokens = 1200;
        a.CompletionTokens = 80;
        _store.AddMessages(conv.Id, new[] { q, a });

        var loaded = _store.GetMessages(conv.Id)[1];
        Assert.Equal("qwen3.8-max", loaded.ModelName);
        Assert.Equal(1200, loaded.PromptTokens);
        Assert.Equal(80, loaded.CompletionTokens);

        _store.SetSummary(conv.Id, "## 目标\n整理日报", a.Id);
        var c = _store.Get(conv.Id)!;
        Assert.Equal(a.Id, c.SummaryUpto);
        Assert.Contains("整理日报", c.Summary);

        // 编辑第一个问题后，摘要覆盖的消息被删除，摘要随之作废
        _store.DeleteMessagesFrom(conv.Id, q.Id, inclusive: true);
        Assert.Null(_store.Get(conv.Id)!.Summary);
    }

    [Fact]
    public void RenameUnknownConversationThrows()
    {
        Assert.Throws<KeyNotFoundException>(() => _store.Rename("nope", "x"));
    }
}
