namespace Flyknit.Core.Agent;

/// <summary>
/// 写操作全局排队（docs/c2-router.md 3.3）：一句话拆开的几件事同时做时，读可以并行，写文件、跑会改东西的命令一次一个。
/// 第 4 期（隔离）之前本机上的写操作就是这个约定。锁在确认之后、执行之前拿，等员工确认时不占着；
/// 任务被停止时等锁的那一步直接放弃。
/// </summary>
public sealed class WriteGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        return new Releaser(_lock);
    }

    /// <summary>现在有没有写操作在执行（测试和界面用）。</summary>
    public bool Busy => _lock.CurrentCount == 0;

    private sealed class Releaser(SemaphoreSlim s) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                s.Release();
            }
        }
    }
}
