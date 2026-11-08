using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Storage;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 每轮按问题挑的参考资料（记忆、技能、相似任务、可派遣的专家、当前时间）不进系统提示词，附在这一轮的用户消息上、
/// 跟着它保存：多轮对话里「系统提示词 + 历史」保持同一个前缀，提示词缓存才命中。
/// </summary>
public sealed class TurnContextTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-turn").FullName;

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

    [Theory]
    [InlineData(ConversationMode.Agent)]
    [InlineData(ConversationMode.Chat)]
    public void SystemPromptNoLongerChangesWithTheQuestionOrTheClock(ConversationMode mode)
    {
        var builder = new PromptBuilder(null, null);
        var (first, turn1) = builder.BuildSplit(new PromptContext { Mode = mode, Workspace = _dir, Query = "整理日报" });
        var (second, turn2) = builder.BuildSplit(new PromptContext { Mode = mode, Workspace = _dir, Query = "完全不同的问题" });

        Assert.Equal(first, second);
        Assert.DoesNotContain("当前时间", first);
        Assert.Contains("当前时间", turn1);
        Assert.Contains("当前时间", turn2);
        // 合在一起还是原来那一整段（一次性的调用照旧用 Build）
        Assert.Equal(builder.Build(new PromptContext { Mode = mode, Workspace = _dir, Query = "整理日报" }).Replace("\n", "").Replace("\r", ""),
            (first + turn1).Replace("\n", "").Replace("\r", ""));
    }

    [Fact]
    public void TranslateHasNoTurnContext()
    {
        var (system, turn) = new PromptBuilder(null, null).BuildSplit(new PromptContext { Mode = ConversationMode.Translate, TranslateTo = "vi" });
        Assert.NotEmpty(system);
        Assert.Equal("", turn);
    }

    [Fact]
    public void TurnContextIsSentAfterTheUserTextAndClearlyMarked()
    {
        var plain = ChatMessage.User("把日报汇总一下");
        Assert.Equal("把日报汇总一下", OpenAiSerializer.ToJson(plain)["content"]!.GetValue<string>());

        var withContext = ChatMessage.User("把日报汇总一下");
        withContext.TurnContext = "<相关记忆>员工喜欢表格</相关记忆>\n当前时间：2026-11-08 10:00 星期日。";
        var content = OpenAiSerializer.ToJson(withContext)["content"]!.GetValue<string>();
        Assert.StartsWith("把日报汇总一下\n\n" + TurnContextBlock.Open, content);
        Assert.EndsWith(TurnContextBlock.Close, content);
        Assert.Contains("不是员工说的话", content);
        Assert.Contains("员工喜欢表格", content);
        // 界面、存档用的原话不变
        Assert.Equal("把日报汇总一下", withContext.Content);
    }

    [Fact]
    public void TurnContextIsSavedWithTheMessageAndWrittenOnlyOnce()
    {
        var store = new ConversationStore(Path.Combine(_dir, "history.db"));
        var conv = store.Create(ConversationMode.Agent, "t");
        var user = ChatMessage.User("问题");
        store.AddMessages(conv.Id, new[] { user });

        store.SetTurnContext(user.Id, "第一次的参考资料");
        store.SetTurnContext(user.Id, "重试时又算了一遍"); // 同一轮重试：沿用第一次的，历史不变
        Assert.Equal("第一次的参考资料", store.GetMessages(conv.Id).Single().TurnContext);

        var forked = store.Fork(conv.Id, user.Id, "分支");
        Assert.Equal("第一次的参考资料", store.GetMessages(forked!.Id).Single().TurnContext);
    }

    [Fact]
    public void EstimatedSizeIncludesTheTurnContext()
    {
        var m = ChatMessage.User("问题");
        var before = Flyknit.Core.Context.TokenEstimator.Estimate(m);
        m.TurnContext = new string('记', 400);
        Assert.True(Flyknit.Core.Context.TokenEstimator.Estimate(m) >= before + 300);
    }
}
