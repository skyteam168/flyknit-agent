using Xunit;
using Flyknit.Core.Agent;
using Flyknit.Core.Security;

namespace Flyknit.Core.Tests;

/// <summary>手机发来的任务要确认时同时问手机和电脑：谁先答算谁的，另一边收起来；都不答超时拒绝。</summary>
public sealed class FirstAnswerTests
{
    private static ConfirmRequest Req() => new()
    {
        ConversationId = "c1",
        Call = new Flyknit.Core.Chat.ToolCall("call1", "write_file", "{}"),
        Summary = "写入 a.txt",
        Decision = PolicyDecision.Confirm("需要确认"),
    };

    /// <summary>电脑上的卡片：手动回答，被取消时像界面那样返回拒绝。</summary>
    private sealed class Card : IConfirmationHandler
    {
        public readonly TaskCompletionSource<ConfirmChoice> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled;

        public async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            await using var reg = ct.Register(() => { Cancelled = true; Answer.TrySetResult(ConfirmChoice.Reject); });
            return await Answer.Task;
        }
    }

    [Fact]
    public async Task ThePcCanAnswerWhenThePhoneNeverSeesTheRequest()
    {
        var card = new Card();
        var phoneStopped = false;
        var race = FirstAnswer.RaceAsync(Req(), card, async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { phoneStopped = true; throw; }
            return null;
        }, TimeSpan.FromMinutes(5), default);

        card.Answer.SetResult(ConfirmChoice.AllowForSession);
        var (choice, by) = await race;

        Assert.Equal(ConfirmChoice.AllowForSession, choice);
        Assert.Equal(FirstAnswer.Source.Pc, by);
        Assert.True(phoneStopped);
    }

    [Fact]
    public async Task ThePhoneAnsweringFirstClosesThePcCard()
    {
        var card = new Card();
        var (choice, by) = await FirstAnswer.RaceAsync(Req(), card, _ => Task.FromResult<bool?>(true), TimeSpan.FromMinutes(5), default);

        Assert.Equal(ConfirmChoice.AllowOnce, choice);
        Assert.Equal(FirstAnswer.Source.Phone, by);
        Assert.True(card.Cancelled);

        var rejected = await FirstAnswer.RaceAsync(Req(), new Card(), _ => Task.FromResult<bool?>(false), TimeSpan.FromMinutes(5), default);
        Assert.Equal((ConfirmChoice.Reject, FirstAnswer.Source.Phone), rejected);
    }

    [Fact]
    public async Task APhoneThatFailsOrExpiresLeavesTheDecisionToThePc()
    {
        var card = new Card();
        var race = FirstAnswer.RaceAsync(Req(), card, _ => throw new HttpRequestException("断网"), TimeSpan.FromMinutes(5), default);
        await Task.Delay(50);
        Assert.False(race.IsCompleted);

        card.Answer.SetResult(ConfirmChoice.AllowOnce);
        Assert.Equal((ConfirmChoice.AllowOnce, FirstAnswer.Source.Pc), await race);

        var expired = new Card();
        var race2 = FirstAnswer.RaceAsync(Req(), expired, _ => Task.FromResult<bool?>(null), TimeSpan.FromMinutes(5), default);
        await Task.Delay(50);
        Assert.False(race2.IsCompleted);
        expired.Answer.SetResult(ConfirmChoice.Reject);
        Assert.Equal((ConfirmChoice.Reject, FirstAnswer.Source.Pc), await race2);
    }

    [Fact]
    public async Task NobodyAnsweringEndsInARejectionAfterTheTimeout()
    {
        var card = new Card();
        var (choice, by) = await FirstAnswer.RaceAsync(Req(), card, async ct => { await Task.Delay(Timeout.Infinite, ct); return null; },
            TimeSpan.FromMilliseconds(200), default);

        Assert.Equal(ConfirmChoice.Reject, choice);
        Assert.Equal(FirstAnswer.Source.Timeout, by);
        Assert.True(card.Cancelled);
    }

    [Fact]
    public async Task StoppingTheTaskCancelsTheWait()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FirstAnswer.RaceAsync(Req(), new Card(),
            async ct => { await Task.Delay(Timeout.Infinite, ct); return null; }, TimeSpan.FromMinutes(5), cts.Token));
    }
}
