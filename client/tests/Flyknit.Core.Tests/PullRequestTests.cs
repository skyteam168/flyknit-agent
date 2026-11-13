using System.Diagnostics;
using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Git;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>仓库信息、分支操作、PR 状态解析、盯 PR 的规矩（50 分钟、合并即停、自动修复上限）。</summary>
public sealed class PullRequestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flyknit-pr-" + Guid.NewGuid().ToString("N"));

    public PullRequestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    // ---------- 远端地址 ----------

    [Theory]
    [InlineData("https://github.com/acme/app.git", "github.com", "acme", "app")]
    [InlineData("https://github.com/acme/app", "github.com", "acme", "app")]
    [InlineData("git@github.com:acme/app.git", "github.com", "acme", "app")]
    [InlineData("ssh://git@github.com/acme/app.git", "github.com", "acme", "app")]
    [InlineData("https://gitlab.example.com/group/sub/app.git", "gitlab.example.com", "group/sub", "app")]
    [InlineData("https://gitee.com/acme/app.git", "gitee.com", "acme", "app")]
    public void RemotesAreParsed(string url, string host, string owner, string name) =>
        Assert.Equal((host, owner, name), GitRepository.ParseRemote(url));

    [Theory]
    [InlineData("https://x-access-token:ghp_secret123@github.com/acme/app.git", "https://github.com/acme/app.git")]
    [InlineData("https://user@github.com/acme/app.git", "https://github.com/acme/app.git")]
    [InlineData("git@github.com:acme/app.git", "git@github.com:acme/app.git")]
    public void CredentialsInRemoteUrlsAreStripped(string url, string expected) =>
        Assert.Equal(expected, GitRepository.StripCredentials(url));

    [Theory]
    [InlineData("## main...origin/main [ahead 2, behind 1]\n M a.cs\n?? b.cs\n", "main", false, 2, 1, 2)]
    [InlineData("## feature/x.y...origin/feature/x.y\n", "feature/x.y", false, 0, 0, 0)]
    [InlineData("## No commits yet on main\n?? a\n", "main", false, 0, 0, 1)]
    [InlineData("## HEAD (no branch)\n", "", true, 0, 0, 0)]
    [InlineData("## dev\n", "dev", false, 0, 0, 0)]
    public void StatusLineIsParsed(string porcelain, string branch, bool detached, int ahead, int behind, int changed) =>
        Assert.Equal((branch, detached, ahead, behind, changed), GitRepository.ParseStatus(porcelain));

    // ---------- 真实仓库上的分支操作 ----------

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
        Git("config", "user.name", "t");
        Git("config", "user.email", "t@example.com");
        Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "a");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
        Git("remote", "add", "origin", "https://x-access-token:ghp_secret@github.com/acme/app.git");
    }

    [Fact]
    public async Task RepoInfoOnARealRepository()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "b");

        var info = await GitRepository.InfoAsync(_dir, default);

        Assert.NotNull(info);
        Assert.Equal("main", info!.Branch);
        Assert.Equal("acme/app", info.DisplayName);
        Assert.True(info.IsGitHub);
        Assert.Equal("https://github.com/acme/app", info.WebUrl);
        Assert.Equal("https://github.com/acme/app/tree/main", info.BranchUrl);
        Assert.DoesNotContain("ghp_secret", info.RemoteUrl); // 界面上看不到令牌
        Assert.Equal(1, info.Changed);
    }

    [Fact]
    public async Task NotARepository() => Assert.Null(await GitRepository.InfoAsync(_dir, default));

    [Fact]
    public async Task CreateAndSwitchBranches()
    {
        InitRepo();
        var created = await GitRepository.CreateBranchAsync(_dir, "feature/login", checkout: true, default);
        Assert.True(created.Ok, created.Message);
        Assert.Equal("feature/login", (await GitRepository.InfoAsync(_dir, default))!.Branch);
        Assert.Contains("main", await GitRepository.BranchesAsync(_dir, default));

        var back = await GitRepository.SwitchAsync(_dir, "main", default);
        Assert.True(back.Ok, back.Message);
        Assert.Equal("main", (await GitRepository.InfoAsync(_dir, default))!.Branch);
    }

    [Fact]
    public async Task SwitchingWithUncommittedChangesIsRefused()
    {
        InitRepo();
        Git("branch", "other");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "changed");

        var r = await GitRepository.SwitchAsync(_dir, "other", default);

        Assert.False(r.Ok);
        Assert.Contains("没提交的改动", r.Message);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(_dir, "a.txt"))); // 改动还在
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("--orphan")]
    [InlineData("bad name")]
    [InlineData("a..b")]
    [InlineData("")]
    public async Task BadBranchNamesAreRejected(string name)
    {
        InitRepo();
        var r = await GitRepository.CreateBranchAsync(_dir, name, checkout: true, default);
        Assert.False(r.Ok);
    }

    // ---------- PR 状态 ----------

    private const string ViewJson = """
        {"number":93,"title":"阶段 0-5","url":"https://github.com/acme/app/pull/93","state":"OPEN","isDraft":false,
         "mergeable":"MERGEABLE","reviewDecision":"REVIEW_REQUIRED","headRefName":"feature","baseRefName":"main",
         "comments":[{"body":"x"}],"reviews":[],
         "statusCheckRollup":[
           {"__typename":"CheckRun","name":"client","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://github.com/acme/app/actions/runs/1"},
           {"__typename":"CheckRun","name":"server","status":"IN_PROGRESS","conclusion":"","detailsUrl":""},
           {"__typename":"CheckRun","name":"lint","status":"COMPLETED","conclusion":"SKIPPED","detailsUrl":""},
           {"__typename":"StatusContext","context":"ci/legacy","state":"SUCCESS","targetUrl":"https://ci.example.com/1"}
         ]}
        """;

    private static PrStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return PrStatus.Parse(doc.RootElement);
    }

    [Fact]
    public void GhViewJsonIsParsed()
    {
        var s = Parse(ViewJson);
        Assert.Equal(93, s.Number);
        Assert.Equal("failure", s.Ci);
        Assert.Equal(new[] { "fail", "pending", "skip", "pass" }, s.Checks.Select(c => c.State));
        Assert.Equal("ci/legacy", s.Checks[3].Name);
        Assert.Equal(1, s.Comments);
        Assert.Contains("✗ client https://github.com/acme/app/actions/runs/1", s.Describe());
        // 存进数据库再读回来不丢东西
        var back = PrStatus.FromJson(s.ToJson())!;
        Assert.Equal(s.Checks, back.Checks);
        Assert.Equal("failure", back.Ci);
    }

    private static PrStatus Status(string ci = "success", string state = "OPEN", string mergeable = "MERGEABLE", string review = "", int comments = 0) => new()
    {
        Number = 7,
        Title = "fix",
        State = state,
        Mergeable = mergeable,
        Review = review,
        Comments = comments,
        Base = "main",
        Checks = ci switch
        {
            "failure" => new() { new PrCheck("test", "fail", "u") },
            "pending" => new() { new PrCheck("test", "pending", "") },
            "none" => new(),
            _ => new() { new PrCheck("test", "pass", "") },
        },
    };

    [Fact]
    public void ChangesBetweenChecks()
    {
        Assert.Equal(new[] { "ci_failed" }, PrWatchPolicy.Changes(Status("pending"), Status("failure")).Select(e => e.Kind));
        Assert.Empty(PrWatchPolicy.Changes(Status("failure"), Status("failure")));       // 同样的失败不重复报
        Assert.Equal(new[] { "ci_passed" }, PrWatchPolicy.Changes(Status("failure"), Status("success")).Select(e => e.Kind));
        Assert.Equal(new[] { "conflict" }, PrWatchPolicy.Changes(Status(), Status(mergeable: "CONFLICTING")).Select(e => e.Kind));
        Assert.Equal(new[] { "changes_requested" }, PrWatchPolicy.Changes(Status(), Status(review: "CHANGES_REQUESTED")).Select(e => e.Kind));
        Assert.Equal(new[] { "new_comments" }, PrWatchPolicy.Changes(Status(comments: 1), Status(comments: 3)).Select(e => e.Kind));
        Assert.Equal(new[] { "merged" }, PrWatchPolicy.Changes(Status(), Status(state: "MERGED")).Select(e => e.Kind));
        // 第一次检查：已经存在的失败要报
        Assert.Equal(new[] { "ci_failed" }, PrWatchPolicy.Changes(null, Status("failure")).Select(e => e.Kind));
    }

    [Fact]
    public void NextCheckIsFiftyMinutesLaterAndStopsWhenMerged()
    {
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var w = new PrWatch { Number = 7, CreatedAt = now.AddHours(-1) };
        var next = PrWatchPolicy.Next(w, Status(), "", now);
        Assert.Equal(now.AddMinutes(50), next.NextCheckAt);
        Assert.True(next.Active);
        Assert.Equal("merged", PrWatchPolicy.Next(w, Status(state: "MERGED"), "", now).EndedReason);
        Assert.Equal("closed", PrWatchPolicy.Next(w, Status(state: "CLOSED"), "", now).EndedReason);
        Assert.Equal("expired", PrWatchPolicy.Next(w with { CreatedAt = now.AddDays(-15) }, Status(), "", now).EndedReason);
        // 查不到（网络、gh 掉登录）：保留上次状态，照常排下次
        var failed = PrWatchPolicy.Next(w with { Last = Status("failure") }, null, "network", now);
        Assert.Equal("failure", failed.Last!.Ci);
        Assert.Equal("network", failed.LastError);
        Assert.True(failed.Active);
    }

    [Fact]
    public void AutoFixPromptOnlyWhenOnAndUnderTheLimit()
    {
        var w = new PrWatch { Number = 7, AutoFix = true };
        var events = PrWatchPolicy.Changes(Status("pending"), Status("failure"));
        var prompt = PrWatchPolicy.FixPrompt(w, Status("failure"), events);
        Assert.NotNull(prompt);
        Assert.Contains("gh run view", prompt);
        Assert.Contains("不要跳过、禁用测试", prompt);
        Assert.Contains("不要 rebase、不要 push --force", prompt);

        Assert.Null(PrWatchPolicy.FixPrompt(w with { AutoFix = false }, Status("failure"), events));
        Assert.Null(PrWatchPolicy.FixPrompt(w with { AutoFixes = PrWatchPolicy.MaxAutoFixes }, Status("failure"), events));
        // 只是 CI 通过、批准：不用动手
        Assert.Null(PrWatchPolicy.FixPrompt(w, Status(), PrWatchPolicy.Changes(Status("failure"), Status())));
    }

    [Fact]
    public async Task WatcherChecksDueWatchesAndPersists()
    {
        var store = new ConversationStore(Path.Combine(_dir, "h.db"));
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var next = Status("pending");
        var watcher = new PrWatcher(store.PrWatches, (_, _, _) => Task.FromResult(next), () => now);

        var w = watcher.Watch("c1", _dir, Status("pending"), autoFix: true);
        Assert.Equal(now.AddMinutes(50), w.NextCheckAt);
        Assert.Equal(w.Id, watcher.Watch("c1", _dir, Status("pending"), true).Id); // 同一个 PR 不重复盯
        Assert.Empty(await watcher.CheckDueAsync(default));                  // 还没到点

        now = now.AddMinutes(51);
        next = Status("failure");
        var outcome = Assert.Single(await watcher.CheckDueAsync(default));
        Assert.Equal("ci_failed", Assert.Single(outcome.Events).Kind);
        Assert.NotNull(outcome.FixPrompt);
        Assert.Equal(1, outcome.Watch.AutoFixes);
        Assert.Equal("failure", store.PrWatches.Get(w.Id)!.Last!.Ci);       // 存下来了

        now = now.AddMinutes(51);
        next = Status(state: "MERGED");
        var merged = Assert.Single(await watcher.CheckDueAsync(default));
        Assert.False(merged.Watch.Active);
        now = now.AddMinutes(51);
        Assert.Empty(await watcher.CheckDueAsync(default));                   // 合并后不再查
        Assert.Equal("stopped", watcher.Stop(watcher.Watch("c1", _dir, Status(), false).Id)!.EndedReason);
    }

    [Fact]
    public async Task WatchingAPrSubscribesTheConversationUntilItEnds()
    {
        var store = new ConversationStore(Path.Combine(_dir, "s.db"));
        var conv = store.Create(ConversationMode.Agent, "改日报").Id;
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var next = Status();
        var watcher = new PrWatcher(store.PrWatches, (_, _, _) => Task.FromResult(next), () => now, store.Subscriptions);

        var w = watcher.Watch(conv, _dir, Status("pending"), autoFix: true);
        var sub = Assert.Single(store.Subscriptions.For(conv));
        Assert.True(sub.Active);
        Assert.Equal(Flyknit.Core.Tasks.SubscriptionKinds.PullRequest, sub.Kind);
        Assert.Equal(w.Id, sub.Selector);
        Assert.Equal("PR #7 fix", sub.Label);
        watcher.Watch(conv, _dir, Status("pending"), true);                    // 同一个 PR 再盯一次：不重复订
        Assert.Single(store.Subscriptions.For(conv));

        // 合并了：订阅跟着结束，记下原因
        now = now.AddMinutes(51);
        next = Status(state: "MERGED");
        await watcher.CheckDueAsync(default);
        var ended = Assert.Single(store.Subscriptions.For(conv));
        Assert.False(ended.Active);
        Assert.Equal("merged", ended.EndedReason);

        // 员工停掉：订阅结束成 stopped
        var w2 = watcher.Watch(conv, _dir, Status() with { Number = 8 }, false);
        watcher.Stop(w2.Id);
        Assert.Equal("stopped", store.Subscriptions.For(conv).Single(s => s.Selector == w2.Id).EndedReason);
        Assert.Null(store.Subscriptions.Find(Flyknit.Core.Tasks.SubscriptionKinds.PullRequest, w2.Id));
    }

    [Fact]
    public void StartupSyncBackfillsOldWatchesAndEndsOrphanedSubscriptions()
    {
        var store = new ConversationStore(Path.Combine(_dir, "sync.db"));
        var conv = store.Create(ConversationMode.Agent, "x").Id;
        var now = DateTimeOffset.Now;
        // 升级前就在盯的 PR：没有订阅
        var old = new PrWatcher(store.PrWatches, now: () => now).Watch(conv, _dir, Status(), true);
        Assert.Empty(store.Subscriptions.For(conv));
        // 订阅还挂着、PR 记录已经不盯了
        var gone = new PrWatcher(store.PrWatches, now: () => now).Watch(conv, _dir, Status() with { Number = 9 }, true);
        store.Subscriptions.Subscribe(conv, Flyknit.Core.Tasks.SubscriptionKinds.PullRequest, gone.Id, "PR #9");
        store.PrWatches.Save(gone with { EndedReason = "closed" });
        // 订阅指向的记录根本不存在
        store.Subscriptions.Subscribe(conv, Flyknit.Core.Tasks.SubscriptionKinds.PullRequest, "pw_missing", "PR #10");

        new PrWatcher(store.PrWatches, now: () => now, subscriptions: store.Subscriptions).SyncSubscriptions();

        var subs = store.Subscriptions.For(conv);
        Assert.True(subs.Single(s => s.Selector == old.Id).Active);
        Assert.Equal("closed", subs.Single(s => s.Selector == gone.Id).EndedReason);
        Assert.Equal("stopped", subs.Single(s => s.Selector == "pw_missing").EndedReason);
    }

    [Fact]
    public void SubscriptionFailuresDoNotStopWatching()
    {
        var store = new ConversationStore(Path.Combine(_dir, "f.db"));
        var failures = 0;
        // 对话不存在：订阅表的外键会拒，但盯 PR 照常
        var watcher = new PrWatcher(store.PrWatches, subscriptions: store.Subscriptions);
        watcher.SubscriptionFailed += _ => failures++;
        var w = watcher.Watch("no-such-conversation", _dir, Status(), true);
        Assert.True(store.PrWatches.Get(w.Id)!.Active);
        Assert.Equal(1, failures);
    }

    // ---------- 工具 ----------

    private ToolContext Ctx(PermissionMode mode = PermissionMode.Workspace) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Workspace = _dir,
        Permission = mode,
    };

    [Fact]
    public void CreatingAPrAlwaysAsksAndIsBlockedReadOnly()
    {
        var tool = new CreatePullRequestTool(null);
        var args = JsonSerializer.SerializeToElement(new { title = "x", body = "y" });
        var decision = tool.Assess(args, Ctx());
        Assert.Equal(RiskLevel.Confirm, decision.Level);       // 推送、建 PR 会影响别人：至少问一次
        Assert.NotEqual(CommandEffect.Destructive, decision.Effect);
        Assert.Equal(RiskLevel.Blocked, tool.Assess(args, Ctx(PermissionMode.ReadOnly)).Level);
        Assert.False(PlanMode.Allows(tool));
        Assert.True(PlanMode.Allows(new PullRequestStatusTool()));
    }

    [Fact]
    public async Task CreatingAPrFromTheDefaultBranchIsRefused()
    {
        InitRepo();
        Git("update-ref", "refs/remotes/origin/main", "HEAD");
        Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        var r = await new CreatePullRequestTool(null).ExecuteAsync(JsonSerializer.SerializeToElement(new { title = "x", body = "y" }), Ctx(), default);
        Assert.False(r.Ok);
        Assert.Contains("git switch -c", r.Output);
    }

    [Fact]
    public async Task CreatingAPrWithoutARemoteIsRefused()
    {
        Git("init", "-q", "-b", "main");
        var r = await new CreatePullRequestTool(null).ExecuteAsync(JsonSerializer.SerializeToElement(new { title = "x", body = "y" }), Ctx(), default);
        Assert.False(r.Ok);
        Assert.Contains("没有远端", r.Output);
    }
}
