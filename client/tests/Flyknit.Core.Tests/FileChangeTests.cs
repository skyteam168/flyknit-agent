using System.Text.Json;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 每一轮任务对文件的改动：工具改文件时记下来（新建 / 修改 / 删除，改之前的备份），任务结束后能一键退回到任务之前。
/// </summary>
public class FileChangeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flyknit-changes").FullName;
    private readonly string _work;
    private readonly FileBackup _backup;
    private readonly List<FileChange> _changes = new();

    public FileChangeTests()
    {
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_work);
        _backup = new FileBackup(Path.Combine(_root, "backups"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private ToolContext Ctx() => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _work,
        Permission = PermissionMode.Workspace,
        Backup = _backup,
        RecordChange = _changes.Add,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.Sandbox] = new() { Value = false, Locked = true },
        }),
    };

    private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();

    private string P(string name) => Path.Combine(_work, name);

    [Fact]
    public async Task WritingAndDeletingAreRecordedWithWhatWasThereBefore()
    {
        File.WriteAllText(P("report.txt"), "九月原稿");
        File.WriteAllText(P("old.csv"), "a,b\n1,2");
        var ctx = Ctx();

        await new WriteFileTool().ExecuteAsync(Args(new { path = "new.md", content = "# 新文件" }), ctx, default);
        await new WriteFileTool().ExecuteAsync(Args(new { path = "report.txt", content = "十月改稿" }), ctx, default);
        await new DeletePathTool().ExecuteAsync(Args(new { path = "old.csv" }), ctx, default);

        Assert.Equal(new[] { FileChange.Created, FileChange.Modified, FileChange.Deleted }, _changes.Select(c => c.Kind));
        Assert.All(_changes, c => Assert.True(c.Restorable));
        Assert.Equal("九月原稿", File.ReadAllText(_changes[1].BackupPath!));
        Assert.Equal("a,b\n1,2", File.ReadAllText(_changes[2].BackupPath!));
    }

    [Fact]
    public async Task RevertingPutsEverythingBackTheWayItWasBeforeTheTask()
    {
        File.WriteAllText(P("report.txt"), "九月原稿");
        File.WriteAllText(P("old.csv"), "a,b\n1,2");
        var ctx = Ctx();
        await new WriteFileTool().ExecuteAsync(Args(new { path = "new.md", content = "# 新文件" }), ctx, default);
        await new WriteFileTool().ExecuteAsync(Args(new { path = "report.txt", content = "十月改稿" }), ctx, default);
        await new WriteFileTool().ExecuteAsync(Args(new { path = "report.txt", content = "十月改稿第二版" }), ctx, default);
        await new DeletePathTool().ExecuteAsync(Args(new { path = "old.csv" }), ctx, default);

        var result = ChangeReverter.Revert(_changes, new PermanentDeleter(), _backup);

        Assert.Empty(result.Skipped);
        Assert.Equal(3, result.Reverted.Count);                         // 同一个文件改两次只还原一次
        Assert.False(File.Exists(P("new.md")));                          // 新建的去掉
        Assert.Equal("九月原稿", File.ReadAllText(P("report.txt")));     // 回到任务之前（不是第一版改稿）
        Assert.Equal("a,b\n1,2", File.ReadAllText(P("old.csv")));        // 删掉的回来了
    }

    [Fact]
    public void OnlyTheChosenFilesAreRevertedAndUnrecoverableOnesAreExplained()
    {
        File.WriteAllText(P("a.txt"), "现在的 a");
        File.WriteAllText(P("b.txt"), "现在的 b");
        var changes = new List<FileChange>
        {
            new(P("a.txt"), FileChange.Created, null, "write_file"),
            new(P("b.txt"), FileChange.Modified, null, "run_shell"),     // 命令改的，没有备份
        };

        var onlyB = ChangeReverter.Revert(changes, new PermanentDeleter(), _backup, p => p.EndsWith("b.txt"));
        Assert.Empty(onlyB.Reverted);
        var skipped = Assert.Single(onlyB.Skipped);
        Assert.Contains("命令", skipped.Reason);
        Assert.True(File.Exists(P("a.txt")));                            // 没选 a，不动

        var all = ChangeReverter.Revert(changes, new PermanentDeleter(), _backup);
        Assert.Single(all.Reverted);
        Assert.False(File.Exists(P("a.txt")));
        Assert.Equal("现在的 b", File.ReadAllText(P("b.txt")));
    }

    [Fact]
    public async Task ACommandThatCreatesFilesIsRecordedToo()
    {
        File.WriteAllText(P("keep.txt"), "x");
        var ctx = Ctx();
        var cmd = OperatingSystem.IsWindows() ? "echo hi > made.txt" : "echo hi > made.txt";
        await new RunShellTool().ExecuteAsync(Args(new { command = cmd }), ctx, default);

        var made = Assert.Single(_changes);
        Assert.Equal(FileChange.Created, made.Kind);
        Assert.Equal("run_shell", made.Source);
        Assert.EndsWith("made.txt", made.Path);
    }
}
