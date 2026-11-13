using System.Collections.Concurrent;
using Flyknit.Core.Agent;

namespace Flyknit.Core.Tasks;

/// <summary>
/// 员工端宿主用的任务记账（docs/c1-unified-tasks.md 第 5 节）。宿主只在已有的节点上调它：
/// 入口登记触发（<see cref="Expect"/>）→ 开始跑（<see cref="BeginRun"/>）→ 等确认（<see cref="Waiting"/>）→
/// 这一轮结束（<see cref="EndRun"/>）→ 后面没有接着跑的了（<see cref="Finish"/>）。
///
/// 规则：
/// - 入口登记过的触发（手机、钉钉、指令、定时、PR 自动修复……）：下一次开始跑时开一个新任务。
/// - 没登记的（员工自己说话）：对话里有没结束的任务就接着记在它下面（排队的话、插话、自动续跑、中断续跑），
///   没有就开一个新的「员工本人」任务。
/// - 任务的 token 预算用完后：自动续跑不再开；员工自己再说话算新任务。
///
/// 这是账本，不参与执行：任何异常都吞掉、只记日志，不能挡住任务。
/// </summary>
public sealed class TaskLedger
{
    private readonly TaskStore _store;
    private readonly Action<string, Exception?> _warn;
    private readonly ConcurrentDictionary<string, TriggerEvent> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Current> _current = new(StringComparer.Ordinal);

    private sealed class Current
    {
        public required string TaskId { get; init; }
        public string RunId { get; set; } = "";
        /// <summary>最近一轮为什么停；null = 出错（异常），Message 是错误说明。</summary>
        public AgentStopReason? LastStop { get; set; }
        public string Message { get; set; } = "";
        public bool Ended { get; set; }
        /// <summary>收尾时用这个结果，不按最后一轮的停止原因推（看门狗停掉的：记失败和原因，不记「已停止」）。</summary>
        public (TaskEvent Event, string Reason)? Override { get; set; }
    }

    public TaskLedger(TaskStore store, Action<string, Exception?>? warn = null)
    {
        _store = store;
        _warn = warn ?? ((_, _) => { });
    }

    /// <summary>新任务的 token 预算（安全中心「单个任务的 token 上限」，换算成 tokens），0 = 不限。</summary>
    public long DefaultBudget { get; set; }

    /// <summary>入口登记一次触发：这个对话下一次开始跑时开新任务。</summary>
    public void Expect(TriggerEvent trigger) => _pending[trigger.ConversationId] = trigger;

    /// <summary>入口登记了但没跑起来（被拒、出错）：撤掉。</summary>
    public void Forget(string conversationId) => _pending.TryRemove(conversationId, out _);

    public sealed record Ticket(string TaskId, string RunId, string Kind, long? RemainingBudget);

    /// <summary>
    /// 开始跑一轮。kind 是宿主知道的这一轮的种类（第一轮 / 排队的话 / 自动续跑 / 中断续跑），任务账本据此和对话状态决定记在哪个任务下。
    /// 返回 null 表示记账失败（照常执行）。
    /// </summary>
    public Ticket? BeginRun(string conversationId, string title, string runId, string kind)
    {
        try
        {
            TaskRecord task;
            if (_pending.TryRemove(conversationId, out var trigger))
            {
                // 新的外部任务来了：这个对话里还没结束的旧任务（多半是中断了没续跑的）就此结束
                if (_store.OpenFor(conversationId) is { } old)
                {
                    _store.Transition(old.Id, TaskEvent.Stop, "没有续跑，开始了新任务");
                }
                task = _store.Create(trigger with { TokenBudget = DefaultBudget, Title = trigger.Title.Length > 0 ? trigger.Title : title });
                kind = RunKinds.First;
            }
            // 预算用完的任务不再接着记（自动续跑在 CanContinue 那里就拦下了；员工自己再说话算新任务）
            else if (_store.OpenFor(conversationId) is { } open && !open.BudgetExhausted)
            {
                task = open;
                if (open.State == TaskState.Interrupted)
                {
                    kind = RunKinds.Resume;
                }
                else if (kind == RunKinds.First)
                {
                    kind = RunKinds.Queued;
                }
            }
            else
            {
                if (_store.OpenFor(conversationId) is { } exhausted)
                {
                    _store.Transition(exhausted.Id, TaskEvent.Fail, "任务额度用完");
                }
                task = _store.Create(new TriggerEvent(TaskOrigins.Ui, conversationId, title, RequestedBy: "员工本人", TokenBudget: DefaultBudget));
                kind = RunKinds.First;
            }
            _store.StartRun(task.Id, runId, kind);
            _current[conversationId] = new Current { TaskId = task.Id, RunId = runId };
            long? remaining = task.TokenBudget > 0 ? Math.Max(0, task.TokenBudget - task.TokensUsed) : null;
            return new Ticket(task.Id, runId, kind, remaining);
        }
        catch (Exception ex)
        {
            _warn("记录任务开始失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 先定好这件事怎么收尾（看门狗停掉任务前调）：之后的 Finish 用它，而不是按「被停止」记成已停止。
    /// </summary>
    public void NoteOutcome(string conversationId, TaskEvent e, string reason)
    {
        if (_current.TryGetValue(conversationId, out var c))
        {
            c.Override = (e, reason);
        }
    }

    /// <summary>这个对话现在在跑的任务。</summary>
    public string? TaskIdOf(string conversationId) => _current.TryGetValue(conversationId, out var c) ? c.TaskId : null;

    /// <summary>进入 / 离开等员工确认。</summary>
    public void Waiting(string conversationId, bool waiting)
    {
        if (!_current.TryGetValue(conversationId, out var c))
        {
            return;
        }
        Safe(() => _store.Transition(c.TaskId, waiting ? TaskEvent.WaitConfirm : TaskEvent.Confirmed), "记录等确认失败");
    }

    /// <summary>一轮跑完：记下用量和停下的原因。stop 为 null 表示出错，message 是错误说明。</summary>
    public void EndRun(string conversationId, AgentStopReason? stop, long promptTokens, long completionTokens, string? answerId, string message = "")
    {
        if (!_current.TryGetValue(conversationId, out var c) || c.Ended)
        {
            return;
        }
        c.LastStop = stop;
        c.Message = message;
        c.Ended = true;
        Safe(() => _store.EndRun(c.RunId, stop?.ToString() ?? "Error", promptTokens, completionTokens, answerId), "记录任务用量失败");
    }

    /// <summary>
    /// 预算还够不够自动接着做（长任务模式自动续跑前问一声）。没有预算或记账失败都算够。
    /// </summary>
    public bool CanContinue(string conversationId)
    {
        try
        {
            return !_current.TryGetValue(conversationId, out var c) || _store.Get(c.TaskId) is not { BudgetExhausted: true };
        }
        catch (Exception ex)
        {
            _warn("读取任务预算失败", ex);
            return true;
        }
    }

    /// <summary>
    /// 这个对话没有接着跑的了（宿主在一轮的收尾、排队的话也处理完之后调）：按最后一轮的结果把任务落到完成 / 失败 / 停止。
    /// </summary>
    public void Finish(string conversationId)
    {
        if (!_current.TryRemove(conversationId, out var c))
        {
            return;
        }
        Safe(() =>
        {
            var task = _store.Get(c.TaskId);
            if (task is null || TaskStateMachine.IsFinal(task.State))
            {
                return;
            }
            var (e, reason) = c.Override ?? Outcome(c.LastStop, c.Message, task.BudgetExhausted);
            _store.Transition(task.Id, e, reason);
        }, "记录任务结束失败");
    }

    /// <summary>最后一轮的结果 → 任务怎么结束。</summary>
    public static (TaskEvent Event, string Reason) Outcome(AgentStopReason? stop, string message, bool budgetExhausted) => stop switch
    {
        AgentStopReason.Completed => (TaskEvent.Complete, ""),
        AgentStopReason.Cancelled => (TaskEvent.Stop, "已停止"),
        _ when budgetExhausted => (TaskEvent.Fail, "任务额度用完"),
        AgentStopReason.MaxSteps => (TaskEvent.Fail, "步数到上限，没做完"),
        AgentStopReason.Budget => (TaskEvent.Fail, "单次运行的额度用完，没做完"),
        AgentStopReason.Stuck => (TaskEvent.Fail, "一直在重复同一件事，停下了"),
        AgentStopReason.TooManyFailures => (TaskEvent.Fail, "失败太多次，停下了"),
        AgentStopReason.Failed => (TaskEvent.Fail, "模型服务出错"),
        null => (TaskEvent.Fail, message.Length > 0 ? "出错：" + message : "出错"),
        _ => (TaskEvent.Fail, stop.ToString()!),
    };

    /// <summary>
    /// 这一轮最多能用多少 token：单次运行的上限（0 = 不限）和任务还剩的预算（null = 不限）取小的。
    /// 剩余预算再小也至少给 1：0 在 AgentOptions 里表示「不限」，不能把「用完了」变成「不限」。
    /// </summary>
    public static long CapRunTokens(long runLimit, long? remaining)
    {
        if (remaining is not { } r)
        {
            return runLimit;
        }
        var left = Math.Max(1, r);
        return runLimit <= 0 ? left : Math.Min(runLimit, left);
    }

    /// <summary>员工对中断的任务选了「不用了」，或在任务中心停掉一个没在跑的任务。</summary>
    public void Abandon(string conversationId, string reason)
    {
        Safe(() =>
        {
            if (_store.OpenFor(conversationId) is { } open && !_current.ContainsKey(conversationId))
            {
                _store.Transition(open.Id, TaskEvent.Stop, reason);
            }
        }, "记录任务放弃失败");
    }

    /// <summary>
    /// 在任务中心停掉一个没在跑的任务（中断了没续跑的、还没开始的）。正在跑的由宿主走正常的「停止」，这里不碰，返回 false。
    /// </summary>
    public bool StopIdle(string taskId, string reason)
    {
        try
        {
            if (_store.Get(taskId) is not { } task || TaskStateMachine.IsFinal(task.State)
                || (_current.TryGetValue(task.ConversationId, out var c) && c.TaskId == taskId))
            {
                return false;
            }
            return _store.Transition(taskId, TaskEvent.Stop, reason) is not null;
        }
        catch (Exception ex)
        {
            _warn("记录任务停止失败", ex);
            return false;
        }
    }

    /// <summary>程序启动时调：上次没收尾的任务标成中断，升级前的老数据补成任务。</summary>
    public void RecoverAfterRestart() => Safe(() => _store.RecoverAfterRestart(), "恢复任务状态失败");

    private void Safe(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _warn(what, ex);
        }
    }
}
