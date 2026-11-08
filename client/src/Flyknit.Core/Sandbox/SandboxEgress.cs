using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Git;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Sandbox;

/// <summary>出口检查的结果：能推返回 null，不能推返回原因。</summary>
public static class SandboxEgressRules
{
    public const int MaxChangedFiles = 2000;

    /// <summary>只有任务分支能推：flyknit/ 开头、不是基准分支、不是 main / master。</summary>
    public static string? CheckBranch(SandboxSession s) =>
        !s.TaskBranch.StartsWith("flyknit/", StringComparison.Ordinal) ? $"只能推送沙箱的任务分支（flyknit/…），{s.TaskBranch} 不行"
        : s.TaskBranch == s.BaseBranch || s.TaskBranch is "main" or "master" ? "不能推送到默认分支"
        : null;

    /// <summary>改了哪些文件：CI 配置默认不许改（IT 可以打开），一次改太多文件也不推。</summary>
    public static string? CheckFiles(IReadOnlyList<string> changed, bool allowCi)
    {
        if (changed.Count == 0)
        {
            return "任务分支上没有新的提交。先用 git_commit 提交改动";
        }
        if (changed.Count > MaxChangedFiles)
        {
            return $"这次改了 {changed.Count} 个文件，超过 {MaxChangedFiles} 个，不自动推送。请员工检查";
        }
        if (!allowCi)
        {
            var ci = changed.Where(IsCiConfig).ToList();
            if (ci.Count > 0)
            {
                return $"改动里有 CI 配置（{string.Join("、", ci.Take(5))}），IT 没有允许沙箱改 CI 配置。把这些改动撤掉再提交，或者请员工在本机处理";
            }
        }
        return null;
    }

    public static bool IsCiConfig(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        return p.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
            || p.Equals(".gitlab-ci.yml", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(".circleci/", StringComparison.OrdinalIgnoreCase)
            || p.Equals("azure-pipelines.yml", StringComparison.OrdinalIgnoreCase)
            || p.Equals("Jenkinsfile", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 沙箱的出口：把任务分支推到 GitHub、建 PR、开始盯（同名工具 create_pull_request，模型照平常的用法用）。
///
/// 在容器外做，用员工自己的 git 凭证和 gh 登录（令牌从不进容器）：
/// 1. 从容器导出任务分支（git bundle），在员工电脑的临时裸仓库里导入——导入、推送都关掉 hooks，仓库里的钩子不会在员工电脑上执行；
/// 2. 检查：只能是任务分支、不能是默认分支；不强推（没有 +，远端已有且不能快进时 git 自己会拒绝）；CI 配置默认不许改；
/// 3. 推送、gh pr create、开始盯 PR。
/// 不实现 <see cref="ISandboxedTool"/>：读到疑似注入的内容后，这一步照样要员工确认（污染模式）。
/// </summary>
public sealed class SandboxCreatePullRequest : ITool
{
    private readonly DockerSandbox _sandbox;
    private readonly Func<SandboxSession> _session;
    private readonly SandboxOptions _options;
    private readonly IPrWatchRegistry? _registry;

    public SandboxCreatePullRequest(DockerSandbox sandbox, Func<SandboxSession> session, SandboxOptions options, IPrWatchRegistry? registry)
    {
        _sandbox = sandbox;
        _session = session;
        _options = options;
        _registry = registry;
    }

    public string Name => CreatePullRequestTool.ToolName;

    public string Description =>
        "把沙箱里的任务分支推到 GitHub 并建 PR（只能推任务分支，不能推 main，不能强推）。先用 git_commit 把改动都提交。" +
        "title 简短说清做了什么；body 写为什么改、改了哪些地方、怎么验证的（跑了哪些测试、结果如何，没跑的如实写）。" +
        "建好后默认每 50 分钟查一次 CI 和评审，CI 失败时自动在这个对话里接着修。";

    public JsonObject Parameters => ToolArgs.Schema(
        ("title", "string", "PR 标题", true),
        ("body", "string", "PR 说明（Markdown）", true),
        ("draft", "boolean", "建成草稿，默认 false", false),
        ("watch", "boolean", "建好后开始盯 CI 和评审，默认 true", false),
        ("auto_fix", "boolean", "CI 失败、冲突、要求修改时自动开一轮修复，默认 true", false));

    /// <summary>只推任务分支、经过出口检查：沙箱任务里不用确认（污染模式下 Agent 循环会改成要确认）。</summary>
    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto("推送沙箱任务分支并建 PR") with { Effect = CommandEffect.Write };

    public string Describe(JsonElement args) => $"推送任务分支并建 PR：「{args.Str("title")}」";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var s = _session();
        if (SandboxEgressRules.CheckBranch(s) is { } branchError)
        {
            return ToolResult.Fail(branchError);
        }
        if (s.Owner.Length == 0 || s.RemoteUrl.Length == 0)
        {
            return ToolResult.Fail("这个仓库没有 GitHub 远端，建不了 PR");
        }
        var work = Path.Combine(Path.GetTempPath(), "flyknit-sandbox", "egress-" + Guid.NewGuid().ToString("N")[..8]);
        var noHooks = Path.Combine(work, "no-hooks");
        var bare = Path.Combine(work, "repo.git");
        string? bundle = null;
        try
        {
            Directory.CreateDirectory(noHooks);
            bundle = await _sandbox.ExportBranchAsync(s, ct);
            Git(await GitRunner.RunAsync(work, new[] { "init", "--bare", "--quiet", bare }, ct), "准备出口仓库失败");
            var hooks = new[] { "-c", $"core.hooksPath={noHooks}" };
            Git(await GitRunner.RunAsync(bare, hooks.Concat(new[] { "fetch", "--quiet", bundle, $"refs/heads/{s.TaskBranch}:refs/heads/{s.TaskBranch}" }), ct), "导入任务分支失败");

            // 这次相对开始时改了哪些文件
            var range = s.BaseCommit.Length > 0 ? $"{s.BaseCommit}..refs/heads/{s.TaskBranch}" : $"refs/heads/{s.TaskBranch}";
            var diff = await GitRunner.RunAsync(bare, new[] { "diff", "--name-only", s.BaseCommit.Length > 0 ? s.BaseCommit : "4b825dc642cb6eb9a060e54bf8d69288fbee4904", $"refs/heads/{s.TaskBranch}" }, ct);
            if (diff.ExitCode != 0)
            {
                // bundle 只带任务分支能到达的提交，基准提交一定在里面；出错说明分支被改写过了
                return ToolResult.Fail("算不出任务分支的改动（分支历史可能被改写过）。不要 rebase 或重置到基准之前");
            }
            var changed = diff.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (SandboxEgressRules.CheckFiles(changed, _options.AllowCiChanges) is { } fileError)
            {
                return ToolResult.Fail(fileError);
            }
            var commits = (await GitRunner.RunAsync(bare, new[] { "log", "--oneline", range }, ct)).Stdout.Trim();

            // 推送：没有 +，不会强推；远端已有这个分支且不能快进时 git 自己拒绝
            var push = await GitRunner.RunAsync(bare, hooks.Concat(new[] { "push", "--quiet", s.RemoteUrl, $"refs/heads/{s.TaskBranch}:refs/heads/{s.TaskBranch}" }), ct);
            if (push.ExitCode != 0)
            {
                return ToolResult.Fail("推送失败：" + SecretRedactor.Redact((push.Stderr.Trim().Length > 0 ? push.Stderr : push.Stdout).Trim()) +
                    "\n（用的是员工电脑上的 git 登录。没登录过 GitHub 的话，请员工先在命令行 git push 一次或运行 gh auth login）");
            }
            var url = await GhCli.CreateAsync(s.RepoRoot, args.Required("title").Trim(), args.Str("body").Trim(), s.BaseBranch, s.TaskBranch,
                args.Bool("draft", false), ct, repo: $"{s.Owner}/{s.Name}");
            var number = int.TryParse(url.TrimEnd('/').Split('/').Last(), out var n) ? n : 0;
            var text = $"已推送 {s.TaskBranch}（{changed.Length} 个文件）并建 PR：{url}\n提交：\n{commits}";
            if (number > 0 && args.Bool("watch", true) && _registry is not null)
            {
                try
                {
                    var status = await GhCli.ViewAsync(s.RepoRoot, number, ct);
                    _registry.Watch(ctx.ConversationId, s.RepoRoot, status, args.Bool("auto_fix", true));
                    text += $"\n已开始盯这个 PR（每 {PrWatchPolicy.DefaultIntervalMinutes} 分钟查一次 CI 和评审）。";
                }
                catch (Exception ex) when (ex is GhUnavailableException or InvalidOperationException or JsonException)
                {
                    text += $"\n没能开始盯 PR：{ex.Message}";
                }
            }
            return ToolResult.Success(text);
        }
        catch (Exception ex) when (ex is SandboxException or GhUnavailableException or GitMissingException or InvalidOperationException)
        {
            return ToolResult.Fail(ex.Message);
        }
        finally
        {
            if (bundle is not null)
            {
                DockerSandbox.TryDelete(Path.GetDirectoryName(bundle)!);
            }
            DockerSandbox.TryDelete(work);
        }
    }

    private static void Git(GitRunner.Result r, string what)
    {
        if (r.ExitCode != 0)
        {
            throw new SandboxException($"{what}：{(r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim()}");
        }
    }
}
