using Flyknit.Core.Setup;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>资源管理器右键菜单：命令行怎么拼、怎么解析，多选文件攒成一批。</summary>
public class ExplorerMenuTests
{
    [Fact]
    public void CommandLinesRoundTrip()
    {
        var exe = @"C:\Program Files\FlyknitBuddy\Flyknit.Client.exe";
        foreach (var entry in ExplorerMenu.Entries)
        {
            var command = ExplorerMenu.Command(exe, entry);
            Assert.StartsWith($"\"{exe}\" --shell ", command);
            Assert.EndsWith(entry.Target == ShellTarget.Background ? "\"%V\"" : "\"%1\"", command);
        }

        var parsed = ShellRequest.Parse(new[] { "--silent", "--shell", "summarize", @"D:\报表\九月.xlsx" });
        Assert.Equal(new ShellRequest(ShellAction.Summarize, @"D:\报表\九月.xlsx"), parsed);
        Assert.Equal(parsed, ShellRequest.Parse(parsed!.ToArgs()));
    }

    [Fact]
    public void DriveRootAndBadInputAreHandled()
    {
        // "%V" 在 D:\ 时，命令行变成 "D:\" —— 解析后多一个引号
        Assert.Equal(@"D:\", ShellRequest.Parse(new[] { "--shell", "workspace", "D:\\\"" })!.Path);
        Assert.Null(ShellRequest.Parse(new[] { "--shell", "format", @"C:\" }));
        Assert.Null(ShellRequest.Parse(new[] { "--shell", "ask" }));
        Assert.Null(ShellRequest.Parse(new[] { "--shell", "ask", "  " }));
        Assert.Null(ShellRequest.Parse(new[] { "--silent" }));
        Assert.Null(ShellRequest.Parse(new[] { "--shell", "99", "a.txt" }));
    }

    [Fact]
    public void MultiSelectGoesIntoOneConversation()
    {
        var batcher = new ShellRequestBatcher();
        batcher.Add(new ShellRequest(ShellAction.Ask, @"D:\a.xlsx"));
        batcher.Add(new ShellRequest(ShellAction.Ask, @"D:\b.docx"));
        batcher.Add(new ShellRequest(ShellAction.Ask, @"d:\A.XLSX"));
        batcher.Add(new ShellRequest(ShellAction.Workspace, @"D:\x"));
        batcher.Add(new ShellRequest(ShellAction.Workspace, @"D:\y"));
        var batches = batcher.Drain();
        Assert.Equal(new[] { @"D:\a.xlsx", @"D:\b.docx" }, batches.Single(b => b.Action == ShellAction.Ask).Paths);
        Assert.Equal(new[] { @"D:\y" }, batches.Single(b => b.Action == ShellAction.Workspace).Paths);
        Assert.Empty(batcher.Drain());
    }

    [Fact]
    public void LabelsFollowTheUiLanguage()
    {
        Assert.Equal("用 FlyknitBuddy 总结", ExplorerMenu.Label(ShellAction.Summarize, "zh-CN"));
        Assert.Equal("Tóm tắt bằng FlyknitBuddy", ExplorerMenu.Label(ShellAction.Summarize, "vi-VN"));
        Assert.Equal("Work here with FlyknitBuddy", ExplorerMenu.Label(ShellAction.Workspace, "en-US"));
    }
}
