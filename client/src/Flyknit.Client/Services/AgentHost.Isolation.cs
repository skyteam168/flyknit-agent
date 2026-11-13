using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Git;
using Flyknit.Core.Security;
using Flyknit.Core.Tasks;

namespace Flyknit.Client.Services;

/// <summary>
/// 隔离并行写（VNext 第 4 期，docs/c4-parallel-writes.md）：一句话拆开的几件、会改代码时，各在自己的沙箱或 git worktree 里改，各出一个 flyknit/ 分支和 PR。
/// - 原对话开着沙箱（且 IT 允许）：每件一个容器，真正同时改；出口照旧（只推任务分支、不强推、不推默认分支）。
/// - 否则：每件一个 worktree（放在 %LOCALAPPDATA%\Flyknit\worktrees，不在员工工作区里），本机工具照常确认、写操作仍一次一个。
/// 安全中心「隔离并行写」关着（默认）、工作区不是 git 仓库、同时做 = 1 时都不用，和第 2 期一样。
/// </summary>
public sealed partial class AgentHost
{
    private const string IsolationSandbox = "sandbox";
    private const string IsolationWorktree = "worktree";

    /// <summary>PR 合并 / 关闭后，或这么久没动静，就清掉 worktree。</summary>
    public static readonly TimeSpan WorktreeIdleLimit = TimeSpan.FromDays(7);

    private static string WorktreeRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flyknit", "worktrees");

    private Timer? _worktreeTimer;
    private int _worktreeCleaning;

    /// <summary>
    /// 这个工作区拆开时用哪种隔离：沙箱 / worktree / 不隔离（空）。
    /// 要 IT 打开「隔离并行写」「分开做」「开发者工具」、「同时做几件」≥ 2，工作区是 git 仓库。
    /// </summary>
    private async Task<string> IsolationForAsync(string? workspace, bool sandbox, CancellationToken ct)
    {
        if (!Security.On(SecuritySettings.IsolatedWrites, fallback: false) || !RouterSplitOn || RouterParallel < 2 || !DeveloperToolsOn)
        {
            return "";
        }
        try
        {
            var dir = _settings.ResolveWorkspace(workspace);
            if (await GitRepository.InfoAsync(dir, ct) is null)
            {
                return "";
            }
            return sandbox && SandboxSwitchOn ? IsolationSandbox : IsolationWorktree;
        }
        catch (Exception ex)
        {
            Log.Warn("判断隔离方式失败，按不隔离处理", ex);
            return "";
        }
    }

    /// <summary>拆分建议卡上说明会怎么隔离（界面据此写一句话）。</summary>
    public Task<string> IsolationHintAsync(string? workspace, bool sandbox) => IsolationForAsync(workspace, sandbox, _watchCts.Token);

    /// <summary>一次建好 n 个 worktree；中途失败就把建好的删掉、返回 null（这次不隔离）。</summary>
    private async Task<IReadOnlyList<WorktreeManager.Created>?> CreateWorktreesAsync(string? workspace, int n)
    {
        var dir = _settings.ResolveWorkspace(workspace);
        var made = new List<WorktreeManager.Created>();
        try
        {
            for (var i = 0; i < n; i++)
            {
                made.Add(await WorktreeManager.CreateAsync(dir, WorktreeRoot, _watchCts.Token));
            }
            return made;
        }
        catch (Exception ex)
        {
            Log.Warn("建独立工作副本失败，这次不隔离", ex);
            foreach (var w in made)
            {
                try
                {
                    await WorktreeManager.RemoveAsync(w.RepoRoot, w.Path, CancellationToken.None);
                }
                catch (Exception)
                {
                    // 删不掉的下次清理再试
                }
            }
            return null;
        }
    }

    /// <summary>这个对话在 worktree 里：只能推它自己的分支（ToolContext.IsolatedBranch）。</summary>
    private string IsolatedBranchFor(string conversationId)
    {
        try
        {
            return Store.Worktrees.ActiveFor(conversationId)?.Branch ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>给拆开的那一件的说明：在哪改、做完怎么交回。</summary>
    private string IsolationPrompt(string conversationId)
    {
        if (Store.Worktrees.ActiveFor(conversationId) is { } w)
        {
            return $"你在一个独立的工作副本里做这件事（目录 {w.Path}，分支 {w.Branch}），只在这里改。做完后用 git 提交，再用 create_pull_request 建一个 PR，标题写清这一件做了什么。";
        }
        if (Store.Get(conversationId) is { Sandbox: true })
        {
            return "你在一个独立的沙箱里做这件事，只改这件需要改的。做完后提交，再用 create_pull_request 建一个 PR，标题写清这一件做了什么。";
        }
        return "";
    }

    /// <summary>拆开的每一件：隔离方式、分支、PR 链接（汇总卡、汇总消息用）。</summary>
    private (string Isolation, string Branch, string PrUrl, string PrId) IsolationOf(string conversationId)
    {
        var w = Store.Worktrees.ActiveFor(conversationId);
        var pr = Store.PrWatches.OfConversation(conversationId).FirstOrDefault();
        var isolation = w is not null ? IsolationWorktree : Store.Get(conversationId) is { Sandbox: true } ? IsolationSandbox : "";
        return (isolation, w?.Branch ?? "", pr?.Url ?? "", pr?.Id ?? "");
    }

    /// <summary>
    /// 汇总里的分支 / PR 和「改到同一个文件」提示。worktree 的几件在本地比较（git diff --name-only）；
    /// 沙箱的分支在容器里，冲突交给 GitHub 的 PR 页面判断。
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> PerChild, IReadOnlyList<string> Overlaps)> IsolationReportAsync(IReadOnlyList<(string ConversationId, string Label)> children)
    {
        var per = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (conv, label) in children)
        {
            var (_, branch, prUrl, _) = IsolationOf(conv);
            var parts = new List<string>();
            if (branch.Length > 0)
            {
                parts.Add("分支 `" + branch + "`");
            }
            if (prUrl.Length > 0)
            {
                parts.Add("PR " + prUrl);
            }
            if (parts.Count > 0)
            {
                per[conv] = string.Join(" · ", parts);
            }
            if (Store.Worktrees.ActiveFor(conv) is { } w)
            {
                try
                {
                    files[label] = await ChangeOverlap.FilesChangedAsync(w.RepoDir, w.BaseCommit, w.Branch, _watchCts.Token);
                }
                catch (Exception ex)
                {
                    Log.Warn("比较改动文件失败", ex);
                }
            }
        }
        var overlaps = ChangeOverlap.Find(files)
            .Select(kv => $"⚠️ `{kv.Key}` 被 {string.Join("、", kv.Value)} 都改了，合并时可能冲突")
            .ToList();
        return (per, overlaps);
    }

    private void AuditIsolation(string conversationId, string what, string detail)
    {
        try
        {
            _auditSink.Record(new AuditEntry
            {
                ConversationId = conversationId,
                TaskId = Ledger.TaskIdOf(conversationId) ?? "",
                ToolName = "worktree",
                Actor = "router",
                Risk = "auto",
                Decision = "auto",
                Status = what,
                Summary = detail,
                Scene = Scenes.Agent,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("记录工作副本审计失败", ex);
        }
    }

    // ---------- 清理 ----------

    private void StartWorktreeCleanup() =>
        _worktreeTimer ??= new Timer(_ => _ = CleanupWorktreesAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));

    /// <summary>
    /// 清理：PR 合并 / 关闭了，或者 7 天没动静（对话没更新、也没有在盯的 PR）的 worktree 删掉；
    /// 有未提交的改动不删，在那个对话里留一句说明（每次启动最多说一次）。分支保留。
    /// </summary>
    private async Task CleanupWorktreesAsync()
    {
        if (Interlocked.Exchange(ref _worktreeCleaning, 1) == 1)
        {
            return;
        }
        try
        {
            foreach (var w in Store.Worktrees.Active())
            {
                if (IsRunning(w.ConversationId))
                {
                    continue;
                }
                var watches = Store.PrWatches.OfConversation(w.ConversationId);
                var prEnded = watches.Count > 0 && watches.All(x => !x.Active);
                var conv = Store.Get(w.ConversationId);
                var idle = DateTimeOffset.Now - (conv?.UpdatedAt ?? w.CreatedAt) > WorktreeIdleLimit && watches.All(x => !x.Active);
                if (!prEnded && !idle && conv is not null)
                {
                    continue;
                }
                var (removed, reason) = await WorktreeManager.RemoveAsync(w.RepoDir, w.Path, _watchCts.Token);
                if (removed)
                {
                    Store.Worktrees.MarkRemoved(w.Id);
                    AuditIsolation(w.ConversationId, "worktree_removed", $"{w.Branch} {w.Path}");
                    Log.Info($"清理了独立工作副本 {w.Path}（分支 {w.Branch} 保留）");
                }
                else if (conv is not null && _worktreeNoticed.Add(w.Id))
                {
                    Store.AddMessages(w.ConversationId, new[]
                    {
                        ChatMessage.Assistant($"这件事的独立工作副本（{w.Path}）里还有没提交的改动，所以没有自动删除。需要的话提交或丢弃后，它会在下次清理时删掉。"),
                    });
                    Log.Info($"独立工作副本 {w.Path} 没有删除：{reason}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("清理独立工作副本失败", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _worktreeCleaning, 0);
        }
    }

    private readonly HashSet<string> _worktreeNoticed = new(StringComparer.Ordinal);
}
