using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;

namespace Flyknit.Client.Services;

/// <summary>
/// 任务授权记录：手机、钉钉 / 企业微信、IT 指令、定时任务、事件触发的任务开始前记一份「谁让做、做到哪」
/// （见 <see cref="TaskGrant"/>）。这一轮运行的权限按授权封顶、工作区用授权里的，审计每一条带上授权编号。
/// </summary>
public sealed partial class AgentHost
{
    /// <summary>正在按授权跑的对话 → 这一轮的授权。员工在电脑上自己发的消息不在这里。</summary>
    private readonly ConcurrentDictionary<string, TaskGrant> _activeGrants = new(StringComparer.Ordinal);

    public TaskGrant? ActiveGrant(string conversationId) => _activeGrants.TryGetValue(conversationId, out var g) ? g : null;

    /// <summary>
    /// 起一份授权并让这个对话接下来的一轮按它跑。权限上限：这个对话上一份授权和对话现在的权限里小的那个。
    /// 通道和指令中心的任务要等服务端核对：服务端明确拒收（4xx）返回原因，任务不该执行；
    /// 连不上或老服务端（404）照样执行，本机的记录照存。
    /// </summary>
    public async Task<(TaskGrant Grant, string? Refused)> IssueGrantAsync(Conversation conv, string origin, string reference,
        string requestedBy, string instruction, CancellationToken ct)
    {
        var (grant, report) = IssueGrant(conv, origin, reference, requestedBy, instruction, ct);
        if (origin is not ("mobile" or "dingtalk" or "wecom" or "instruction"))
        {
            return (grant, null);
        }
        var code = await report;
        if (code is >= 400 and < 500 and not 404)
        {
            _activeGrants.TryRemove(conv.Id, out _);
            Log.Warn($"服务端拒收任务授权 {grant.Id}（{origin} #{reference}，HTTP {code}），不执行");
            return (grant, "服务端核对不通过：这条任务不是发给这台电脑的，没有执行");
        }
        return (grant, null);
    }

    /// <summary>本机发起的（定时任务、事件触发）：记下、后台上报，不等服务端。</summary>
    public (TaskGrant Grant, Task<int> Report) IssueGrant(Conversation conv, string origin, string reference,
        string requestedBy, string instruction, CancellationToken ct = default)
    {
        var previous = Store.Grants.Latest(conv.Id);
        var workspace = _settings.ResolveWorkspace(conv.Workspace);
        var grant = TaskGrant.Issue(origin, conv.Id, reference, requestedBy, instruction, conv.Permission, workspace, previous);
        Store.Grants.Add(grant);
        _activeGrants[conv.Id] = grant;
        var report = Task.Run(async () =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                return await Server.ReportGrantAsync(grant, cts.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"上报任务授权 {grant.Id} 失败：{ex.Message}");
                return 0;
            }
        }, CancellationToken.None);
        return (grant, report);
    }

    /// <summary>这一轮跑完：授权不再用于这个对话后面的运行（员工在电脑上接着说话不受限制）。</summary>
    public void EndGrant(string conversationId, TaskGrant grant)
    {
        _activeGrants.TryRemove(new System.Collections.Generic.KeyValuePair<string, TaskGrant>(conversationId, grant));
    }
}
