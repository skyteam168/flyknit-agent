using Flyknit.Core.Workspace;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>右侧「文件」标签：只在工作区里列和搜，跳不出去；依赖目录列出来但搜索不进去。</summary>
public sealed class WorkspaceBrowserTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flyknit-ws").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("flyknit-ws-out").FullName;

    public WorkspaceBrowserTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "报表", "十月"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, "说明.md"), "# 说明");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "报表", "十月", "产量汇总.xlsx"), "x");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "产量.js"), "x");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "no");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _outside })
        {
            try { Directory.Delete(d, true); } catch (Exception) { }
        }
    }

    [Fact]
    public void ListingShowsFoldersFirstAndHidesGit()
    {
        var list = WorkspaceBrowser.List(_root, "");

        Assert.True(list.Exists);
        Assert.Equal(new[] { true, true, false, false }, list.Entries.Select(e => e.IsDirectory));
        Assert.DoesNotContain(list.Entries, e => e.Name == ".git");
        Assert.True(list.Entries.Single(e => e.Name == "node_modules").Noise);
        Assert.Equal(1, list.Entries.Single(e => e.Name == "a.txt").Size);

        var sub = WorkspaceBrowser.List(_root, Path.Combine("报表", "十月"));
        var file = Assert.Single(sub.Entries);
        Assert.Equal(Path.Combine("报表", "十月", "产量汇总.xlsx"), file.Path);
        Assert.Equal(Path.Combine(_root, "报表", "十月", "产量汇总.xlsx"), file.FullPath);
    }

    [Fact]
    public void PathsCannotEscapeTheWorkspace()
    {
        var escape = Path.Combine("..", Path.GetFileName(_outside));
        Assert.Null(WorkspaceBrowser.Resolve(_root, escape));
        // 绝对路径：Windows 上（D:\...）直接拒绝；Linux 上开头的 / 当相对路径，落在工作区里面。总之出不去
        var abs = WorkspaceBrowser.Resolve(_root, _outside);
        Assert.True(abs is null || abs.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar), abs);
        Assert.False(File.Exists(abs ?? ""));
        Assert.Null(WorkspaceBrowser.Resolve("", "a.txt"));
        Assert.False(WorkspaceBrowser.List(_root, escape).Exists);
        Assert.Equal(Path.GetFullPath(_root), WorkspaceBrowser.Resolve(_root, "")!.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(_root, "a.txt"), WorkspaceBrowser.Resolve(_root, "a.txt"));
        Assert.Equal(Path.Combine(_root, "a.txt"), WorkspaceBrowser.Resolve(_root, "/a.txt")); // 开头的分隔符当相对路径
    }

    [Fact]
    public void SearchFindsByNameButSkipsDependencyFolders()
    {
        var r = WorkspaceBrowser.Search(_root, "产量");

        var hit = Assert.Single(r.Results);
        Assert.Equal("产量汇总.xlsx", hit.Name);
        Assert.False(r.Truncated);
        Assert.Contains(WorkspaceBrowser.Search(_root, "十月").Results, e => e.IsDirectory);
        Assert.Contains(WorkspaceBrowser.Search(_root, "说明.MD").Results, e => e.Name == "说明.md"); // 不分大小写
        Assert.Empty(WorkspaceBrowser.Search(_root, "  ").Results);
        Assert.Empty(WorkspaceBrowser.Search(_root, "secret").Results);
    }

    [Fact]
    public void BigFoldersAreCut()
    {
        var big = Path.Combine(_root, "big");
        Directory.CreateDirectory(big);
        for (var i = 0; i < WorkspaceBrowser.MaxEntries + 5; i++)
        {
            File.WriteAllText(Path.Combine(big, $"f{i}.log"), "");
        }

        var list = WorkspaceBrowser.List(_root, "big");

        Assert.True(list.Truncated);
        Assert.Equal(WorkspaceBrowser.MaxEntries, list.Entries.Count);
    }
}
