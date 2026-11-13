using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Tools;

namespace Flyknit.Client.Services;

/// <summary>一个正在运行的任务此刻的样子：悬浮球、任务面板显示用。</summary>
/// <param name="Step">正在做的事：计划里进行中的那一步，没有计划时是最近一个工具在做什么。</param>
/// <param name="Done">计划完成了几步（没有计划为 0）。</param>
/// <param name="Total">计划一共几步（没有计划为 0）。</param>
public sealed record RunSnapshot(string ConversationId, string Title, string Step, int Done, int Total, bool Paused, DateTimeOffset StartedAt, bool Remote);

public sealed partial class AgentHost
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset StartedAt, string Step)> _progress = new();

    /// <summary>被暂停的任务：闸门一直关着，直到继续或停止。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pauses = new();

    /// <summary>任务开始、结束、换了一步、计划有进展、暂停 / 继续时触发（后台线程）。</summary>
    public event Action? RunStatusChanged;

    private void BeginProgress(string conversationId)
    {
        _progress[conversationId] = (DateTimeOffset.Now, "");
        RunStatusChanged?.Invoke();
    }

    private void EndProgress(string conversationId)
    {
        _progress.TryRemove(conversationId, out _);
        Resume(conversationId, notify: false);
        PostPaused(conversationId, false);
        RunStatusChanged?.Invoke();
    }

    /// <summary>工具开始执行：记下它在做什么（一行字）。</summary>
    private void NoteStep(string conversationId, string text)
    {
        if (!_progress.TryGetValue(conversationId, out var p))
        {
            return;
        }
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        _progress[conversationId] = (p.StartedAt, line.Length > 80 ? line[..80] + "…" : line);
        RunStatusChanged?.Invoke();
    }

    private void NotePlan() => RunStatusChanged?.Invoke();

    public bool IsPaused(string conversationId) => _pauses.ContainsKey(conversationId);

    /// <summary>
    /// 暂停：正在进行的那次模型调用 / 那个工具做完后停住，不再往下走，直到继续或停止。
    /// 没在运行返回 false。
    /// </summary>
    public bool Pause(string conversationId)
    {
        if (!IsRunning(conversationId) || !_pauses.TryAdd(conversationId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)))
        {
            return false;
        }
        WatchHold(conversationId, true); // 暂停期间不算没进展
        Log.Info($"暂停任务 {conversationId}");
        PostPaused(conversationId, true);
        RunStatusChanged?.Invoke();
        return true;
    }

    public bool Resume(string conversationId) => Resume(conversationId, notify: true);

    private bool Resume(string conversationId, bool notify)
    {
        if (!_pauses.TryRemove(conversationId, out var gate))
        {
            return false;
        }
        gate.TrySetResult();
        WatchHold(conversationId, false);
        if (notify)
        {
            Log.Info($"继续任务 {conversationId}");
            PostPaused(conversationId, false);
            RunStatusChanged?.Invoke();
        }
        return true;
    }

    private void PostPaused(string conversationId, bool paused) =>
        Events?.Post(new { type = "chat.paused", conversationId, paused });

    /// <summary>给 AgentLoop 的暂停闸门：暂停中就等着；被停止时正常返回（循环自己按停止收尾）。</summary>
    private Func<CancellationToken, Task> PauseGateFor(string conversationId) => async ct =>
    {
        while (_pauses.TryGetValue(conversationId, out var gate) && !ct.IsCancellationRequested)
        {
            try
            {
                await gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    };

    /// <summary>正在运行的任务，最早开始的在前。</summary>
    public IReadOnlyList<RunSnapshot> RunSnapshots()
    {
        var list = new List<RunSnapshot>();
        foreach (var (id, p) in _progress)
        {
            if (!IsRunning(id))
            {
                continue;
            }
            var conv = Store.Get(id);
            var plan = TaskPlan.Parse(conv?.Plan);
            var done = plan.Count(x => x.Status == "completed");
            var current = plan.FirstOrDefault(x => x.Status == "in_progress")?.Step;
            list.Add(new RunSnapshot(
                id,
                conv?.Title is { Length: > 0 } t ? t : "新任务",
                current ?? p.Step,
                done,
                plan.Count,
                IsPaused(id),
                p.StartedAt,
                _remoteRuns.ContainsKey(id)));
        }
        return list.OrderBy(x => x.StartedAt).ToList();
    }
}
