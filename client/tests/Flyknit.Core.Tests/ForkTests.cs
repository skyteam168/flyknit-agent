using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>从某条消息分叉出新对话：复制到那条为止，设置照旧，原对话不动；停在工具调用中间时带上结果。</summary>
public sealed class ForkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-fork").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    [Fact]
    public void ForkCopiesUpToTheMessageAndKeepsSettings()
    {
        var store = new ConversationStore(Path.Combine(_dir, "h.db"));
        var conv = store.Create(ConversationMode.Agent, "整理日报", modelId: 3, workspace: @"D:\日报", permission: PermissionMode.Full);
        var call = new ToolCall("c1", "read_file", "{}");
        var q1 = ChatMessage.User("先看看有哪些文件");
        var a1 = new ChatMessage { Role = ChatRole.Assistant, Content = "我看一下", ToolCalls = { call } };
        var t1 = ChatMessage.ToolResult(call, "34 个文件");
        var a2 = ChatMessage.Assistant("共 34 个。");
        var q2 = ChatMessage.User("按月份分文件夹");
        store.AddMessages(conv.Id, new[] { q1, a1, t1, a2, q2 });

        var fork = store.Fork(conv.Id, a1.Id, "整理日报（分叉）");

        Assert.NotNull(fork);
        Assert.NotEqual(conv.Id, fork!.Id);
        Assert.Equal("整理日报（分叉）", fork.Title);
        Assert.Equal(3, fork.ModelId);
        Assert.Equal(@"D:\日报", fork.Workspace);
        Assert.Equal(PermissionMode.Full, fork.Permission);
        var copied = store.GetMessages(fork.Id);
        Assert.Equal(new[] { "先看看有哪些文件", "我看一下", "34 个文件" }, copied.Select(m => m.Content)); // 调用的结果一起带上
        Assert.DoesNotContain(copied, m => m.Id == a1.Id);                 // 新的消息 ID
        Assert.Equal(5, store.GetMessages(conv.Id).Count);                  // 原对话不动
        Assert.Null(store.Fork(conv.Id, "nope", "x"));
        Assert.Null(store.Fork("nope", a1.Id, "x"));
    }
}
