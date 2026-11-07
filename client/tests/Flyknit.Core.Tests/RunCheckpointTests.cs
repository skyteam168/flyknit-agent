using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>断点：每步落盘，程序被关后「继续」时说清做到哪、最后那个操作有没有执行完。</summary>
public sealed class RunCheckpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-cp").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Gateway : IChatGateway
    {
        private readonly Queue<ChatTurn> _turns;
        public Gateway(params ChatTurn[] turns) => _turns = new Queue<ChatTurn>(turns);
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) =>
            Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : new ChatTurn { Content = "完成" });
    }

    private sealed class Confirm : IConfirmationHandler
    {
        public ConfirmChoice Choice { get; set; } = ConfirmChoice.Reject;
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(Choice);
    }

    /// <summary>执行时把整轮卡住，模拟程序在这一步被关掉。</summary>
    private sealed class Hang : ITool
    {
        public string Name => "slow_job";
        public string Description => "慢任务";
        public System.Text.Json.Nodes.JsonObject Parameters => ToolArgs.Schema();
        public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto();
        public string Describe(JsonElement args) => "导出全年报表";
        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Success("");
        }
    }

    private static ChatTurn Call(string name, object args) => new()
    {
        ToolCalls = new[] { new ToolCall("c_" + Guid.NewGuid().ToString("N")[..6], name, JsonSerializer.Serialize(args)) },
    };

    private ToolContext Ctx() => new() { Policy = CommandPolicy.Default(), ConversationId = "c1", Workspace = _dir, RunId = "r1" };

    [Fact]
    public async Task EveryStepUpdatesTheCheckpoint()
    {
        var saved = new List<string>();
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "x");
        var gateway = new Gateway(
            Call("update_plan", new { steps = new[] { new { step = "读文件", status = "in_progress" }, new { step = "写报告", status = "pending" } } }),
            Call("read_file", new { path = file }),
            Call("run_shell", new { command = "python build.py" }), // 认不出会做什么的命令要确认，被拒绝
            new ChatTurn { Content = "好了" });
        var loop = new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Confirm()) { Checkpoint = saved.Add };

        await loop.RunAsync(new List<ChatMessage> { ChatMessage.User("读一下") }, Scenes.Agent, Ctx(), new NullObserver(), true, CancellationToken.None);

        var last = RunCheckpoint.Parse(saved.Last())!;
        Assert.Equal("r1", last.RunId);
        Assert.Equal("读文件", last.Stage);
        Assert.Equal(2, last.Plan.Count);
        Assert.Equal(1, last.ApprovalsAsked);
        Assert.Equal(1, last.ApprovalsRejected);
        Assert.True(last.ToolCalls >= 2);
        Assert.Equal("read_file", last.LastTool!.Name);
        Assert.Equal("ok", last.LastTool.State);
    }

    [Fact]
    public async Task ACallInterruptedMidwayIsMarkedStarted()
    {
        var snapshots = new List<string>();
        var tools = ToolRegistry.CreateDefault().Add(new Hang());
        var loop = new AgentLoop(new Gateway(Call("slow_job", new { })), tools, new Confirm()) { Checkpoint = snapshots.Add };
        using var cts = new CancellationTokenSource(300);

        try
        {
            await loop.RunAsync(new List<ChatMessage> { ChatMessage.User("导出") }, Scenes.Agent, Ctx(), new NullObserver(), true, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        // 程序在执行中被关（进程直接没了）时，最后落盘的就是「开始了」这一份；
        // 员工点停止是正常收尾，循环会给这个调用补一个失败结果，断点跟着更新
        var cp = snapshots.Select(RunCheckpoint.Parse).First(c => c!.LastTool is { State: "started" })!;
        Assert.Equal("failed", RunCheckpoint.Parse(snapshots.Last())!.LastTool!.State);
        Assert.Equal("slow_job", cp.LastTool!.Name);
        Assert.Equal("started", cp.LastTool.State);
        var note = RunCheckpoint.ResumeNote(cp);
        Assert.Contains("导出全年报表", note);
        Assert.Contains("先核对它的结果", note);
    }

    [Fact]
    public void ResumeNoteShowsPlanProgress()
    {
        var cp = new RunCheckpoint { Steps = 7, ToolCalls = 5 };
        cp.PlanUpdated(new[] { new PlanItem("收集数据", "completed"), new PlanItem("做图表", "in_progress"), new PlanItem("写结论", "pending") });
        cp.ToolStarted("x", "write_file", "写 chart.xlsx");
        cp.ToolFinished("x", true);
        var note = RunCheckpoint.ResumeNote(RunCheckpoint.Parse(cp.ToJson()));
        Assert.Contains("✓ 收集数据", note);
        Assert.Contains("→ 做图表", note);
        Assert.Contains("从「做图表」接着做", note);
        Assert.Contains("已完成", note);
    }

    [Fact]
    public void WithoutCheckpointTheGenericNoteIsUsed()
    {
        Assert.Contains("先看看已经完成了哪些步骤", RunCheckpoint.ResumeNote(null));
        Assert.Null(RunCheckpoint.Parse("不是 JSON"));
    }

    [Fact]
    public void StoreKeepsTheCheckpointUntilTheRunEnds()
    {
        var store = new ConversationStore(Path.Combine(_dir, "h.db"));
        var conv = store.Create(ConversationMode.Agent, "做月报");
        store.MarkRunStarted(conv.Id, "ui");
        var cp = new RunCheckpoint();
        cp.PlanUpdated(new[] { new PlanItem("汇总", "in_progress") });
        store.SetRunState(conv.Id, cp.ToJson());

        var run = Assert.Single(store.InterruptedRuns());
        Assert.Equal("汇总", run.Stage);
        Assert.NotNull(store.GetRunState(conv.Id));

        store.MarkRunEnded(conv.Id);
        Assert.Null(store.GetRunState(conv.Id));
        store.MarkRunStarted(conv.Id, "ui");
        Assert.Equal("", Assert.Single(store.InterruptedRuns()).Stage); // 新的一轮从干净的断点开始
    }

    private sealed class NullObserver : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }
}
