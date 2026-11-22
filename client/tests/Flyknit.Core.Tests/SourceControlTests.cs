using System.Text.Json;
using Flyknit.Core.Git;
using Flyknit.Core.Hosting;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>编码模式的「源代码管理」：改动列表、上次提交的内容、暂存 / 取消暂存、丢弃、提交、路径校验、开关。用真的 git 跑。</summary>
public sealed class SourceControlTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("fk-scm-").FullName;
    private readonly List<string> _log = new();
    private bool _enabled = true;
    private readonly SourceControlFeature _scm;

    public SourceControlTests()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Tester");
        Git("config", "core.autocrlf", "false"); // Windows CI 默认会把换行转成 CRLF，测试按原样比较内容
        Write("src/app.ts", "export const a = 1\n");
        Write("README.md", "# demo\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
        // web 是工作区（仓库里的一个子文件夹也行）
        _scm = new SourceControlFeature(id => id == "c1" ? _repo : id == "sub" ? Path.Combine(_repo, "src") : null, () => _enabled, _log.Add)
        {
            SuggestMessage = (diff, _) => Task.FromResult<string?>(diff.Contains("app.ts") ? "改了 app.ts" : null),
        };
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal); // .git 里有只读文件
        }
        Directory.Delete(_repo, true);
    }

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private void Git(params string[] args)
    {
        var r = GitRunner.RunAsync(_repo, args, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(r.ExitCode == 0, r.Stderr);
    }

    private async Task<JsonElement> Call(string method, object args)
    {
        var result = await _scm.HandleAsync(method, new BridgeArgs(JsonSerializer.SerializeToElement(args)), CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private async Task<ScmStatus> Status(string conv = "c1") =>
        (ScmStatus)(await _scm.HandleAsync("scm.status", new BridgeArgs(JsonSerializer.SerializeToElement(new { conversationId = conv })), CancellationToken.None))!;

    [Fact]
    public async Task ListsChangesWithStagedModifiedAndUntracked()
    {
        Write("src/app.ts", "export const a = 2\n");
        Write("src/new.ts", "x\n");
        File.Delete(Path.Combine(_repo, "README.md"));
        Git("add", "src/app.ts");
        Write("src/app.ts", "export const a = 3\n"); // 暂存后又改了：两边都有

        var s = await Status();
        Assert.True(s.IsRepo);
        Assert.Equal("main", s.Branch);
        Assert.Contains(s.Changes, c => c.Path == "src/app.ts" && c.Status == "M" && c.Staged);
        Assert.Contains(s.Changes, c => c.Path == "src/app.ts" && c.Status == "M" && !c.Staged);
        Assert.Contains(s.Changes, c => c.Path == "src/new.ts" && c.Status == "U" && !c.Staged);
        Assert.Contains(s.Changes, c => c.Path == "README.md" && c.Status == "D" && !c.Staged);
        // 工作区是仓库里的子文件夹：工作区外的文件打不开（WorkspacePath 为空）
        var sub = await Status("sub");
        Assert.Equal("app.ts", sub.Changes.First(c => c.Path == "src/app.ts").WorkspacePath);
        Assert.Null(sub.Changes.First(c => c.Path == "README.md").WorkspacePath);
    }

    [Fact]
    public async Task HeadContentForDiff()
    {
        Write("src/app.ts", "export const a = 9\n");
        var head = await Call("scm.head", new { conversationId = "c1", path = "src/app.ts" });
        Assert.Equal("export const a = 1\n", head.GetProperty("text").GetString());
        Assert.True(head.GetProperty("exists").GetBoolean());
        Write("src/fresh.ts", "y\n");
        Assert.False((await Call("scm.head", new { conversationId = "c1", path = "src/fresh.ts" })).GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task StageUnstageDiscardAndCommit()
    {
        Write("src/app.ts", "export const a = 5\n");
        Write("src/new.ts", "n\n");
        Assert.True((await Call("scm.stage", new { conversationId = "c1", paths = new[] { "src/app.ts" } })).GetProperty("ok").GetBoolean());
        Assert.Contains((await Status()).Changes, c => c.Path == "src/app.ts" && c.Staged);
        Assert.True((await Call("scm.unstage", new { conversationId = "c1", paths = new[] { "src/app.ts" } })).GetProperty("ok").GetBoolean());
        Assert.DoesNotContain((await Status()).Changes, c => c.Staged);

        // 丢弃：跟踪的恢复原样，新文件删掉
        Assert.True((await Call("scm.discard", new { conversationId = "c1", paths = new[] { "src/app.ts", "src/new.ts" } })).GetProperty("ok").GetBoolean());
        Assert.Equal("export const a = 1\n", File.ReadAllText(Path.Combine(_repo, "src", "app.ts")));
        Assert.False(File.Exists(Path.Combine(_repo, "src", "new.ts")));
        Assert.Empty((await Status()).Changes);

        // 提交：没写说明不行；stageAll 把全部改动一起提交
        Write("src/app.ts", "export const a = 7\n");
        Assert.False((await Call("scm.commit", new { conversationId = "c1", message = "  " })).GetProperty("ok").GetBoolean());
        Assert.True((await Call("scm.commit", new { conversationId = "c1", message = "改 a", stageAll = true })).GetProperty("ok").GetBoolean());
        Assert.Empty((await Status()).Changes);
        Assert.Contains(_log, l => l.Contains("提交") && l.Contains("改 a"));
    }

    [Fact]
    public async Task AiCommitMessageUsesTheDiff()
    {
        Assert.False((await Call("scm.message", new { conversationId = "c1" })).GetProperty("ok").GetBoolean()); // 没有改动
        Write("src/app.ts", "export const a = 8\n");
        var r = await Call("scm.message", new { conversationId = "c1" });
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("改了 app.ts", r.GetProperty("text").GetString());
    }

    [Fact]
    public async Task RejectsPathsOutsideTheRepoAndOptionLikePaths()
    {
        foreach (var bad in new[] { "../outside.txt", "-rf", "/etc/passwd", "" })
        {
            Assert.False((await Call("scm.stage", new { conversationId = "c1", paths = new[] { bad } })).GetProperty("ok").GetBoolean());
            Assert.False((await Call("scm.discard", new { conversationId = "c1", paths = new[] { bad } })).GetProperty("ok").GetBoolean());
        }
        Assert.False((await Call("scm.head", new { conversationId = "c1", path = "../x" })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task OffWithoutDeveloperToolsAndOutsideARepo()
    {
        Assert.False((await Status("nobody")).IsRepo);
        _enabled = false;
        Assert.False((await Call("scm.stage", new { conversationId = "c1", paths = new[] { "src/app.ts" } })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ParsesBranchAheadBehindAndRenames()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "r"));
        var s = SourceControlFeature.Parse("## main...origin/main [ahead 2, behind 1]\0R  new.ts\0old.ts\0 M a.ts\0", root, root);
        Assert.Equal(("main", 2, 1, true), (s.Branch, s.Ahead, s.Behind, s.HasUpstream));
        Assert.Contains(s.Changes, c => c.Path == "new.ts" && c.Status == "R" && c.Staged && c.OldPath == "old.ts");
        Assert.Contains(s.Changes, c => c.Path == "a.ts" && c.Status == "M" && !c.Staged);
        var fresh = SourceControlFeature.Parse("## No commits yet on main\0?? a.ts\0", root, root);
        Assert.Equal(("main", false), (fresh.Branch, fresh.HasUpstream));
        Assert.True(SourceControlFeature.Parse("## HEAD (no branch)\0", root, root).Detached);
    }
}
