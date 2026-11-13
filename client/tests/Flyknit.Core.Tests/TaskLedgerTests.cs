using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>宿主用的任务记账：一串算一个任务、外部触发开新任务、预算、结束状态、记账失败不挡执行。</summary>
public sealed class TaskLedgerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-ledger").FullName;
    private readonly ConversationStore _store;
    private readonly TaskLedger _ledger;
    private readonly string _conv;

    public TaskLedgerTests()
    {
        _store = new ConversationStore(Path.Combine(_dir, "history.db"));
        _ledger = new TaskLedger(_store.Tasks);
        _conv = _store.Create(ConversationMode.Agent, "整理日报").Id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private void Turn(string runId, string kind, AgentStopReason? stop, long tokens = 100, bool finish = true)
    {
        _ledger.BeginRun(_conv, "整理日报", runId, kind);
        _ledger.EndRun(_conv, stop, tokens, 0, null);
        if (finish)
        {
            _ledger.Finish(_conv);
        }
    }

    [Fact]
    public void AChainOfRunsIsOneTask()
    {
        // 员工说话 → 排队的话接着跑 → 长任务自动续跑 → 停下：一个任务、三个 Run
        var t1 = _ledger.BeginRun(_conv, "整理日报", "r1", RunKinds.First)!;
        _ledger.EndRun(_conv, AgentStopReason.Completed, 100, 10, "a1");
        var t2 = _ledger.BeginRun(_conv, "整理日报", "r2", RunKinds.First)!;   // 宿主不知道是排队的：账本认出来
        _ledger.EndRun(_conv, AgentStopReason.MaxSteps, 100, 10, "a2");
        var t3 = _ledger.BeginRun(_conv, "整理日报", "r3", RunKinds.AutoContinue)!;
        _ledger.EndRun(_conv, AgentStopReason.Completed, 100, 10, "a3");
        _ledger.Finish(_conv);

        Assert.Equal(t1.TaskId, t2.TaskId);
        Assert.Equal(t1.TaskId, t3.TaskId);
        Assert.Equal(RunKinds.Queued, t2.Kind);
        var task = _store.Tasks.Get(t1.TaskId)!;
        Assert.Equal(TaskState.Done, task.State);
        Assert.Equal(3, task.Runs);
        Assert.Equal(330, task.TokensUsed);
        Assert.Equal(TaskOrigins.Ui, task.Origin);
        Assert.Equal("员工本人", task.RequestedBy);
    }

    [Fact]
    public void TalkingAgainAfterTheChainEndedIsANewTask()
    {
        Turn("r1", RunKinds.First, AgentStopReason.Completed);
        var second = _ledger.BeginRun(_conv, "整理日报", "r2", RunKinds.First)!;
        Assert.Equal(RunKinds.First, second.Kind);
        Assert.Equal(2, _store.Tasks.List().Count);
    }

    [Fact]
    public void ExternalTriggersAlwaysStartTheirOwnTask()
    {
        _ledger.Expect(new TriggerEvent(TaskOrigins.Schedule, _conv, "日报定时任务", "sched-1", "定时任务：日报", "g1"));
        var t = _ledger.BeginRun(_conv, "整理日报", "r1", RunKinds.First)!;
        var task = _store.Tasks.Get(t.TaskId)!;
        Assert.Equal(TaskOrigins.Schedule, task.Origin);
        Assert.Equal("g1", task.GrantId);
        Assert.Equal("日报定时任务", task.Title);
        Assert.Equal(TaskState.Running, task.State);
    }

    [Fact]
    public void AWatchdogStopIsRecordedAsAFailureWithItsReason()
    {
        var t = _ledger.BeginRun(_conv, "x", "r1", RunKinds.First)!;
        _ledger.NoteOutcome(_conv, TaskEvent.Fail, "看门狗：长时间没有进展");
        _ledger.EndRun(_conv, AgentStopReason.Cancelled, 10, 2, null);   // 看门狗走的是正常的停止
        _ledger.Finish(_conv);
        var task = _store.Tasks.Get(t.TaskId)!;
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("看门狗：长时间没有进展", task.StopReason);
        _ledger.NoteOutcome(_conv, TaskEvent.Fail, "没在跑");             // 没在跑的对话：不做事、不抛
    }

    [Fact]
    public void AnAdoptedPendingTaskIsUsedInsteadOfANewOne()
    {
        var pre = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, _conv, "1/3 · 合并日报", Reference: "split:c0", RequestedBy: "员工本人", GroupId: "g1"));
        _ledger.Adopt(_conv, pre.Id);
        var t = _ledger.BeginRun(_conv, "x", "r1", RunKinds.First)!;
        Assert.Equal(pre.Id, t.TaskId);
        var task = _store.Tasks.Get(pre.Id)!;
        Assert.Equal(TaskState.Running, task.State);
        Assert.Equal("g1", task.GroupId);

        // 预建的任务已经被停掉了（任务中心里停了还没轮到的那件）：不再用它，照常开新任务
        _ledger.EndRun(_conv, AgentStopReason.Completed, 1, 1, null);
        _ledger.Finish(_conv);
        var stopped = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, _conv, "2/3", GroupId: "g1"));
        _store.Tasks.Transition(stopped.Id, TaskEvent.Stop, "员工在任务中心停止");
        _ledger.Adopt(_conv, stopped.Id);
        Assert.NotEqual(stopped.Id, _ledger.BeginRun(_conv, "x", "r2", RunKinds.First)!.TaskId);
    }

    [Fact]
    public void TheTaskCentreStopsOnlyIdleTasks()
    {
        var t = _ledger.BeginRun(_conv, "x", "r1", RunKinds.First)!;
        Assert.False(_ledger.StopIdle(t.TaskId, "员工在任务中心停止"));   // 正在跑：宿主走正常的停止，账本不碰
        Assert.Equal(TaskState.Running, _store.Tasks.Get(t.TaskId)!.State);

        _store.Tasks.RecoverAfterRestart();                              // 程序被关：中断
        var fresh = new TaskLedger(_store.Tasks);
        Assert.True(fresh.StopIdle(t.TaskId, "员工在任务中心停止"));
        var stopped = _store.Tasks.Get(t.TaskId)!;
        Assert.Equal(TaskState.Stopped, stopped.State);
        Assert.Equal("员工在任务中心停止", stopped.StopReason);
        Assert.False(fresh.StopIdle(t.TaskId, "再停一次"));               // 已经结束的不动
        Assert.False(fresh.StopIdle("no-such-task", "x"));
        Assert.Null(_store.Tasks.OpenFor(_conv));
    }

    [Fact]
    public void ATasksGrantCanBeLookedUpById()
    {
        var g = TaskGrant.Issue("pr", _conv, "pw_1", "盯 PR #7", "修 CI", PermissionMode.Workspace, "D:\\repo", null);
        _store.Grants.Add(g);
        var got = _store.Grants.Get(g.Id)!;
        Assert.Equal("pr", got.Origin);
        Assert.Equal(PermissionMode.Workspace, got.Permission);
        Assert.Null(_store.Grants.Get("g-missing"));
    }

    [Fact]
    public void ATriggerClosesAnUnresumedInterruptedTask()
    {
        _ledger.BeginRun(_conv, "x", "r1", RunKinds.First);
        _store.Tasks.RecoverAfterRestart();                     // 程序被关：中断
        var interrupted = _store.Tasks.OpenFor(_conv)!;
        var fresh = new TaskLedger(_store.Tasks);                // 重新打开程序
        fresh.Expect(new TriggerEvent(TaskOrigins.Mobile, _conv, "手机发的", RequestedBy: "手机"));
        var t = fresh.BeginRun(_conv, "x", "r2", RunKinds.First)!;
        Assert.NotEqual(interrupted.Id, t.TaskId);
        var old = _store.Tasks.Get(interrupted.Id)!;
        Assert.Equal(TaskState.Stopped, old.State);
        Assert.Contains("新任务", old.StopReason);
    }

    [Fact]
    public void ResumingAnInterruptedTaskContinuesIt()
    {
        var t1 = _ledger.BeginRun(_conv, "x", "r1", RunKinds.First)!;
        _store.Tasks.RecoverAfterRestart();
        var fresh = new TaskLedger(_store.Tasks);
        var t2 = fresh.BeginRun(_conv, "x", "r2", RunKinds.Resume)!;
        Assert.Equal(t1.TaskId, t2.TaskId);
        Assert.Equal(RunKinds.Resume, t2.Kind);
        Assert.Equal(TaskState.Running, _store.Tasks.Get(t1.TaskId)!.State);
    }

    [Theory]
    [InlineData(AgentStopReason.Completed, TaskState.Done)]
    [InlineData(AgentStopReason.Cancelled, TaskState.Stopped)]
    [InlineData(AgentStopReason.MaxSteps, TaskState.Failed)]
    [InlineData(AgentStopReason.Stuck, TaskState.Failed)]
    [InlineData(AgentStopReason.Failed, TaskState.Failed)]
    public void TheLastRunDecidesHowTheTaskEnds(AgentStopReason stop, TaskState expected)
    {
        Turn("r1", RunKinds.First, stop);
        Assert.Equal(expected, _store.Tasks.List().Single().State);
    }

    [Fact]
    public void AnExceptionEndsTheTaskAsFailedWithTheMessage()
    {
        _ledger.BeginRun(_conv, "x", "r1", RunKinds.First);
        _ledger.EndRun(_conv, null, 0, 0, null, "沙箱没起来");
        _ledger.Finish(_conv);
        var task = _store.Tasks.List().Single();
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("出错：沙箱没起来", task.StopReason);
    }

    [Fact]
    public void WaitingForConfirmationIsTracked()
    {
        var t = _ledger.BeginRun(_conv, "x", "r1", RunKinds.First)!;
        _ledger.Waiting(_conv, true);
        Assert.Equal(TaskState.WaitingConfirm, _store.Tasks.Get(t.TaskId)!.State);
        _ledger.Waiting(_conv, false);
        Assert.Equal(TaskState.Running, _store.Tasks.Get(t.TaskId)!.State);
    }

    [Fact]
    public void BudgetStopsAutoContinueAndTheNextMessageStartsANewTask()
    {
        _ledger.DefaultBudget = 1_000;
        var t1 = _ledger.BeginRun(_conv, "x", "r1", RunKinds.First)!;
        Assert.Equal(1_000, t1.RemainingBudget);
        _ledger.EndRun(_conv, AgentStopReason.MaxSteps, 600, 0, null);
        Assert.True(_ledger.CanContinue(_conv));
        var t2 = _ledger.BeginRun(_conv, "x", "r2", RunKinds.AutoContinue)!;
        Assert.Equal(400, t2.RemainingBudget);                  // 这一轮最多还能用多少
        _ledger.EndRun(_conv, AgentStopReason.Budget, 400, 0, null);
        Assert.False(_ledger.CanContinue(_conv));               // 用完了：不再自动续跑
        _ledger.Finish(_conv);
        var task = _store.Tasks.Get(t1.TaskId)!;
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("任务额度用完", task.StopReason);

        // 员工自己再说话：新任务，额度重新算
        var t3 = _ledger.BeginRun(_conv, "x", "r3", RunKinds.First)!;
        Assert.NotEqual(t1.TaskId, t3.TaskId);
        Assert.Equal(1_000, t3.RemainingBudget);
    }

    [Fact]
    public void NoBudgetMeansUnlimited()
    {
        var t = _ledger.BeginRun(_conv, "x", "r1", RunKinds.First)!;
        Assert.Null(t.RemainingBudget);
        _ledger.EndRun(_conv, AgentStopReason.MaxSteps, 99_999_999, 0, null);
        Assert.True(_ledger.CanContinue(_conv));
    }

    [Fact]
    public void AbandoningAnInterruptedTaskStopsIt()
    {
        _ledger.BeginRun(_conv, "x", "r1", RunKinds.First);
        _store.Tasks.RecoverAfterRestart();
        var fresh = new TaskLedger(_store.Tasks);
        fresh.Abandon(_conv, "员工放弃续跑");
        var task = _store.Tasks.List().Single();
        Assert.Equal(TaskState.Stopped, task.State);
        Assert.Equal("员工放弃续跑", task.StopReason);
    }

    [Theory]
    [InlineData(2_000_000, null, 2_000_000)]     // 任务不限：用单次上限
    [InlineData(0, null, 0)]                     // 都不限
    [InlineData(2_000_000, 300_000L, 300_000)]   // 任务剩得少：用剩下的
    [InlineData(200_000, 300_000L, 200_000)]     // 单次上限更小：用单次上限
    [InlineData(0, 300_000L, 300_000)]           // 单次不限、任务有预算
    [InlineData(2_000_000, 0L, 1)]               // 剩 0 不能变成「不限」
    public void RunTokenCapTakesTheSmallerLimit(long runLimit, long? remaining, long expected)
    {
        Assert.Equal(expected, TaskLedger.CapRunTokens(runLimit, remaining));
    }

    private sealed class Answering(ConfirmChoice choice, Action onAsk) : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            onAsk();
            return Task.FromResult(choice);
        }
    }

    [Fact]
    public async Task ConfirmationMarksTheTaskWaitingAndPassesTheAnswerThrough()
    {
        var t = _ledger.BeginRun(_conv, "x", "r1", RunKinds.First)!;
        TaskState? during = null;
        var inner = new Answering(ConfirmChoice.Reject, () => during = _store.Tasks.Get(t.TaskId)!.State);
        var wrapped = new LedgerConfirmation(inner, waiting => _ledger.Waiting(_conv, waiting));
        var answer = await wrapped.ConfirmAsync(new ConfirmRequest { ConversationId = _conv, Call = new ToolCall("1", "run_shell", "{}"), Summary = "del a", Decision = PolicyDecision.Confirm("x") }, CancellationToken.None);
        Assert.Equal(ConfirmChoice.Reject, answer);                // 确认结果原样返回，不改
        Assert.Equal(TaskState.WaitingConfirm, during);
        Assert.Equal(TaskState.Running, _store.Tasks.Get(t.TaskId)!.State);
    }

    [Fact]
    public void BookkeepingFailuresNeverThrow()
    {
        // 库被删掉（或者文件被锁住）：记账全部失败，但都不抛异常
        var warnings = new List<string>();
        var broken = new TaskLedger(new TaskStore(() => throw new InvalidOperationException("库打不开")), (what, _) => warnings.Add(what));
        broken.Expect(new TriggerEvent(TaskOrigins.Mobile, "c", "x"));
        Assert.Null(broken.BeginRun("c", "x", "r1", RunKinds.First));
        broken.Waiting("c", true);
        broken.EndRun("c", AgentStopReason.Completed, 1, 1, null);
        Assert.True(broken.CanContinue("c"));
        broken.Finish("c");
        broken.Abandon("c", "x");
        broken.RecoverAfterRestart();
        Assert.NotEmpty(warnings);
    }
}
