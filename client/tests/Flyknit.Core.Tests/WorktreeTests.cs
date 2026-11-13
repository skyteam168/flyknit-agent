using System.Diagnostics;
using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Git;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 并行写的独立工作副本（docs/c4-parallel-writes.md）：真实 git 仓库上建两个 worktree，各改同一个文件互不覆盖、员工的工作副本不变；
/// 有未提交改动不删；找出改到同一个文件的；建 PR 只认自己的分支。
/// </summary>
public sealed class WorktreeTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("flyknit-wt-repo").FullName;
    private readonly string _root = Directory.CreateTempSubdirectory("flyknit-wt-root").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _repo, _root })
        {
            try
            {
                Directory.Delete(d, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
        return stdout.Trim();
    }

    private void InitRepo()
    {
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "config", "user.name", "t");
        Git(_repo, "config", "user.email", "t@example.com");
        Git(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "report.cs"), "// v1\n");
        File.WriteAllText(Path.Combine(_repo, "readme.md"), "hi\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "init");
        Git(_repo, "remote", "add", "origin", "https://github.com/acme/app.git");
    }

    private static void Commit(string dir, string file, string content)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "-qam", "change " + file);
    }

    [Fact]
    public async Task TwoWorktreesEditTheSameFileWithoutTouchingEachOtherOrTheEmployeesCopy()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(_repo, "readme.md"), "员工自己没提交的改动\n");   // 员工工作副本里的未提交改动

        var a = await WorktreeManager.CreateAsync(_repo, _root, CancellationToken.None);
        var b = await WorktreeManager.CreateAsync(_repo, _root, CancellationToken.None);
        Assert.StartsWith(WorktreeManager.BranchPrefix, a.Branch);
        Assert.NotEqual(a.Branch, b.Branch);
        Assert.NotEqual(a.Path, b.Path);
        Assert.False(a.Path.StartsWith(_repo, StringComparison.OrdinalIgnoreCase));            // 不在员工的工作区里
        Assert.Equal("hi\n", File.ReadAllText(Path.Combine(a.Path, "readme.md")));             // 未提交的改动不带过去

        Commit(a.Path, "report.cs", "// fix A\n");
        Commit(b.Path, "report.cs", "// fix B\n");

        Assert.Equal("// fix A", Git(_repo, "show", $"{a.Branch}:report.cs"));
        Assert.Equal("// fix B", Git(_repo, "show", $"{b.Branch}:report.cs"));
        Assert.Equal("main", Git(_repo, "rev-parse", "--abbrev-ref", "HEAD"));                  // 员工当前分支不变
        Assert.Equal("// v1\n", File.ReadAllText(Path.Combine(_repo, "report.cs")).Replace("\r\n", "\n"));
        Assert.Equal("员工自己没提交的改动\n", File.ReadAllText(Path.Combine(_repo, "readme.md")));

        // 两件都改了 report.cs：找出来
        var files = new Dictionary<string, IReadOnlyList<string>>
        {
            ["1/2"] = await ChangeOverlap.FilesChangedAsync(_repo, a.BaseCommit, a.Branch, CancellationToken.None),
            ["2/2"] = await ChangeOverlap.FilesChangedAsync(_repo, b.BaseCommit, b.Branch, CancellationToken.None),
        };
        var overlap = ChangeOverlap.Find(files);
        Assert.Equal(new[] { "1/2", "2/2" }, overlap["report.cs"]);
        Assert.Single(overlap);
        Assert.Empty(ChangeOverlap.Find(new Dictionary<string, IReadOnlyList<string>> { ["x"] = new[] { "a.cs" }, ["y"] = new[] { "b.cs" } }));
    }

    [Fact]
    public async Task ADirtyWorktreeIsKeptAndACleanOneIsRemoved()
    {
        InitRepo();
        var w = await WorktreeManager.CreateAsync(_repo, _root, CancellationToken.None);
        File.WriteAllText(Path.Combine(w.Path, "new.txt"), "没提交");
        Assert.True(await WorktreeManager.IsDirtyAsync(w.Path, CancellationToken.None));
        var (removed, reason) = await WorktreeManager.RemoveAsync(w.RepoRoot, w.Path, CancellationToken.None);
        Assert.False(removed);
        Assert.Contains("没提交", reason);
        Assert.True(Directory.Exists(w.Path));

        File.Delete(Path.Combine(w.Path, "new.txt"));
        Assert.True((await WorktreeManager.RemoveAsync(w.RepoRoot, w.Path, CancellationToken.None)).Removed);
        Assert.False(Directory.Exists(w.Path));
        Assert.DoesNotContain(await WorktreeManager.ListAsync(_repo, CancellationToken.None), p => Path.GetFullPath(p) == Path.GetFullPath(w.Path));
        Assert.Contains(w.Branch, Git(_repo, "branch", "--list", "flyknit/*"));                 // 分支保留
        Assert.True((await WorktreeManager.RemoveAsync(w.RepoRoot, w.Path, CancellationToken.None)).Removed); // 已经没了：不出错
    }

    [Fact]
    public void TheStoreRemembersActiveWorktreesPerConversation()
    {
        var store = new ConversationStore(Path.Combine(_root, "w.db"));
        var conv = store.Create(ConversationMode.Agent, "1/2").Id;
        var row = store.Worktrees.Add(conv, new WorktreeManager.Created("D:\\wt\\a", "flyknit/x", "abc", "D:\\repo"));
        Assert.Equal(row.Id, store.Worktrees.ActiveFor(conv)!.Id);
        Assert.Single(store.Worktrees.Active());
        store.Worktrees.MarkRemoved(row.Id);
        Assert.Null(store.Worktrees.ActiveFor(conv));
        Assert.Empty(store.Worktrees.Active());
    }

    [Fact]
    public async Task APullRequestFromAnIsolatedCopyMustUseItsOwnBranch()
    {
        InitRepo();
        var w = await WorktreeManager.CreateAsync(_repo, _root, CancellationToken.None);
        Git(w.Path, "switch", "-q", "-c", "other");                                            // AI 自己切到了别的分支
        var ctx = new ToolContext
        {
            Policy = CommandPolicy.Default(),
            ConversationId = "c1",
            Workspace = w.Path,
            Permission = PermissionMode.Workspace,
            IsolatedBranch = w.Branch,
        };
        var args = JsonSerializer.SerializeToElement(new { title = "fix" });
        var r = await new CreatePullRequestTool(null).ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains(w.Branch, r.Output);
        Assert.Contains("只能从分支", r.Output);
        Assert.Equal(w.Branch, ctx.ForChild("r2", PermissionMode.ReadOnly).IsolatedBranch);                               // 专家 / 子代理继承
    }

    // ---------- 沙箱工具不排队 ----------

    private sealed class SlowSandboxTool : ITool, Flyknit.Core.Sandbox.ISandboxedTool
    {
        public int Running;
        public int MaxConcurrent;
        public string Name => "sandbox_write";
        public string Description => "x";
        public System.Text.Json.Nodes.JsonObject Parameters => new() { ["type"] = "object", ["properties"] = new System.Text.Json.Nodes.JsonObject() };
        public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto() with { Effect = CommandEffect.Write };
        public string Describe(JsonElement args) => "写";
        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Running);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Delay(200, ct);
            Interlocked.Decrement(ref Running);
            return new ToolResult(true, "ok");
        }
    }

    [Fact]
    public async Task SandboxedToolsDoNotWaitForTheWriteGate()
    {
        var gate = new WriteGate();
        using var held = await gate.AcquireAsync(CancellationToken.None);                       // 本机有写操作占着锁
        var tool = new SlowSandboxTool();
        var registry = new ToolRegistry();
        registry.Add(tool);
        var gw = new Scripted(new ChatTurn { ToolCalls = new[] { new ToolCall("c1", "sandbox_write", "{}") } }, new ChatTurn { Content = "完成" });
        var loop = new AgentLoop(gw, registry, new Reject()) { WriteGate = gate };
        var run = loop.RunAsync(new List<ChatMessage> { ChatMessage.User("改") }, Scenes.Agent,
            new ToolContext { Policy = CommandPolicy.Default(), ConversationId = "c", Workspace = _root, Permission = PermissionMode.Workspace },
            new Quiet(), true, CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(5000));
        Assert.Same(run, finished);                                                           // 锁被占着，沙箱工具照样执行完
        Assert.Equal(1, tool.MaxConcurrent);
    }

    private sealed class Scripted(params ChatTurn[] turns) : IChatGateway
    {
        private readonly Queue<ChatTurn> _turns = new(turns);
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) =>
            Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : new ChatTurn { Content = "完成" });
    }

    private sealed class Reject : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.Reject);
    }

    private sealed class Quiet : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }
}
