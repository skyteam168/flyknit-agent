using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Git;

/// <summary>开始盯一个 PR（宿主实现：存进数据库、定时查、有事通知员工、按需自动修）。</summary>
public interface IPrWatchRegistry
{
    PrWatch Watch(string conversationId, string repoDir, PrStatus status, bool autoFix);
}

/// <summary>
/// PR 相关的工具（IT 打开「开发者工具」后才有，和 git_* 一起）：建 PR、查 PR 状态、盯 PR。
/// 都通过员工电脑上的 gh 调 GitHub，用员工自己的登录。
/// </summary>
public static class PullRequestToolSet
{
    public static IReadOnlyList<ITool> Create(IPrWatchRegistry? registry) => new ITool[]
    {
        new CreatePullRequestTool(registry),
        new PullRequestStatusTool(),
        new WatchPullRequestTool(registry),
    };

    internal static string Dir(JsonElement args, ToolContext ctx) =>
        args.Str("path") is { Length: > 0 } p ? ctx.ResolvePath(p) : ctx.WorkingDirectory;

    internal static JsonObject PathProperty() => new() { ["type"] = "string", ["description"] = "仓库里的任意目录，默认工作区" };
}

/// <summary>
/// 建 PR：把当前分支推到远端，再用 gh 建 PR，默认建好后开始盯（每 50 分钟查一次 CI 和评审）。
/// 推送和建 PR 都会影响到别人（远端仓库、GitHub 上的通知），所以一律要员工确认；确认时可以选「以后自动」（写入类，不是高危）。
/// 不在默认分支上建、不强制推送。
/// </summary>
public sealed class CreatePullRequestTool : ITool
{
    public const string ToolName = "create_pull_request";
    private readonly IPrWatchRegistry? _registry;

    public CreatePullRequestTool(IPrWatchRegistry? registry) => _registry = registry;

    public string Name => ToolName;

    public string Description =>
        "把当前分支推送到远端并在 GitHub 上建拉取请求（PR）。先确认改动都已用 git_commit 提交、当前不在 main/master 上（不在就先用 run_shell 执行 git switch -c 新分支）。" +
        "title 简短说清做了什么；body 写为什么改、改了哪些地方、怎么验证的（跑了哪些测试、结果如何，没跑的如实写）。" +
        "watch=true（默认）建好后每 50 分钟自动查一次 CI 和评审，CI 失败时在这个对话里提醒并可自动修复。";

    public JsonObject Parameters
    {
        get
        {
            var schema = ToolArgs.Schema(
                ("title", "string", "PR 标题", true),
                ("body", "string", "PR 说明（Markdown）", true),
                ("base", "string", "合并到哪个分支，默认仓库的默认分支", false),
                ("draft", "boolean", "建成草稿，默认 false", false),
                ("watch", "boolean", "建好后开始盯 CI 和评审，默认 true", false),
                ("auto_fix", "boolean", "CI 失败、冲突、要求修改时自动开一轮修复（操作照常要确认），默认 true", false));
            schema["properties"]!["path"] = PullRequestToolSet.PathProperty();
            return schema;
        }
    }

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        string dir;
        try
        {
            dir = PullRequestToolSet.Dir(args, ctx);
        }
        catch (Exception)
        {
            return PolicyDecision.Blocked("目录无效");
        }
        // 先按等价命令过一遍（拦截规则、仅可查看、网络白名单），再保证至少要确认一次
        var decision = PermissionRules.ForCommand(ctx.Policy, ctx.EffectivePermission, ctx.Workspace, "git push -u origin HEAD && gh pr create", dir);
        if (ctx.NetworkAllowlist)
        {
            decision = PermissionRules.ForNetwork(decision, ctx.Policy.Network.EvaluateCommand("git push -u origin HEAD && gh pr create"));
        }
        if (decision.Level != RiskLevel.Auto)
        {
            return decision;
        }
        return PolicyDecision.Confirm("会把分支推送到远端，并在 GitHub 上建 PR（别人能看到）") with
        {
            Effect = CommandEffect.Write,
            Rule = new ApprovalCandidate(ToolName, "*", ToolName, ToolName, "*", "推送分支并建 PR"),
        };
    }

    public string Describe(JsonElement args) => $"建 PR：「{args.Str("title")}」" + (args.Bool("draft", false) ? "（草稿）" : "");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var dir = PullRequestToolSet.Dir(args, ctx);
        var title = args.Required("title").Trim();
        var body = args.Str("body").Trim();
        var baseBranch = args.Str("base").Trim();
        if (baseBranch.StartsWith('-'))
        {
            return ToolResult.Fail("base 不能以 - 开头");
        }
        try
        {
            var info = await GitRepository.InfoAsync(dir, ct);
            if (info is null)
            {
                return ToolResult.Fail($"{dir} 不在 git 仓库里");
            }
            if (!info.IsGitHub)
            {
                return ToolResult.Fail(info.Host.Length == 0
                    ? "这个仓库没有远端（origin），建不了 PR。先让员工确认要推到哪个仓库"
                    : $"远端在 {info.Host}，目前只支持 GitHub 上建 PR");
            }
            if (info.Detached || info.Branch.Length == 0)
            {
                return ToolResult.Fail("当前不在任何分支上。先用 run_shell 执行 git switch -c <新分支名> 建一个分支");
            }
            var target = baseBranch.Length > 0 ? baseBranch : info.DefaultBranch;
            if (info.Branch == target || (target.Length == 0 && info.Branch is "main" or "master"))
            {
                return ToolResult.Fail($"当前在 {info.Branch} 上。PR 要从单独的分支提：先用 run_shell 执行 git switch -c <新分支名>，再推送建 PR");
            }
            var note = info.Changed > 0 ? $"注意：还有 {info.Changed} 处没提交的改动没有包含在 PR 里。\n" : "";

            var push = await GitRunner.RunAsync(dir, new[] { "push", "-u", "origin", "HEAD" }, ct);
            if (push.ExitCode != 0)
            {
                return ToolResult.Fail("推送失败：" + SecretRedactor.Redact((push.Stderr.Trim().Length > 0 ? push.Stderr : push.Stdout).Trim()));
            }
            var url = await GhCli.CreateAsync(dir, title, body, target, info.Branch, args.Bool("draft", false), ct);
            var status = await GhCli.ViewAsync(dir, 0, ct);
            var watched = "";
            if (args.Bool("watch", true) && _registry is not null)
            {
                _registry.Watch(ctx.ConversationId, info.Root, status, args.Bool("auto_fix", true));
                watched = $"\n已开始盯这个 PR：每 {PrWatchPolicy.DefaultIntervalMinutes} 分钟查一次 CI、冲突和评审，有情况会在这个对话里提醒" +
                    (args.Bool("auto_fix", true) ? "并自动开一轮修复。" : "。");
            }
            return ToolResult.Success($"已建 PR：{url}\n{note}{status.Describe()}{watched}");
        }
        catch (GhUnavailableException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (GitMissingException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}

/// <summary>查 PR 状态（CI 每项检查、能不能合并、评审、评论数）。只读。</summary>
public sealed class PullRequestStatusTool : ITool, IParallelTool
{
    public const string ToolName = "pr_status";

    public string Name => ToolName;
    public string Description => "查看 GitHub 上一个拉取请求（PR）的状态：CI 每项检查过没过（失败的给出链接）、和目标分支有没有冲突、评审结论、评论数。不填 number 时查当前分支的 PR。只读。";

    public JsonObject Parameters
    {
        get
        {
            var schema = ToolArgs.Schema(("number", "integer", "PR 编号，默认当前分支的 PR", false));
            schema["properties"]!["path"] = PullRequestToolSet.PathProperty();
            return schema;
        }
    }

    public bool CanRunInParallel(JsonElement args) => true;
    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
    public string Describe(JsonElement args) => args.Int("number", 0) is > 0 and var n ? $"查看 PR #{n}" : "查看当前分支的 PR";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var status = await GhCli.ViewAsync(PullRequestToolSet.Dir(args, ctx), Math.Max(0, args.Int("number", 0)), ct);
            return ToolResult.Success(status.Describe());
        }
        catch (Exception ex) when (ex is GhUnavailableException or GitMissingException or InvalidOperationException or JsonException)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}

/// <summary>开始盯一个已有的 PR。只在本机记一条、定时用 gh 查，不改任何东西，所以不用确认。</summary>
public sealed class WatchPullRequestTool : ITool
{
    public const string ToolName = "watch_pull_request";
    private readonly IPrWatchRegistry? _registry;

    public WatchPullRequestTool(IPrWatchRegistry? registry) => _registry = registry;

    public string Name => ToolName;
    public string Description => "开始盯一个 GitHub PR：每 50 分钟查一次 CI、冲突和评审，有情况在这个对话里提醒员工；auto_fix=true 时自动开一轮去修（操作照常要确认）。合并或关闭后自动停止。";

    public JsonObject Parameters
    {
        get
        {
            var schema = ToolArgs.Schema(
                ("number", "integer", "PR 编号，默认当前分支的 PR", false),
                ("auto_fix", "boolean", "CI 失败、冲突、要求修改时自动开一轮修复，默认 true", false));
            schema["properties"]!["path"] = PullRequestToolSet.PathProperty();
            return schema;
        }
    }

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto("只在本机定时查看");
    public string Describe(JsonElement args) => args.Int("number", 0) is > 0 and var n ? $"开始盯 PR #{n}" : "开始盯当前分支的 PR";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (_registry is null)
        {
            return ToolResult.Fail("这里不能盯 PR（没有界面的任务里用不了）");
        }
        try
        {
            var dir = PullRequestToolSet.Dir(args, ctx);
            var status = await GhCli.ViewAsync(dir, Math.Max(0, args.Int("number", 0)), ct);
            if (!status.Open)
            {
                return ToolResult.Fail($"PR #{status.Number} 已经{status.StateText}，不用再盯");
            }
            var root = (await GitRepository.InfoAsync(dir, ct))?.Root ?? dir;
            _registry.Watch(ctx.ConversationId, root, status, args.Bool("auto_fix", true));
            return ToolResult.Success($"已开始盯 PR #{status.Number}（每 {PrWatchPolicy.DefaultIntervalMinutes} 分钟查一次）。\n{status.Describe()}");
        }
        catch (Exception ex) when (ex is GhUnavailableException or GitMissingException or InvalidOperationException or JsonException)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}
