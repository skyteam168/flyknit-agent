namespace Flyknit.Core.Agent;

/// <summary>
/// 手机发来的任务执行到需要确认的操作：同时问手机和电脑，谁先回答算谁的，另一边收起来。
/// 以前只问手机：手机 App 在后台、连接被系统掐断时根本看不到，电脑上也没有任何提示，
/// 任务就一直转圈，等满超时后按拒绝处理。
/// </summary>
public static class FirstAnswer
{
    /// <summary>谁回答的。</summary>
    public enum Source
    {
        /// <summary>电脑上点的（确认卡片或系统通知）。</summary>
        Pc,
        /// <summary>手机上点的。</summary>
        Phone,
        /// <summary>两边都没答，超时了。</summary>
        Timeout,
    }

    /// <param name="pc">电脑上的确认（界面卡片）。被取消时应当返回拒绝或抛取消。</param>
    /// <param name="phone">等手机回答：true 批准，false 拒绝，null 没有回答（出错、过期）—— 这时接着等电脑。</param>
    /// <param name="timeout">两边都不答时最多等多久，到了按拒绝处理。</param>
    public static async Task<(ConfirmChoice Choice, Source By)> RaceAsync(
        ConfirmRequest request, IConfirmationHandler pc, Func<CancellationToken, Task<bool?>> phone, TimeSpan timeout, CancellationToken ct)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        both.CancelAfter(timeout);
        var pcTask = pc.ConfirmAsync(request, both.Token);
        var phoneTask = Guard(phone, both.Token);

        var first = await Task.WhenAny(pcTask, phoneTask);
        if (first == phoneTask && await phoneTask is { } approved)
        {
            both.Cancel(); // 收起电脑上的卡片
            await Quietly(pcTask);
            return (approved ? ConfirmChoice.AllowOnce : ConfirmChoice.Reject, Source.Phone);
        }

        // 手机那边答不了（出错、过期）：接着等电脑
        ConfirmChoice choice;
        try
        {
            choice = await pcTask;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            choice = ConfirmChoice.Reject;
        }
        ct.ThrowIfCancellationRequested();
        var timedOut = both.IsCancellationRequested;
        both.Cancel(); // 停掉对手机的等待
        await Quietly(phoneTask);
        return timedOut ? (ConfirmChoice.Reject, Source.Timeout) : (choice, Source.Pc);
    }

    private static async Task<bool?> Guard(Func<CancellationToken, Task<bool?>> phone, CancellationToken ct)
    {
        try
        {
            return await phone(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }
}
