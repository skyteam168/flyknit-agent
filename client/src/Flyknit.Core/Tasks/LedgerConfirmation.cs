using Flyknit.Core.Agent;

namespace Flyknit.Core.Tasks;

/// <summary>
/// 包在宿主的确认回调外面：问员工之前把任务记成「等确认」，有回答了再记回「进行中」。
/// 确认本身原样交给里面那层（弹卡片、超时拒绝、远程任务一律拒绝……），这里一个字不改。
/// </summary>
public sealed class LedgerConfirmation(IConfirmationHandler inner, Action<bool> waiting) : IConfirmationHandler, IBatchConfirmationHandler
{
    /// <summary>里面那层能合并（桌面端界面）才合并。</summary>
    public bool CanBatch => inner is IBatchConfirmationHandler { CanBatch: true };

    public async Task<IReadOnlyDictionary<string, ConfirmChoice>> ConfirmBatchAsync(IReadOnlyList<ConfirmRequest> requests, CancellationToken ct)
    {
        Notify(true);
        try
        {
            return await ((IBatchConfirmationHandler)inner).ConfirmBatchAsync(requests, ct);
        }
        finally
        {
            Notify(false);
        }
    }

    public async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        Notify(true);
        try
        {
            return await inner.ConfirmAsync(request, ct);
        }
        finally
        {
            Notify(false);
        }
    }

    private void Notify(bool on)
    {
        try
        {
            waiting(on);
        }
        catch (Exception)
        {
            // 记账失败不影响确认
        }
    }
}
