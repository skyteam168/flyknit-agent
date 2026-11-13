using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Client.Bridge;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Git;
using Flyknit.Core.Security;

namespace Flyknit.Client.Services;

/// <summary>
/// 仓库和 PR（IT 打开「开发者工具」后才有）：
/// - 右侧「文件」标签上方的仓库信息：仓库、当前分支、未提交改动、领先落后；员工可以新建、切换分支；
/// - 盯 PR：每分钟看一眼有没有到点的（每个 PR 默认 50 分钟查一次），用 gh 查 CI、冲突、评审，
///   有变化弹通知、推给界面；开着自动修复时在那个对话里开一轮去修（推送等操作照常要员工确认）。
/// GitHub 走员工电脑上的 gh 和员工自己的登录，令牌不经过我们。
/// </summary>
public sealed partial class AgentHost
{
    private PrWatcher? _prWatcher;
    private Timer? _prTimer;
    private int _prChecking;

    /// <summary>PR 有情况（CI 失败、合并了……）：标题、正文、对话 ID。App 据此弹通知。</summary>
    public event Action<string, string, string>? PrNotice;

    public PrWatcher PullRequests
    {
        get
        {
            if (_prWatcher is null)
            {
                var watcher = new PrWatcher(Store.PrWatches);
                watcher.Watched += PostPr;
                _prWatcher = watcher;
            }
            return _prWatcher;
        }
    }

    private bool DeveloperToolsOn => Security.On(SecuritySettings.DeveloperTools, fallback: false);

    private void StartPrWatching() =>
        _prTimer ??= new Timer(_ => _ = CheckDuePrsAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    private async Task CheckDuePrsAsync()
    {
        // IT 关掉「开发者工具」后不再查（记录留着，打开后接着查）
        if (!DeveloperToolsOn || Interlocked.Exchange(ref _prChecking, 1) == 1)
        {
            return;
        }
        try
        {
            foreach (var outcome in await PullRequests.CheckDueAsync(_watchCts.Token))
            {
                HandlePr(outcome);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("检查 PR 失败", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _prChecking, 0);
        }
    }

    /// <summary>界面上点「立即检查」。</summary>
    public async Task<PrWatch?> CheckPrNowAsync(string id)
    {
        if (Store.PrWatches.Get(id) is not { Active: true } watch)
        {
            return Store.PrWatches.Get(id);
        }
        var outcome = await PullRequests.CheckAsync(watch, _watchCts.Token);
        HandlePr(outcome);
        return outcome.Watch;
    }

    public PrWatch? StopPrWatch(string id)
    {
        var w = PullRequests.Stop(id);
        if (w is not null)
        {
            PostPr(w);
        }
        return w;
    }

    public PrWatch? SetPrAutoFix(string id, bool on)
    {
        var w = PullRequests.SetAutoFix(id, on);
        if (w is not null)
        {
            PostPr(w);
        }
        return w;
    }

    public IReadOnlyList<PrWatch> PrWatchesOf(string conversationId) => Store.PrWatches.OfConversation(conversationId);

    private void HandlePr(PrCheckOutcome outcome)
    {
        var w = outcome.Watch;
        PostPr(w);
        if (outcome.Events.Count == 0)
        {
            return;
        }
        var text = string.Join("；", outcome.Events.Select(e => e.Text));
        Log.Info($"PR #{w.Number}（对话 {w.ConversationId}）：{text}");
        PrNotice?.Invoke($"PR #{w.Number}：{text}", w.Title, w.ConversationId);
        if (outcome.FixPrompt is { } prompt)
        {
            StartPrFix(w, prompt);
        }
    }

    /// <summary>
    /// 在那个对话里开一轮修复。界面没挂上（没人确认推送）就不开；对话正在跑就排到这一轮之后。
    /// 修复和员工自己发话一样走正常的权限和确认，不会因为是自动开的就少问一句。
    /// </summary>
    private void StartPrFix(PrWatch w, string prompt)
    {
        var conversationId = w.ConversationId;
        if (Events is not { } events || Confirm is not { } confirm || Store.Get(conversationId) is not { } conv)
        {
            return;
        }
        if (IsRunning(conversationId))
        {
            Inbox.Add(conversationId, prompt, RunInbox.Queue);
            return;
        }
        // 不是员工自己发的话：和定时任务、手机任务一样先记一份授权（权限不超过这个对话现在的权限，本机发起、不等服务端），
        // 任务账本里记成一件「PR 自动修复」的事
        var (grant, _) = IssueGrant(conv, "pr", w.Id, $"盯 PR #{w.Number} 自动修复", prompt);
        ExpectTask(Flyknit.Core.Tasks.TaskOrigins.PullRequest, conv, $"PR #{w.Number} 自动修复", w.Id, $"盯 PR #{w.Number}", grant);
        var message = NewUserMessage(null, prompt, Array.Empty<Attachment>());
        try
        {
            StartRun(conversationId, UiLanguage, confirm, events, _ => message);
        }
        catch
        {
            ForgetTask(conversationId);
            _activeGrants.TryRemove(conversationId, out _);
            throw;
        }
        events.Post(new { type = "chat.message", conversationId, message = MessageDto.From(message) });
    }

    private void PostPr(PrWatch w) => Events?.Post(new { type = "pr.updated", conversationId = w.ConversationId, watch = PrDto(w) });

    internal static object PrDto(PrWatch w) => new
    {
        id = w.Id,
        conversationId = w.ConversationId,
        number = w.Number,
        title = w.Title,
        url = w.Url,
        state = w.Last?.State ?? "OPEN",
        draft = w.Last?.Draft ?? false,
        ci = w.Last?.Ci ?? "none",
        checks = (w.Last?.Checks ?? new List<PrCheck>()).Select(c => new { name = c.Name, state = c.State, url = c.Url }).ToList(),
        mergeable = w.Last?.Mergeable ?? "UNKNOWN",
        review = w.Last?.Review ?? "",
        head = w.Last?.Head ?? "",
        @base = w.Last?.Base ?? "",
        autoFix = w.AutoFix,
        autoFixes = w.AutoFixes,
        maxAutoFixes = PrWatchPolicy.MaxAutoFixes,
        intervalMinutes = w.IntervalMinutes,
        nextCheckAt = w.Active ? w.NextCheckAt.ToString("O") : null,
        lastCheckedAt = w.LastCheckedAt?.ToString("O"),
        lastError = w.LastError,
        active = w.Active,
        endedReason = w.EndedReason,
    };

    // ---------- 聊天里 PR / Issue 链接的悬停卡片 ----------

    private RefCardCache? _refCards;

    /// <summary>
    /// 鼠标停在聊天里的 PR / Issue 链接上时查卡片内容。和盯 PR 一样走员工电脑上的 gh 和员工自己的登录；
    /// 开发者工具没开、网址不是 PR / Issue、主机不在网络白名单、gh 查不到时返回 null（界面就只显示普通链接）。
    /// </summary>
    public async Task<RefCard?> RefCardAsync(string url)
    {
        if (!DeveloperToolsOn || RefLink.Parse(url) is not { } link || !_policy.Network.IsAllowed(link.Host))
        {
            return null;
        }
        _refCards ??= new RefCardCache(GhCli.CardAsync);
        try
        {
            return await _refCards.GetAsync(link, _watchCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ---------- 仓库 ----------

    /// <summary>这个对话工作区所在的仓库；不是仓库、没装 git 返回 null。</summary>
    public async Task<RepoInfo?> RepoInfoAsync(string conversationId)
    {
        if (!DeveloperToolsOn || WorkspaceOf(conversationId) is not { } dir)
        {
            return null;
        }
        try
        {
            return await GitRepository.InfoAsync(dir, _watchCts.Token);
        }
        catch (Flyknit.Core.Tools.GitMissingException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> BranchesAsync(string conversationId) =>
        DeveloperToolsOn && WorkspaceOf(conversationId) is { } dir
            ? await GitRepository.BranchesAsync(dir, _watchCts.Token)
            : Array.Empty<string>();

    /// <summary>员工在界面上新建 / 切换分支。对话正在跑时不让切（AI 正在改的文件会被换掉）。</summary>
    public async Task<(bool Ok, string Message)> ChangeBranchAsync(string conversationId, string name, bool create)
    {
        if (!DeveloperToolsOn || WorkspaceOf(conversationId) is not { } dir)
        {
            return (false, "这个对话没有工作区，或没有开启开发者工具");
        }
        if (IsRunning(conversationId))
        {
            return (false, "任务正在进行，等它结束再切换分支");
        }
        return create
            ? await GitRepository.CreateBranchAsync(dir, name, checkout: true, _watchCts.Token)
            : await GitRepository.SwitchAsync(dir, name, _watchCts.Token);
    }
}
