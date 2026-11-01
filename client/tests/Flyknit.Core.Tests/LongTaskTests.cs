using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 长任务的底座：每一步立刻存库（中途被关不丢）、记下正在跑的任务（重启后知道哪些没做完）、
/// 修补「发起了调用却没有结果」的历史（中途被关时留下的）。
/// </summary>
public class LongTaskTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-long").FullName;
    private readonly ConversationStore _store;

    public LongTaskTests() => _store = new ConversationStore(Path.Combine(_dir, "history.db"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private sealed class Gateway : IChatGateway
    {
        private readonly Func<int, ChatTurn> _reply;
        private int _n;
        public Gateway(Func<int, ChatTurn> reply) => _reply = reply;
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct) => Task.FromResult(_reply(++_n));
    }

    private sealed class Reject : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.Reject);
    }

    private sealed class Observer : IAgentObserver
    {
        public void OnContent(string delta) { }
        public void OnReasoning(string delta) { }
        public void OnAssistantMessage(ChatMessage message) { }
        public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision) { }
        public void OnToolFinished(ToolCall call, ToolResult result, string decision) { }
        public void OnToolMessage(ChatMessage message) { }
        public void OnPlanUpdated(IReadOnlyList<PlanItem> plan) { }
    }

    private ToolContext Context(string conversationId) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = conversationId,
        Workspace = _dir,
        Permission = PermissionMode.Workspace,
        Security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.Sandbox] = new() { Value = false, Locked = true },
        }),
    };

    private ChatTurn ListDir() => new()
    {
        ToolCalls = new[] { new ToolCall("call_" + Guid.NewGuid().ToString("N")[..6], "list_dir", $"{{\"path\":{System.Text.Json.JsonSerializer.Serialize(_dir)}}}") },
    };

    [Fact]
    public async Task EveryStepIsSavedWhileTheRunIsStillGoing()
    {
        var conv = _store.Create(ConversationMode.Agent, "盘点");
        var user = ChatMessage.User("列一下文件");
        _store.AddMessages(conv.Id, new[] { user });
        var savedDuringRun = new List<int>();
        var gateway = new Gateway(n =>
        {
            // 第二次调模型时（任务还没结束），第一步的调用和结果应该已经在库里了
            savedDuringRun.Add(_store.GetMessages(conv.Id).Count);
            return n == 1 ? ListDir() : new ChatTurn { Content = "列好了" };
        });
        var loop = new AgentLoop(gateway, ToolRegistry.CreateDefault(), new Reject())
        {
            Persist = m => _store.AddMessages(conv.Id, new[] { m }),
        };

        var result = await loop.RunAsync(new List<ChatMessage> { ChatMessage.System("sys"), user }, Scenes.Agent, Context(conv.Id), new Observer(), true, CancellationToken.None);

        Assert.Equal(new[] { 1, 3 }, savedDuringRun);      // 用户的话；再加上调用 + 结果
        // 整轮结束时宿主再存一次最终版本：不会重复，只更新内容
        result.NewMessages[^1].TraceJson = "{\"steps\":2}";
        _store.AddMessages(conv.Id, result.NewMessages);
        var all = _store.GetMessages(conv.Id);
        Assert.Equal(4, all.Count);
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant }, all.Select(m => m.Role));
        Assert.Equal("{\"steps\":2}", all[^1].TraceJson);
    }

    [Fact]
    public void SavingTheSameMessageAgainUpdatesItInPlace()
    {
        var conv = _store.Create(ConversationMode.Agent, "t");
        var a = ChatMessage.User("一");
        var b = ChatMessage.Assistant("草稿", Array.Empty<ToolCall>(), null);
        _store.AddMessages(conv.Id, new[] { a, b });
        _store.SetOutputs(b.Id, new[] { @"D:\out.xlsx" });

        b.Content = "最终";
        _store.AddMessages(conv.Id, new[] { b });
        var c = ChatMessage.User("二");
        _store.AddMessages(conv.Id, new[] { c });

        var all = _store.GetMessages(conv.Id);
        Assert.Equal(new[] { "一", "最终", "二" }, all.Select(m => m.Content));
        Assert.Single(all[1].Outputs);                      // 后来补记的产出没被冲掉
    }

    [Fact]
    public void RunsThatNeverEndedAreReportedAsInterrupted()
    {
        var a = _store.Create(ConversationMode.Agent, "整理日报");
        var b = _store.Create(ConversationMode.Agent, "做完了的");
        var gone = _store.Create(ConversationMode.Agent, "删掉的");
        _store.MarkRunStarted(a.Id, "ui");
        _store.MarkRunStarted(b.Id, "headless");
        _store.MarkRunEnded(b.Id);
        _store.MarkRunStarted(gone.Id, "ui");
        _store.Delete(gone.Id);

        var list = _store.InterruptedRuns();
        var only = Assert.Single(list);
        Assert.Equal((a.Id, "整理日报", "ui"), (only.ConversationId, only.Title, only.Origin));
        Assert.NotNull(_store.Get(a.Id)!.RunStartedAt);

        _store.MarkRunEnded(a.Id);
        Assert.Empty(_store.InterruptedRuns());
    }

    [Fact]
    public void LongModeIsRememberedPerConversation()
    {
        var conv = _store.Create(ConversationMode.Agent, "t");
        Assert.False(_store.Get(conv.Id)!.LongMode);
        _store.SetLongMode(conv.Id, true);
        Assert.True(_store.Get(conv.Id)!.LongMode);
        Assert.True(_store.List().Single(x => x.Id == conv.Id).LongMode);
    }

    [Fact]
    public void AToolCallCutOffMidwayGetsAnExplanationInsteadOfBreakingTheHistory()
    {
        var call1 = new ToolCall("c1", "list_dir", "{}");
        var call2 = new ToolCall("c2", "run_shell", "{}");
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("整理"),
            ChatMessage.Assistant("", new[] { call1, call2 }, null),
            ChatMessage.ToolResult(call1, "ok"),                 // c2 执行到一半程序被关了
            ChatMessage.User("继续"),
        };

        Assert.Equal(1, HistoryRepair.CloseDanglingToolCalls(messages));
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Tool, ChatRole.User }, messages.Select(m => m.Role));
        Assert.Equal("c2", messages[3].ToolCallId);
        Assert.Equal(HistoryRepair.InterruptedResult, messages[3].Content);
        Assert.Equal(0, HistoryRepair.CloseDanglingToolCalls(messages)); // 再修一次不会重复补
    }
}
