using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Gateway;

namespace Flyknit.Client.Services;

/// <summary>审计记录先放入队列，每 20 秒批量上报；上报失败保留在队列中稍后重试。</summary>
public sealed class AuditQueue : IAuditSink, IDisposable
{
    private const int MaxQueued = 5000;
    private readonly ConcurrentQueue<AuditEntry> _queue = new();
    private readonly FlyknitServerClient _server;
    private readonly Timer _timer;
    private int _flushing;

    public AuditQueue(FlyknitServerClient server)
    {
        _server = server;
        _timer = new Timer(_ => _ = FlushAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    public void Record(AuditEntry entry)
    {
        _queue.Enqueue(entry);
        while (_queue.Count > MaxQueued && _queue.TryDequeue(out _))
        {
        }
        if (entry.Decision == "blocked")
        {
            _ = FlushAsync(); // 被阻止的危险命令尽快上报
        }
    }

    public async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1)
        {
            return;
        }
        try
        {
            while (!_queue.IsEmpty)
            {
                var batch = new List<AuditEntry>();
                while (batch.Count < 200 && _queue.TryPeek(out var item))
                {
                    batch.Add(item);
                    _queue.TryDequeue(out _);
                }
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await _server.ReportAuditAsync(batch, cts.Token);
                }
                catch (Exception ex)
                {
                    foreach (var item in batch)
                    {
                        _queue.Enqueue(item);
                    }
                    Log.Warn("审计上报失败，稍后重试", ex);
                    return;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
