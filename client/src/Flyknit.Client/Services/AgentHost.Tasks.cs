using System;
using System.Collections.Concurrent;
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
}
