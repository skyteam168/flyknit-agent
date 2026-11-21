using System.Collections.Concurrent;

namespace Flyknit.Core.Code;

/// <summary>
/// 编码模式「带上摘要新建任务」：一个对话越聊越长时，员工新开一个任务接着做，
/// 让新任务第一轮带上旧任务的交接摘要（做过什么、改了哪些文件、还剩什么），不用背着整段历史。
///
/// 摘要在后台用现有的压缩摘要生成（服务端网关、title 之外的同一套计费），存在内存里，
/// 新任务第一句话发出时由 <see cref="EditorContextRunExtension"/> 取走，放进那一轮的参考资料，跟着那条消息存下来；
/// 只给一次。程序重启前没发第一句话的，交接就没了（员工可以再点一次）。
/// </summary>
public sealed class HandoffStore
{
    /// <summary>第一句话发出时摘要还没做好，最多等这么久；等不到就不带了（不能让员工一直干等）。</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(90);

    private sealed record Pending(string From, Task<string?> Summary, CancellationTokenSource Cancel);

    private readonly ConcurrentDictionary<string, Pending> _byConversation = new(StringComparer.Ordinal);

    /// <summary>开始给 <paramref name="to"/> 准备来自 <paramref name="from"/> 的交接摘要（已有的先取消）。</summary>
    public Task<string?> Start(string to, string from, Func<CancellationToken, Task<string?>> summarize)
    {
        Clear(to);
        var cts = new CancellationTokenSource();
        var task = Run(summarize, cts.Token);
        _byConversation[to] = new Pending(from, task, cts);
        return task;
    }

    private static async Task<string?> Run(Func<CancellationToken, Task<string?>> summarize, CancellationToken ct)
    {
        try
        {
            var text = await summarize(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public bool Has(string to) => _byConversation.ContainsKey(to);

    /// <summary>员工点了「不带了」，或者又重新选了一次。</summary>
    public void Clear(string to)
    {
        if (_byConversation.TryRemove(to, out var p))
        {
            p.Cancel.Cancel();
            p.Cancel.Dispose();
        }
    }

    /// <summary>新任务的第一句话发出：取走交接摘要（只给一次）。没有、失败、等太久返回 null。</summary>
    public async Task<(string From, string Summary)?> TakeAsync(string to, CancellationToken ct)
    {
        if (!_byConversation.TryRemove(to, out var p))
        {
            return null;
        }
        try
        {
            var text = await p.Summary.WaitAsync(MaxWait, ct).ConfigureAwait(false);
            return text is null ? null : (p.From, text);
        }
        catch (TimeoutException)
        {
            p.Cancel.Cancel();
            return null;
        }
        finally
        {
            p.Cancel.Dispose();
        }
    }
}
