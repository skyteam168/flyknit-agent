using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Flyknit.Core.Security;
using Flyknit.Core.Tasks;

namespace Flyknit.Client.Services;

/// <summary>
/// 任务账本（VNext 第 1 期，docs/c1-unified-tasks.md）：各入口在已有的节点上登记「这是一件新的事」，
/// 一轮的开始 / 结束在 RunAsync 里记。账本只记录，不放宽也不收紧任何权限（授权照旧由 TaskGrant 封顶）。
/// </summary>
public sealed partial class AgentHost
{
    /// <summary>下一轮的种类（长任务自动续跑时标上），RunAsync 开始时取走。</summary>
    private readonly ConcurrentDictionary<string, string> _nextRunKind = new(StringComparer.Ordinal);

    /// <summary>
    /// 入口登记：这个对话下一次开始跑时开新任务（手机、钉钉、企业微信、指令中心、定时任务、文件夹监视、事件、PR 自动修复）。
    /// 员工自己在窗口里说话不用登记：没登记的按「员工本人」的一串记。
    /// </summary>
    public void ExpectTask(string origin, Flyknit.Core.Storage.Conversation conv, string title, string reference, string requestedBy, TaskGrant? grant)
    {
        Ledger.Expect(new TriggerEvent(origin, conv.Id, title, reference, requestedBy, grant?.Id));
    }

    /// <summary>登记了但没跑起来（服务端拒收、出错）：撤掉登记。</summary>
    public void ForgetTask(string conversationId) => Ledger.Forget(conversationId);

    /// <summary>安全中心「单个任务的 token 上限」（万 tokens，0 = 不限），随配置刷新。新任务按开始时的值记。</summary>
    private void RefreshTaskBudget()
    {
        var wan = Security.Number(SecuritySettings.TaskTokenBudget, 0);
        Ledger.DefaultBudget = wan <= 0 ? 0 : wan * 10_000L;
    }

    /// <summary>任务中心：进行中 / 等确认 / 中断 / 最近结束的任务，带来源、发起人、授权、轮数、token 用量（/ 预算）。</summary>
    public IReadOnlyList<object> TasksList(int recentFinished = 50)
    {
        var grants = new Dictionary<string, TaskGrant?>(StringComparer.Ordinal);
        return Store.Tasks.List(recentFinished).Select(task =>
        {
            TaskGrant? grant = null;
            if (task.GrantId is { Length: > 0 } gid && !grants.TryGetValue(gid, out grant))
            {
                grant = grants[gid] = Store.Grants.Get(gid);
            }
            return (object)new
            {
                id = task.Id,
                conversationId = task.ConversationId,
                conversationTitle = Store.Get(task.ConversationId)?.Title ?? "",
                origin = task.Origin,
                title = task.Title,
                requestedBy = task.RequestedBy,
                state = TaskStateMachine.ToText(task.State),
                stopReason = task.StopReason,
                runs = task.Runs,
                tokensUsed = task.TokensUsed,
                tokenBudget = task.TokenBudget,
                createdAt = task.CreatedAt,
                startedAt = task.StartedAt,
                finishedAt = task.FinishedAt,
                // 正在跑的那一个任务（同一个对话里只有一个）：界面据此合上实时进度、暂停按钮
                live = IsRunning(task.ConversationId) && Ledger.TaskIdOf(task.ConversationId) == task.Id,
                grant = grant is null ? null : new
                {
                    permission = PermissionModes.ToText(grant.Permission),
                    workspace = grant.Workspace,
                },
            };
        }).ToList();
    }

    /// <summary>
    /// 任务中心按任务停止：正在跑的走正常的「停止」（和对话里点停止一样，已发出的工具调用照常补结果）；
    /// 中断了没续跑的、还没开始的直接记成停止（中断的顺带不再提示续跑）。
    /// </summary>
    public bool StopTask(string taskId)
    {
        if (Store.Tasks.Get(taskId) is not { } task || TaskStateMachine.IsFinal(task.State))
        {
            return false;
        }
        var conv = task.ConversationId;
        if (IsRunning(conv) && Ledger.TaskIdOf(conv) == taskId)
        {
            Stop(conv);
            return true;
        }
        var wasInterrupted = task.State == TaskState.Interrupted;
        if (!Ledger.StopIdle(taskId, "员工在任务中心停止"))
        {
            return false;
        }
        if (wasInterrupted && !IsRunning(conv))
        {
            MarkRunEnded(conv);
        }
        return true;
    }
}
