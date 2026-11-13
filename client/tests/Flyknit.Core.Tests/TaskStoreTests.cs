using Flyknit.Core.Agent;
using Flyknit.Core.Storage;
using Flyknit.Core.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>VNext 第 1 期：任务账本（状态机、任务和运行记录、重启后恢复、订阅）。</summary>
public sealed class TaskStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-tasks").FullName;
    private readonly ConversationStore _store;

    public TaskStoreTests() => _store = new ConversationStore(Path.Combine(_dir, "history.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    // ---------- 状态机 ----------

    [Theory]
    [InlineData(TaskState.Pending, TaskEvent.Start, TaskState.Running)]
    [InlineData(TaskState.Running, TaskEvent.WaitConfirm, TaskState.WaitingConfirm)]
    [InlineData(TaskState.WaitingConfirm, TaskEvent.Confirmed, TaskState.Running)]
    [InlineData(TaskState.Running, TaskEvent.Start, TaskState.Running)]          // 同一任务开下一个 Run
    [InlineData(TaskState.Running, TaskEvent.Interrupt, TaskState.Interrupted)]
    [InlineData(TaskState.WaitingConfirm, TaskEvent.Interrupt, TaskState.Interrupted)]
    [InlineData(TaskState.Interrupted, TaskEvent.Resume, TaskState.Running)]
    [InlineData(TaskState.Running, TaskEvent.Complete, TaskState.Done)]
    [InlineData(TaskState.Running, TaskEvent.Fail, TaskState.Failed)]
    [InlineData(TaskState.Running, TaskEvent.Stop, TaskState.Stopped)]
    [InlineData(TaskState.Interrupted, TaskEvent.Stop, TaskState.Stopped)]       // 员工放弃续跑
    [InlineData(TaskState.Pending, TaskEvent.Stop, TaskState.Stopped)]
    public void AllowedTransitions(TaskState from, TaskEvent e, TaskState to)
    {
        Assert.Equal(to, TaskStateMachine.Next(from, e));
    }

    [Theory]
    [InlineData(TaskState.Done, TaskEvent.Start)]          // 结束了就不能再开
    [InlineData(TaskState.Stopped, TaskEvent.Resume)]
    [InlineData(TaskState.Failed, TaskEvent.Complete)]
    [InlineData(TaskState.Pending, TaskEvent.Complete)]    // 没开始就不能算完成
    [InlineData(TaskState.Pending, TaskEvent.Resume)]
    [InlineData(TaskState.Running, TaskEvent.Confirmed)]
    public void IllegalTransitionsAreRejected(TaskState from, TaskEvent e)
    {
        Assert.Null(TaskStateMachine.Next(from, e));
    }

    [Fact]
    public void StatesRoundTripAsText()
    {
        foreach (var s in Enum.GetValues<TaskState>())
        {
            Assert.Equal(s, TaskStateMachine.Parse(TaskStateMachine.ToText(s)));
        }
    }

    // ---------- 任务和运行记录 ----------

    private string NewConversation(string title = "汇总日报") => _store.Create(ConversationMode.Agent, title).Id;

    [Fact]
    public void ATaskCollectsItsRunsAndTokens()
    {
        var conv = NewConversation();
        var task = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Schedule, conv, "日报", "sched-1", "定时任务：日报", "g123", 50_000));
        Assert.Equal(TaskState.Pending, task.State);
        Assert.Equal("g123", task.GrantId);

        _store.Tasks.StartRun(task.Id, "run-1", RunKinds.First);
        _store.Tasks.EndRun("run-1", "MaxSteps", 20_000, 1_000, "answer-1");
        _store.Tasks.StartRun(task.Id, "run-2", RunKinds.AutoContinue);
        _store.Tasks.EndRun("run-2", "Completed", 9_000, 500, "answer-2");
        _store.Tasks.EndRun("run-2", "Completed", 9_000, 500, "answer-2");   // 重复上报不重复累计
        var done = _store.Tasks.Transition(task.Id, TaskEvent.Complete)!;

        Assert.Equal(TaskState.Done, done.State);
        Assert.Equal(2, done.Runs);
        Assert.Equal(30_500, done.TokensUsed);
        Assert.NotNull(done.StartedAt);
        Assert.NotNull(done.FinishedAt);
        var runs = _store.Tasks.RunsOf(task.Id);
        Assert.Equal(new[] { RunKinds.First, RunKinds.AutoContinue }, runs.Select(r => r.Kind));
        Assert.Equal("answer-2", runs[1].AnswerId);
    }

    [Fact]
    public void BudgetIsExhaustedOnlyWhenSetAndReached()
    {
        var conv = NewConversation();
        var unlimited = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, conv, "x"));
        _store.Tasks.StartRun(unlimited.Id, "r0", RunKinds.First);
        _store.Tasks.EndRun("r0", "Completed", 9_999_999, 0, null);
        Assert.False(_store.Tasks.Get(unlimited.Id)!.BudgetExhausted);   // 0 = 不限

        var capped = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, conv, "x", TokenBudget: 10_000));
        _store.Tasks.StartRun(capped.Id, "r1", RunKinds.First);
        _store.Tasks.EndRun("r1", "Budget", 6_000, 0, null);
        Assert.False(_store.Tasks.Get(capped.Id)!.BudgetExhausted);
        _store.Tasks.StartRun(capped.Id, "r2", RunKinds.AutoContinue);
        _store.Tasks.EndRun("r2", "Budget", 4_000, 0, null);
        Assert.True(_store.Tasks.Get(capped.Id)!.BudgetExhausted);
    }

    [Fact]
    public void IllegalTransitionLeavesTheTaskAlone()
    {
        var task = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, NewConversation(), "x"));
        Assert.Null(_store.Tasks.Transition(task.Id, TaskEvent.Complete));
        Assert.Equal(TaskState.Pending, _store.Tasks.Get(task.Id)!.State);
        Assert.Null(_store.Tasks.Transition("t-no-such-task", TaskEvent.Start));
    }

    [Fact]
    public void OpenForFindsTheUnfinishedTaskOfAConversation()
    {
        var conv = NewConversation();
        var first = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, conv, "a"));
        _store.Tasks.StartRun(first.Id, "r1", RunKinds.First);
        Assert.Equal(first.Id, _store.Tasks.OpenFor(conv)!.Id);
        _store.Tasks.Transition(first.Id, TaskEvent.Complete);
        Assert.Null(_store.Tasks.OpenFor(conv));
    }

    [Fact]
    public void UnknownOriginFallsBackToUi()
    {
        var task = _store.Tasks.Create(new TriggerEvent("whatever", NewConversation(), "x"));
        Assert.Equal(TaskOrigins.Ui, task.Origin);
    }

    [Fact]
    public void TasksAreDeletedWithTheirConversation()
    {
        var conv = NewConversation();
        var task = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, conv, "x"));
        _store.Tasks.StartRun(task.Id, "r1", RunKinds.First);
        _store.Subscriptions.Subscribe(conv, SubscriptionKinds.PullRequest, "w1", "PR #12");
        _store.Purge(conv);
        Assert.Null(_store.Tasks.Get(task.Id));
        Assert.Empty(_store.Tasks.RunsOf(task.Id));
        Assert.Null(_store.Subscriptions.Find(SubscriptionKinds.PullRequest, "w1"));
    }

    // ---------- 重启后恢复 ----------

    [Fact]
    public void RunningTasksBecomeInterruptedAfterRestartAndOldRunsAreBackfilledOnce()
    {
        var a = NewConversation("正在跑的");
        var task = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, a, "正在跑的"));
        _store.Tasks.StartRun(task.Id, "r1", RunKinds.First);
        _store.MarkRunStarted(a, "ui");

        // 升级前就没跑完的对话：只有 conversations 上的标记，没有任务
        var b = NewConversation("升级前没跑完");
        _store.MarkRunStarted(b, "remote");

        _store.Tasks.RecoverAfterRestart();
        _store.Tasks.RecoverAfterRestart();   // 重复调用不重复补

        Assert.Equal(TaskState.Interrupted, _store.Tasks.Get(task.Id)!.State);
        Assert.Equal("interrupted", _store.Tasks.RunsOf(task.Id).Single().Status);
        var backfilled = _store.Tasks.List().Where(t => t.ConversationId == b).ToList();
        var only = Assert.Single(backfilled);
        Assert.Equal(TaskState.Interrupted, only.State);
        Assert.Equal(TaskOrigins.Mobile, only.Origin);
        Assert.Equal("升级前没跑完", only.Title);
        Assert.Single(_store.Tasks.List(), t => t.ConversationId == a);

        // 续跑：中断 → 进行中，同一个任务
        _store.Tasks.StartRun(task.Id, "r2", RunKinds.Resume);
        Assert.Equal(TaskState.Running, _store.Tasks.Get(task.Id)!.State);
    }

    [Fact]
    public void ListShowsOpenTasksAndRecentFinishedOnes()
    {
        var conv = NewConversation();
        var open = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, conv, "进行中"));
        _store.Tasks.StartRun(open.Id, "r0", RunKinds.First);
        for (var i = 0; i < 5; i++)
        {
            var t = _store.Tasks.Create(new TriggerEvent(TaskOrigins.Ui, conv, $"结束 {i}"));
            _store.Tasks.StartRun(t.Id, $"r{i + 1}", RunKinds.First);
            _store.Tasks.Transition(t.Id, TaskEvent.Complete);
        }
        var list = _store.Tasks.List(recentFinished: 3);
        Assert.Contains(list, t => t.Id == open.Id);
        Assert.Equal(3, list.Count(t => t.State == TaskState.Done));
    }

    // ---------- 订阅 ----------

    [Fact]
    public void SubscriptionsAreUniquePerSourceAndCanEnd()
    {
        var conv = NewConversation();
        var s1 = _store.Subscriptions.Subscribe(conv, SubscriptionKinds.PullRequest, "w1", "PR #12");
        var again = _store.Subscriptions.Subscribe(conv, SubscriptionKinds.PullRequest, "w1", "PR #12");
        Assert.Equal(s1.Id, again.Id);
        Assert.True(_store.Subscriptions.End(SubscriptionKinds.PullRequest, "w1", "merged"));
        Assert.False(_store.Subscriptions.End(SubscriptionKinds.PullRequest, "w1", "merged"));
        var list = _store.Subscriptions.For(conv);
        var only = Assert.Single(list);
        Assert.False(only.Active);
        Assert.Equal("merged", only.EndedReason);
        // 结束后可以重新订阅
        Assert.NotEqual(s1.Id, _store.Subscriptions.Subscribe(conv, SubscriptionKinds.PullRequest, "w1", "PR #12").Id);
    }
}
