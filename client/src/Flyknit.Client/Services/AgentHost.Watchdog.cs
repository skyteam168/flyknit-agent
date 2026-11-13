using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Agent;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tasks;
using Flyknit.Core.Watchdog;

namespace Flyknit.Client.Services;

/// <summary>
/// 看门狗（VNext 第 3 期，docs/c3-watchdog.md）：每分钟看一眼正在跑的任务有没有进展；一直在动却没进展时请便宜模型判断，
/// 卡住 / 跑偏先提醒 AI 并通知员工，提醒后仍不行且 IT 开了自动停止才走正常的停止。
/// 安全中心「看门狗」关着（默认）时完全不介入。看门狗只会提醒和停止，不批准、不重试、不改权限。
///
/// 另外，有任务在跑时每 2 分钟给服务端发一次心跳（只报任务编号和来源），后台和手机端据此看出电脑是不是失联了。
/// </summary>
public sealed partial class AgentHost
{
    private sealed class Watch
    {
        public required string TaskId { get; init; }
        public required ProgressSignals Signals { get; init; }
        public WatchdogPolicy Policy { get; } = new();
        public WatchJudgement? Last { get; set; }
        /// <summary>界面上显示的：空 / reminded / notified / stopped / dismissed</summary>
        public string Stage { get; set; } = "";
    }

    private readonly ConcurrentDictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _advisories = new(StringComparer.Ordinal);
    private Timer? _watchdogTimer;
    private int _watchdogBusy;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private bool _heartbeatHadTasks;

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

    /// <summary>看门狗提醒 / 停下了一个任务：标题、正文、对话 ID。App 据此弹系统通知。</summary>
    public event Action<string, string, string>? WatchdogNotice;

    private bool WatchdogOn => Security.On(SecuritySettings.Watchdog, fallback: false);

    /// <summary>
    /// 一轮开始时调：看门狗开着就把这一轮的事件顺带记成进展信号（同一个任务跨轮共用一份，换了任务重新开始）。
    /// 关着时原样返回，什么都不加。
    /// </summary>
    private IAgentObserver WatchObserver(string conversationId, string? taskId, string goal, IAgentObserver inner)
    {
        StartWatchdog(); // 心跳不管看门狗开没开都要发
        if (!WatchdogOn || taskId is not { Length: > 0 })
        {
            _watches.TryRemove(conversationId, out _);
            return inner;
        }
        var w = _watches.AddOrUpdate(conversationId,
            _ => new Watch { TaskId = taskId, Signals = new ProgressSignals(goal) },
            (_, old) => old.TaskId == taskId ? old : new Watch { TaskId = taskId, Signals = new ProgressSignals(goal) });
        w.Signals.ResetHolds();
        return new WatchdogObserver(inner, w.Signals);
    }

    /// <summary>给 AgentLoop：取走这个对话待附上的看门狗提醒。</summary>
    private Func<string?> AdvisoryFor(string conversationId) => () => _advisories.TryRemove(conversationId, out var text) ? text : null;

    /// <summary>暂停 / 继续：暂停期间不算没进展。</summary>
    private void WatchHold(string conversationId, bool hold)
    {
        if (_watches.TryGetValue(conversationId, out var w))
        {
            if (hold)
            {
                w.Signals.Hold();
            }
            else
            {
                w.Signals.Release();
            }
        }
    }

    /// <summary>这件事结束了（没有接着跑的了）：丢掉它的看门狗状态。</summary>
    private void EndWatch(string conversationId)
    {
        _watches.TryRemove(conversationId, out _);
        _advisories.TryRemove(conversationId, out _);
    }

    /// <summary>任务中心点「不用管」：这个任务后面不再提醒。</summary>
    public bool DismissWatchdog(string taskId)
    {
        var hit = _watches.FirstOrDefault(x => x.Value.TaskId == taskId);
        if (hit.Value is not { } w)
        {
            return false;
        }
        w.Policy.Dismiss();
        w.Stage = "dismissed";
        _advisories.TryRemove(hit.Key, out _);
        PostWatch(hit.Key, w);
        return true;
    }

    /// <summary>任务中心卡片上显示的看门狗状态（没提醒过为 null）。</summary>
    private object? WatchdogDto(string conversationId, string taskId) =>
        _watches.TryGetValue(conversationId, out var w) && w.TaskId == taskId && w.Stage.Length > 0
            ? new { stage = w.Stage, verdict = w.Last?.Verdict.ToString().ToLowerInvariant() ?? "", reason = w.Last?.Reason ?? "" }
            : null;

    private void StartWatchdog() =>
        _watchdogTimer ??= new Timer(_ => _ = WatchdogTickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    private async Task WatchdogTickAsync()
    {
        if (Interlocked.Exchange(ref _watchdogBusy, 1) == 1)
        {
            return;
        }
        try
        {
            await HeartbeatIfDueAsync();
            if (!WatchdogOn)
            {
                return;
            }
            var minutes = Math.Clamp(Security.Number(SecuritySettings.WatchdogMinutes, 10), 5, 60);
            var autoStop = Security.On(SecuritySettings.WatchdogAutoStop, fallback: false);
            foreach (var (conversationId, w) in _watches.ToArray())
            {
                if (!IsRunning(conversationId))
                {
                    continue;
                }
                var conv = Store.Get(conversationId);
                var threshold = TimeSpan.FromMinutes(minutes * (LongModeOn(conv) ? 2 : 1));
                var now = DateTimeOffset.Now;
                if (!w.Policy.ShouldJudge(w.Signals.Suspicious(threshold), now))
                {
                    continue;
                }
                var used = Store.Tasks.Get(w.TaskId)?.TokensUsed ?? 0;
                var judgement = await WatchdogJudge.JudgeAsync(Server, w.Signals, used, _watchCts.Token);
                if (!IsRunning(conversationId) || Ledger.TaskIdOf(conversationId) != w.TaskId)
                {
                    continue; // 判断的这一会儿任务已经结束了
                }
                w.Last = judgement;
                var action = w.Policy.OnVerdict(judgement?.Verdict, autoStop, now);
                Log.Info($"看门狗：对话 {conversationId} 任务 {w.TaskId} {(judgement is null ? "模型判断失败" : judgement.Verdict.ToString())} → {action}");
                Act(conversationId, conv?.Title ?? "", w, action, judgement);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("看门狗检查失败", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _watchdogBusy, 0);
        }
    }

    private void Act(string conversationId, string title, Watch w, WatchAction action, WatchJudgement? j)
    {
        var drifting = j?.Verdict == WatchVerdict.Drifting;
        var why = j is { Reason.Length: > 0 } ? j.Reason : "最近一段时间没有新的进展";
        switch (action)
        {
            case WatchAction.None:
                if (j?.Verdict == WatchVerdict.Progressing)
                {
                    w.Signals.Restart(); // 模型说在推进：重新计时，过一阵再看
                    if (w.Stage is "reminded" or "notified")
                    {
                        w.Stage = "";
                        PostWatch(conversationId, w);
                    }
                }
                return;
            case WatchAction.Remind:
                _advisories[conversationId] = WatchdogJudge.ReminderText(j);
                w.Signals.Restart(); // 提醒后再给 N 分钟看看
                w.Stage = "reminded";
                Audit(conversationId, w.TaskId, "reminded", why);
                WatchdogNotice?.Invoke(drifting ? "任务可能跑偏了" : "任务可能卡住了", $"{title}：{why}", conversationId);
                break;
            case WatchAction.Notify:
                w.Signals.Restart();
                w.Stage = "notified";
                Audit(conversationId, w.TaskId, "notified", why);
                WatchdogNotice?.Invoke("任务还是没有进展", $"{title}：{why}。可以在任务中心停止它", conversationId);
                break;
            case WatchAction.Stop:
                w.Stage = "stopped";
                var reason = drifting ? "看门狗：偏离了目标" : "看门狗：长时间没有进展";
                Ledger.NoteOutcome(conversationId, TaskEvent.Fail, reason);
                Audit(conversationId, w.TaskId, "stopped", why);
                Log.Info($"看门狗停下了对话 {conversationId}：{why}");
                Stop(conversationId); // 和员工点「停止」一样：已发出的工具调用补结果
                WatchdogNotice?.Invoke("任务已被看门狗停下", $"{title}：{why}", conversationId);
                break;
        }
        PostWatch(conversationId, w);
    }

    private void PostWatch(string conversationId, Watch w) =>
        Events?.Post(new { type = "task.watchdog", conversationId, taskId = w.TaskId, watchdog = WatchdogDto(conversationId, w.TaskId) });

    /// <summary>看门狗的动作进审计：工具名 / actor 记 watchdog，status 记做了什么（提醒 / 再通知 / 停下），摘要是原因。</summary>
    private void Audit(string conversationId, string taskId, string what, string why)
    {
        try
        {
            _auditSink.Record(new AuditEntry
            {
                ConversationId = conversationId,
                TaskId = taskId,
                ToolName = "watchdog",
                Actor = "watchdog",
                Risk = "auto",
                Decision = "auto",
                Status = what,
                Summary = why,
                Scene = Scenes.Agent,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("记录看门狗审计失败", ex);
        }
    }

    /// <summary>有任务在跑时每 2 分钟一次；最后一个任务结束后再报一次空的，服务端就知道没有任务在跑了。</summary>
    private async Task HeartbeatIfDueAsync()
    {
        var running = _runs.Keys
            .Select(id => (TaskId: Ledger.TaskIdOf(id), Id: id))
            .Where(x => x.TaskId is { Length: > 0 })
            .Select(x => (x.TaskId!, Store.Tasks.Get(x.TaskId!)?.Origin ?? TaskOrigins.Ui))
            .Take(20)
            .ToList();
        if (running.Count == 0 && !_heartbeatHadTasks)
        {
            return;
        }
        if (running.Count > 0 && DateTimeOffset.Now - _lastHeartbeat < HeartbeatInterval)
        {
            return;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await Server.HeartbeatAsync(running, cts.Token);
            _lastHeartbeat = DateTimeOffset.Now;
            _heartbeatHadTasks = running.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_watchCts.IsCancellationRequested)
        {
            // 离线、旧服务端没有这个接口：下一分钟再试，不影响任务
            Log.Info($"心跳没发出去：{ex.Message}");
        }
    }
}
