using System.Diagnostics;
using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>开发者工具：glob、grep、multi_edit、git_*（git 用例在真实的临时仓库上跑）。</summary>
public sealed class DeveloperToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flyknit-dev-" + Guid.NewGuid().ToString("N"));

    public DeveloperToolsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal); // .git 里的对象文件是只读的
            }
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private ToolContext Ctx(PermissionMode mode = PermissionMode.Workspace) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _dir,
        Permission = mode,
    };

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // ---------- glob ----------

    [Theory]
    [InlineData("*.cs", "src/a/B.cs", true)]
    [InlineData("*.cs", "B.css", false)]
    [InlineData("src/**/*.ts", "src/a/b/c.ts", true)]
    [InlineData("src/**/*.ts", "src/c.ts", true)]
    [InlineData("src/**/*.ts", "lib/c.ts", false)]
    [InlineData("src/*.ts", "src/a/c.ts", false)]
    [InlineData("**/{README,readme}.md", "docs/README.md", true)]
    [InlineData("*.{ts,vue}", "x/App.vue", true)]
    [InlineData("a?c.txt", "abc.txt", true)]
    [InlineData("SRC/**/*.CS", "src/x.cs", true)]
    public void GlobMatches(string pattern, string path, bool expected) => Assert.Equal(expected, new Glob(pattern).IsMatch(path));

    [Fact]
    public async Task SearchFilesSupportsDoubleStarAndSkipsGitDirs()
    {
        Write("src/a/one.ts", "");
        Write("src/two.ts", "");
        Write("node_modules/pkg/three.ts", "");
        Write("lib/four.ts", "");
        var r = await new SearchFilesTool().ExecuteAsync(Args(new { path = _dir, pattern = "src/**/*.ts" }), Ctx(), default);
        Assert.Contains("one.ts", r.Output);
        Assert.Contains("two.ts", r.Output);
        Assert.DoesNotContain("three.ts", r.Output);
        Assert.DoesNotContain("four.ts", r.Output);
    }

    [Fact]
    public async Task SearchFilesSimplePatternUnchanged()
    {
        Write("报表/一月报表.xlsx", "");
        var r = await new SearchFilesTool().ExecuteAsync(Args(new { path = _dir, pattern = "*报表*" }), Ctx(), default);
        Assert.Contains("一月报表.xlsx", r.Output);
    }

    // ---------- grep ----------

    [Fact]
    public async Task GrepFindsLinesWithLineNumbers()
    {
        Write("src/login.cs", "class A\n{\n    void Login() {}\n}\n");
        Write("src/other.ts", "function login() {}\n");
        Write("obj/gen.cs", "void Login() {}\n");
        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "Login\\(", glob = "*.cs" }), Ctx(), default);
        Assert.True(r.Ok, r.Output);
        Assert.Contains("src/login.cs:3:     void Login() {}", r.Output);
        Assert.DoesNotContain("other.ts", r.Output);
        Assert.DoesNotContain("gen.cs", r.Output); // obj 默认跳过
    }

    [Fact]
    public async Task GrepContextAndIgnoreCase()
    {
        Write("a.txt", "one\ntwo\nTHREE\nfour\nfive\n");
        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "three", ignore_case = true, context = 1 }), Ctx(), default);
        Assert.Contains("a.txt-2- two", r.Output);
        Assert.Contains("a.txt:3: THREE", r.Output);
        Assert.Contains("a.txt-4- four", r.Output);
        Assert.DoesNotContain("one", r.Output);
    }

    [Fact]
    public async Task GrepFilesAndCountModes()
    {
        Write("a.txt", "x\nx\n");
        Write("b.txt", "x\n");
        var files = await new GrepTool().ExecuteAsync(Args(new { pattern = "x", output = "files" }), Ctx(), default);
        Assert.Contains("a.txt", files.Output);
        Assert.Contains("b.txt", files.Output);
        var count = await new GrepTool().ExecuteAsync(Args(new { pattern = "x", output = "count" }), Ctx(), default);
        Assert.Contains("a.txt: 2", count.Output);
    }

    [Fact]
    public async Task GrepLiteralAndBadRegex()
    {
        Write("a.txt", "price (USD) 3.5\n");
        var bad = await new GrepTool().ExecuteAsync(Args(new { pattern = "(USD" }), Ctx(), default);
        Assert.False(bad.Ok);
        Assert.Contains("literal", bad.Output);
        var lit = await new GrepTool().ExecuteAsync(Args(new { pattern = "(USD", literal = true }), Ctx(), default);
        Assert.Contains("a.txt:1:", lit.Output);
    }

    [Fact]
    public async Task GrepSkipsBinaryAndHonoursLimit()
    {
        File.WriteAllBytes(Path.Combine(_dir, "bin.dat"), new byte[] { (byte)'h', 0, (byte)'i' });
        Write("many.txt", string.Join("\n", Enumerable.Repeat("hit", 50)));
        var r = await new GrepTool().ExecuteAsync(Args(new { pattern = "h", max_results = 5 }), Ctx(), default);
        Assert.DoesNotContain("bin.dat", r.Output);
        Assert.Contains("已达上限", r.Output);
        Assert.Equal(5, r.Output.Split('\n').Count(l => l.StartsWith("many.txt:")));
    }

    [Fact]
    public void GrepIsReadOnlyAndParallel()
    {
        var tool = new GrepTool();
        Assert.Equal(RiskLevel.Auto, tool.Assess(Args(new { pattern = "x" }), Ctx(PermissionMode.ReadOnly)).Level);
        Assert.True(PlanMode.Allows(tool));
        Assert.IsAssignableFrom<IParallelTool>(tool);
    }

    // ---------- multi_edit ----------

    [Fact]
    public async Task MultiEditAppliesInOrder()
    {
        Write("a.cs", "int a = 1;\nint b = 2;\n");
        var r = await new MultiEditTool().ExecuteAsync(Args(new
        {
            path = "a.cs",
            edits = new object[]
            {
                new { old_text = "int a = 1;", new_text = "int a = 10;" },
                new { old_text = "int a = 10;\nint b = 2;", new_text = "int a = 10;\nint b = 20;" }, // 看到的是上一项改完的
            },
        }), Ctx(), default);
        Assert.True(r.Ok, r.Output);
        Assert.Equal("int a = 10;\nint b = 20;\n", File.ReadAllText(Path.Combine(_dir, "a.cs")));
    }

    [Fact]
    public async Task MultiEditIsAllOrNothing()
    {
        Write("a.cs", "x\ny\n");
        var r = await new MultiEditTool().ExecuteAsync(Args(new
        {
            path = "a.cs",
            edits = new object[] { new { old_text = "x", new_text = "X" }, new { old_text = "missing", new_text = "?" } },
        }), Ctx(), default);
        Assert.False(r.Ok);
        Assert.Contains("第 2 项", r.Output);
        Assert.Equal("x\ny\n", File.ReadAllText(Path.Combine(_dir, "a.cs")));
    }

    [Fact]
    public async Task MultiEditRejectsAmbiguous()
    {
        Write("a.cs", "x x\n");
        var r = await new MultiEditTool().ExecuteAsync(Args(new { path = "a.cs", edits = new object[] { new { old_text = "x", new_text = "y" } } }), Ctx(), default);
        Assert.False(r.Ok);
        var all = await new MultiEditTool().ExecuteAsync(Args(new { path = "a.cs", edits = new object[] { new { old_text = "x", new_text = "y", replace_all = true } } }), Ctx(), default);
        Assert.True(all.Ok);
        Assert.Equal("y y\n", File.ReadAllText(Path.Combine(_dir, "a.cs")));
    }

    [Fact]
    public void MultiEditUsesWritePolicy()
    {
        var tool = new MultiEditTool();
        Assert.Equal(RiskLevel.Blocked, tool.Assess(Args(new { path = Path.Combine(_dir, "a.cs") }), Ctx(PermissionMode.ReadOnly)).Level);
        Assert.False(PlanMode.Allows(tool));
    }

    // ---------- git ----------

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    private void InitRepo()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "测试员");
        Git("config", "user.email", "t@example.com");
        Git("config", "commit.gpgsign", "false");
        Write("readme.md", "hello\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "初始提交");
    }

    [Fact]
    public async Task GitStatusDiffLogOnRealRepo()
    {
        InitRepo();
        Write("readme.md", "hello\nworld\n");
        Write("新文件.txt", "x");

        var status = await new GitStatusTool().ExecuteAsync(Args(new { }), Ctx(), default);
        Assert.True(status.Ok, status.Output);
        Assert.Contains("## main", status.Output);
        Assert.Contains(" M readme.md", status.Output);
        Assert.Contains("?? 新文件.txt", status.Output); // 中文文件名不转义

        var diff = await new GitDiffTool().ExecuteAsync(Args(new { file = "readme.md" }), Ctx(), default);
        Assert.Contains("+world", diff.Output);

        var log = await new GitLogTool().ExecuteAsync(Args(new { max_count = 5 }), Ctx(), default);
        Assert.Contains("初始提交", log.Output);
        Assert.Contains("测试员", log.Output);
    }

    [Fact]
    public async Task GitCommitStagesAndCommits()
    {
        InitRepo();
        Write("a.txt", "a");
        Write("b.txt", "b");
        var r = await new GitCommitTool().ExecuteAsync(Args(new { message = "加 a.txt", files = new[] { "a.txt" } }), Ctx(), default);
        Assert.True(r.Ok, r.Output);
        Assert.Contains("加 a.txt", r.Output);
        var status = await new GitStatusTool().ExecuteAsync(Args(new { }), Ctx(), default);
        Assert.Contains("?? b.txt", status.Output); // 只提交了指定的文件
        Assert.DoesNotContain("a.txt", status.Output);

        var nothing = await new GitCommitTool().ExecuteAsync(Args(new { message = "空" }), Ctx(), default);
        Assert.False(nothing.Ok);
        Assert.Contains("没有可提交的改动", nothing.Output);
    }

    [Theory]
    [InlineData("--output=/tmp/pwned")]
    [InlineData("-p")]
    public async Task GitRejectsOptionInjection(string value)
    {
        InitRepo();
        var diff = await new GitDiffTool().ExecuteAsync(Args(new { target = value }), Ctx(), default);
        Assert.False(diff.Ok);
        var log = await new GitLogTool().ExecuteAsync(Args(new { file = value }), Ctx(), default);
        Assert.False(log.Ok);
        var commit = await new GitCommitTool().ExecuteAsync(Args(new { message = "x", files = new[] { value } }), Ctx(), default);
        Assert.False(commit.Ok);
    }

    [Fact]
    public async Task GitOutsideRepoFailsClearly()
    {
        var r = await new GitStatusTool().ExecuteAsync(Args(new { }), Ctx(), default);
        Assert.False(r.Ok);
        Assert.Contains("不在 git 仓库里", r.Output);
    }

    [Fact]
    public void GitCommitIsJudgedLikeTheEquivalentShellCommand()
    {
        foreach (var mode in new[] { PermissionMode.ReadOnly, PermissionMode.Workspace, PermissionMode.Full })
        {
            var tool = new GitCommitTool().Assess(Args(new { message = "x; rm -rf /", all = true }), Ctx(mode));
            var shell = new RunShellTool().Assess(Args(new { command = "git add -A && git commit" }), Ctx(mode));
            Assert.Equal(shell.Level, tool.Level);
        }
        Assert.Equal(RiskLevel.Blocked, new GitCommitTool().Assess(Args(new { message = "x" }), Ctx(PermissionMode.ReadOnly)).Level);
        Assert.False(PlanMode.Allows(new GitCommitTool()));
    }

    [Fact]
    public void ReadOnlyGitToolsAreAuto()
    {
        foreach (var tool in new ITool[] { new GitStatusTool(), new GitDiffTool(), new GitLogTool() })
        {
            Assert.Equal(RiskLevel.Auto, tool.Assess(Args(new { }), Ctx(PermissionMode.ReadOnly)).Level);
            Assert.True(PlanMode.Allows(tool));
        }
    }

    [Fact]
    public void OffByDefaultAndOnlyInAgentMode()
    {
        SecuritySettings Settings(bool? on) => new(on is null
            ? new Dictionary<string, SecurityItem>()
            : new Dictionary<string, SecurityItem> { [SecuritySettings.DeveloperTools] = new() { Value = on.Value, Locked = true } });

        Assert.Empty(DeveloperToolSet.For(null, agentMode: true));
        Assert.Empty(DeveloperToolSet.For(Settings(null), agentMode: true)); // 服务端没下发：按关
        Assert.Empty(DeveloperToolSet.For(Settings(false), agentMode: true));
        Assert.Empty(DeveloperToolSet.For(Settings(true), agentMode: false)); // 对话模式没有工具
        Assert.Equal(6, DeveloperToolSet.For(Settings(true), agentMode: true).Count);
        // 专家只做减法
        var limited = DeveloperToolSet.For(Settings(true), agentMode: true, t => t.Name != "git_commit");
        Assert.DoesNotContain(limited, t => t.Name == "git_commit");
        Assert.Equal(5, limited.Count);
    }

    [Fact]
    public void ToolNamesAreStable()
    {
        Assert.Equal(new[] { "grep", "multi_edit", "git_status", "git_diff", "git_log", "git_commit" }, DeveloperToolSet.All.Select(t => t.Name));
    }
}
