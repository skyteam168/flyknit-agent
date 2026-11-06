using Flyknit.Core.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>任务授权记录：按授权封顶、后续消息扩大不了授权、本地记录只插入。</summary>
public class TaskGrantTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"flyknit-grants-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_db};Pooling=False");
        c.Open();
        return c;
    }

    [Fact]
    public void LaterMessagesCannotWidenTheGrant()
    {
        var first = TaskGrant.Issue("mobile", "c1", "12", "张工", "整理日报", PermissionMode.Workspace, "D:\\work", null);
        Assert.Equal(PermissionMode.Workspace, first.Permission);
        Assert.Equal(PermissionMode.ReadOnly, first.Cap(PermissionMode.ReadOnly));
        Assert.Equal(PermissionMode.Workspace, first.Cap(PermissionMode.Full));

        // 员工后来把对话改成了完全权限、工作区也换了：下一条远程消息的授权仍然不超过上一份，工作区沿用
        var second = TaskGrant.Issue("mobile", "c1", "13", "张工", "顺便删掉旧文件", PermissionMode.Full, "C:\\", first);
        Assert.Equal(PermissionMode.Workspace, second.Permission);
        Assert.Equal("D:\\work", second.Workspace);

        // 收紧是可以的
        var third = TaskGrant.Issue("mobile", "c1", "14", "张工", "看一下", PermissionMode.ReadOnly, "D:\\work", second);
        Assert.Equal(PermissionMode.ReadOnly, third.Permission);
        Assert.Equal(PermissionMode.ReadOnly, TaskGrant.Issue("mobile", "c1", "15", "", "", PermissionMode.Full, "", third).Permission);

        Assert.Throws<ArgumentException>(() => TaskGrant.Issue("email", "c1", "", "", "", PermissionMode.Full, "", null));
    }

    [Fact]
    public void LocalRecordsAreAppendOnly()
    {
        using (var c = Open())
        {
            TaskGrantStore.Migrate(c);
            TaskGrantStore.Migrate(c);
        }
        var store = new TaskGrantStore(Open);
        var g = TaskGrant.Issue("schedule", "c2", "task-1", "定时任务：清理", "删掉临时文件", PermissionMode.ReadOnly, "D:\\tmp", null);
        store.Add(g);
        var later = TaskGrant.Issue("schedule", "c2", "task-1", "定时任务：清理", "再来一次", PermissionMode.Full, "D:\\tmp", store.Latest("c2"));
        store.Add(later);
        Assert.Equal(later.Id, store.Latest("c2")!.Id);
        Assert.Equal(PermissionMode.ReadOnly, store.Latest("c2")!.Permission);
        Assert.Null(store.Latest("nope"));

        using var conn = Open();
        using var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE task_grants SET permission = 'full'";
        Assert.Throws<SqliteException>(() => upd.ExecuteNonQuery());
        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM task_grants";
        Assert.Throws<SqliteException>(() => del.ExecuteNonQuery());
        Assert.Equal(PermissionMode.ReadOnly, store.Latest("c2")!.Permission);
    }
}
