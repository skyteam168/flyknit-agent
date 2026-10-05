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
        Assert.Equal(2, _store.Get(conv.Id)!.MessageCount);
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
    public void RenameUnknownConversationThrows()
    {
        Assert.Throws<KeyNotFoundException>(() => _store.Rename("nope", "x"));
    }
}
