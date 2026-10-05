using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Scheduling;

namespace Flyknit.Client.Services;

/// <summary>
/// 定时任务调度：每 30 秒检查一次，到点的任务新建一个会话自动执行。
/// 调度跑在客户端，因为任务要操作这台电脑的文件和命令；程序在托盘里即可，不需要开着窗口。
/// </summary>
public sealed class ScheduleRunner : IDisposable
{
    /// <summary>错过超过这个时间就不补跑了，避免早上开机一次性跑一堆。</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(12);

    /// <summary>定时任务里需要确认的操作，等用户这么久（可以在系统通知上点允许），超时按拒绝处理。</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly AgentHost _host;
    private readonly Timer _timer;
    private int _ticking;

    /// <summary>任务列表有变化（界面刷新用）。</summary>
    public event Action? Changed;

    public ScheduleRunner(AgentHost host)
    {
        _host = host;
        _timer = new Timer(_ => _ = TickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private ScheduledTaskStore Store => _host.Store.Schedules;

    /// <summary>启动调度：先补上缺失的下次运行时间，再开始定时检查。</summary>
    public void Start()
    {
        try
        {
            var now = DateTimeOffset.Now;
            foreach (var task in Store.List())
            {
                if (!task.Enabled)
                {
                    continue;
                }
                // 没有下次时间（新任务 / 改过规则），或者错过太久，重新排期
                if (task.NextRunAt is null || now - task.NextRunAt > CatchUpWindow || !task.CatchUp && task.NextRunAt < now)
                {
                    Store.Reschedule(task, now);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("初始化定时任务失败", ex);
        }
        _timer.Change(TimeSpan.FromSeconds(10), Interval);
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }
        try
        {
            var now = DateTimeOffset.Now;
            List<ScheduledTask> due;
            try
            {
                due = Store.Due(now, CatchUpWindow);
            }
            catch (Exception ex)
            {
                Log.Warn("读取定时任务失败", ex);
                return;
            }
            foreach (var task in due)
            {
                await RunAsync(task, now, manual: false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>立即运行一次（界面上的「运行」按钮，或到点自动触发）。</summary>
    public async Task<(bool Ok, string Message)> RunAsync(ScheduledTask task, DateTimeOffset now, bool manual)
    {
        if (task.Instructions.Trim().Length == 0)
        {
            return (false, "任务内容为空");
        }
        if (_host.Events is not { } events)
        {
            return (false, "界面尚未就绪，稍后再试");
        }

        // 先把下次时间排好：这样即使这次运行失败或崩溃，也不会反复触发同一次
        if (!manual)
        {
            task.NextRunAt = task.Schedule.NextRun(now);
            if (task.Schedule.Kind == ScheduleKind.Once)
            {
                task.Enabled = false; // 一次性任务跑完就停用
            }
        }
        task.LastRunAt = now;
        task.LastStatus = "running";
        task.LastSummary = "";
        task.RunCount++;

        try
        {
            var conv = _host.Store.Create(
                ConversationMode.Agent,
                $"{task.Name} · {now:MM-dd HH:mm}",
                task.ModelId,
                task.Workspace,
                task.Permission);
            _host.Store.Rename(conv.Id, conv.Title); // 定时任务的标题固定，不让 AI 覆盖
            task.LastConversationId = conv.Id;
            Store.Save(task);
            Changed?.Invoke();

            events.Post(new { type = "conversation.updated", conversation = Bridge.ConversationDto.From(_host.Store.Get(conv.Id)!) });
            events.Post(new { type = "schedules.changed" });

            var confirm = manual && _host.Confirm is { } live
                ? live
                : new TimedConfirmation(_host.Confirm, ConfirmTimeout);

            _host.Send(conv.Id, task.Instructions, Array.Empty<Flyknit.Core.Chat.Attachment>(), null,
                _host.UiLanguage, confirm, events);
            Log.Info($"定时任务「{task.Name}」已开始运行（会话 {conv.Id}）");
            return (true, "");
        }
        catch (Exception ex)
        {
            task.LastStatus = "failed";
            task.LastSummary = ex.Message;
            Store.Save(task);
            Changed?.Invoke();
            Log.Warn($"定时任务「{task.Name}」启动失败", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>运行结束后回写结果（由 AgentHost.RunFinished 调用）。</summary>
    public void OnRunFinished(RunFinishedInfo info)
    {
        try
        {
            var task = Store.List().FirstOrDefault(t => t.LastConversationId == info.ConversationId);
            if (task is null || task.LastStatus != "running")
            {
                return;
            }
            task.LastStatus = info.StopReason switch
            {
                null => "failed",
                AgentStopReason.Completed => "ok",
                AgentStopReason.Cancelled => "stopped",
                _ => "failed",
            };
            task.LastSummary = info.Answer.Length > 300 ? info.Answer[..300] : info.Answer;
            Store.Save(task);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("回写定时任务结果失败", ex);
        }
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>
    /// 无人值守时的确认：照常弹系统通知和界面卡片，等一段时间；
    /// 没人应答就按拒绝处理，让任务继续走下去而不是一直挂着。
    /// </summary>
    private sealed class TimedConfirmation : IConfirmationHandler
    {
        private readonly IConfirmationHandler? _inner;
        private readonly TimeSpan _timeout;

        public TimedConfirmation(IConfirmationHandler? inner, TimeSpan timeout)
        {
            _inner = inner;
            _timeout = timeout;
        }

        public async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            if (_inner is null)
            {
                return ConfirmChoice.Reject;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            try
            {
                return await _inner.ConfirmAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Info($"定时任务等待确认超时（{_timeout.TotalMinutes:0} 分钟），已拒绝：{request.Summary}");
                return ConfirmChoice.Reject;
            }
        }
    }
}
