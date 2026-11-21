using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Chat;
using Flyknit.Core.Code;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Storage;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 编码模式「带上摘要新建任务」：旧任务整理成交接摘要，新任务第一句话带上（只给一次），
/// 同一个项目才能带；员工点「不带了」就不带。
/// </summary>
public sealed class TaskHandoffTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("fk-handoff-").FullName;
    private readonly string _other = Directory.CreateTempSubdirectory("fk-handoff-other-").FullName;
    private readonly List<string> _log = new();
    private readonly CodeFeature _code;
    private readonly List<string> _summarized = new();
    private Func<string, CancellationToken, Task<string?>> _make;

    public TaskHandoffTests()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "web"));
        _make = (from, _) => Task.FromResult<string?>($"任务 {from}：登录页改完了，还差记住密码");
        _code = new CodeFeature(id => id switch
        {
            "old" or "new" => _ws,
            "narrowed" => Path.Combine(_ws, "web"),
            "elsewhere" => _other,
            _ => null,
        }, _log.Add)
        {
            Summarize = (from, ct) =>
            {
                _summarized.Add(from);
                return _make(from, ct);
            },
        };
    }

    public void Dispose()
    {
        Directory.Delete(_ws, true);
        Directory.Delete(_other, true);
    }

    private static RunScope Scope(string id, bool newMessage = true) => new()
    {
        Conversation = new Conversation { Id = id, Workspace = "x", Mode = ConversationMode.Agent },
        Query = "接着做",
        RunId = "r1",
        Scene = "code",
        NewUserMessage = newMessage,
        Confirm = new Allow(),
    };

    private sealed class Allow : IConfirmationHandler
    {
        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(ConfirmChoice.AllowOnce);
    }

    private static T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;

    [Fact]
    public async Task FirstMessageOfTheNewTaskCarriesTheSummaryOnce()
    {
        var r = await _code.HandoffAsync("new", "old");
        Assert.True(Prop<bool>(r, "ok"));
        Assert.Contains("还差记住密码", Prop<string>(r, "summary"));
        Assert.Equal(new[] { "old" }, _summarized);

        var ext = new EditorContextRunExtension(_code.Editors, _code.Handoffs);
        // 续跑、重试不是新的一句话：不取
        Assert.Null(await ext.PrepareAsync(Scope("new", newMessage: false), CancellationToken.None));
        var first = (await ext.PrepareAsync(Scope("new"), CancellationToken.None))!.TurnContext;
        Assert.Contains("<上一个任务的交接", first);
        Assert.Contains("还差记住密码", first);
        Assert.Contains("不是员工的指令", first);
        // 只给一次：之后跟着第一条消息存在历史里
        Assert.Null(await ext.PrepareAsync(Scope("new"), CancellationToken.None));
        Assert.False(_code.Handoffs.Has("new"));
    }

    [Fact]
    public async Task HandoffSitsBeforeTheEditorContext()
    {
        await _code.HandoffAsync("new", "old");
        File.WriteAllText(Path.Combine(_ws, "web", "package.json"), "{}");
        File.WriteAllText(Path.Combine(_ws, "web", "App.vue"), "");
        _code.Focus("new", Path.Combine("web", "App.vue"), 3, 0, 0, Array.Empty<string>());
        var text = (await new EditorContextRunExtension(_code.Editors, _code.Handoffs).PrepareAsync(Scope("new"), CancellationToken.None))!.TurnContext;
        Assert.True(text.IndexOf("<上一个任务的交接", StringComparison.Ordinal) < text.IndexOf("<编辑器上下文", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyTasksOfTheSameProjectCanHandOff()
    {
        Assert.False(Prop<bool>(await _code.HandoffAsync("elsewhere", "old"), "ok"));
        Assert.False(Prop<bool>(await _code.HandoffAsync("new", "unknown"), "ok"));
        Assert.False(Prop<bool>(await _code.HandoffAsync("new", "new"), "ok"));
        Assert.Empty(_summarized);
        // 收窄到项目之后新开的任务：工作区在旧任务里面，算同一个项目
        Assert.True(Prop<bool>(await _code.HandoffAsync("narrowed", "old"), "ok"));
    }

    [Fact]
    public async Task ClearingCancelsAPendingSummary()
    {
        var gate = new TaskCompletionSource();
        _make = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return "不该用到";
        };
        var pending = _code.HandoffAsync("new", "old");
        _code.ClearHandoff("new");
        var r = await pending;
        Assert.False(Prop<bool>(r, "ok"));
        Assert.False(_code.Handoffs.Has("new"));
        Assert.Null(await new EditorContextRunExtension(_code.Editors, _code.Handoffs).PrepareAsync(Scope("new"), CancellationToken.None));
    }

    [Fact]
    public async Task EmptySummaryIsNotCarried()
    {
        _make = (_, _) => Task.FromResult<string?>("  ");
        Assert.False(Prop<bool>(await _code.HandoffAsync("new", "old"), "ok"));
        Assert.False(_code.Handoffs.Has("new"));
    }

    [Fact]
    public async Task NotSupportedWithoutASummarizer()
    {
        var bare = new CodeFeature(_ => _ws, _ => { });
        Assert.False(Prop<bool>(await bare.HandoffAsync("new", "old"), "ok"));
    }

    private sealed class FakeGateway : IChatGateway
    {
        public List<ChatRequest> Requests { get; } = new();

        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatTurn { Content = "交接：登录页做完" });
        }
    }

    [Fact]
    public async Task HandoffSummaryCoversTheWholeConversationAndThePreviousSummary()
    {
        var gateway = new FakeGateway();
        var context = new ContextManager(gateway, "系统提示", "之前的摘要：建了登录页");
        var summary = await context.SummarizeForHandoffAsync(new[]
        {
            ChatMessage.System("系统提示"),
            ChatMessage.User("加上记住密码"),
            ChatMessage.Assistant("好的，改了 Login.vue"),
            ChatMessage.User("再把按钮改成蓝色"),
        }, CancellationToken.None);
        Assert.Equal("交接：登录页做完", summary);
        var request = Assert.Single(gateway.Requests);
        var sent = request.Messages.Last().Content;
        Assert.Contains("之前的摘要：建了登录页", sent);
        Assert.Contains("加上记住密码", sent);
        Assert.Contains("再把按钮改成蓝色", sent); // 最后一句也摘进去（不像整理对话那样留原文）
        Assert.Contains("新开的任务", request.Messages[0].Content);
        // 这个对话本身不变
        Assert.Equal("之前的摘要：建了登录页", context.Summary);
    }

    [Fact]
    public async Task NothingToHandOffReturnsTheExistingSummary()
    {
        var gateway = new FakeGateway();
        var context = new ContextManager(gateway, "", "旧摘要");
        Assert.Equal("旧摘要", await context.SummarizeForHandoffAsync(Array.Empty<ChatMessage>(), CancellationToken.None));
        Assert.Empty(gateway.Requests);
    }
}
