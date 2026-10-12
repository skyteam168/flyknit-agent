using Flyknit.Core.Gateway;

namespace Flyknit.Core.Agent;

/// <summary>预算检查的结论。</summary>
public enum BudgetVerdict
{
    Ok,

    /// <summary>快用完了 / 一直没进展：提醒模型收尾。</summary>
    Nudge,

    /// <summary>用完了 / 提醒过仍没进展：停下来。</summary>
    Stop,
}

/// <summary>
/// 一次任务的全局预算：模型调用次数、累计 token、重新规划次数、连续没有进展的轮数，统一记在这里，
/// 每次调用模型之前检查一遍。
///
/// <see cref="LoopGuard"/> 只认得「同一个操作反复做」；这里管的是它认不出来的那种浪费：
/// 每一步都不一样，但做来做去没有新东西（反复读写、换着花样验证、一遍遍改计划），
/// 以及上下文越来越长、每调一次模型都把几万 token 再发一遍。
/// </summary>
public sealed class RunBudget
{
    /// <summary>一次任务最多用多少 token（输入 + 输出累计）。0 表示不限。</summary>
    public long MaxTokens { get; init; } = AgentOptions.DefaultMaxRunTokens;

    /// <summary>用到这个比例时提醒一次收尾。</summary>
    public double TokenNudgeRatio { get; init; } = 0.7;

    /// <summary>连续这么多轮没有新进展：提醒一次。</summary>
    public int NoProgressNudgeAt { get; init; } = 4;

    /// <summary>提醒后仍然连续没有进展，到这么多轮就停。</summary>
    public int NoProgressStopAt { get; init; } = 8;

    /// <summary>计划改了这么多次（不是勾掉完成的步骤，而是换了要做的事）：提醒一次。</summary>
    public int ReplanNudgeAt { get; init; } = 4;

    public long Tokens { get; private set; }
    public int ModelCalls { get; private set; }
    public int Replans { get; private set; }
    public int NoProgressRounds { get; private set; }

    /// <summary>这一轮最多的连续无进展轮数（统计用）。</summary>
    public int MaxNoProgressStreak { get; private set; }

    private bool _tokenNudged;
    private bool _progressNudged;
    private bool _replanNudged;

    /// <summary>为什么停（给收尾提醒和链路用）。</summary>
    public string StopNote { get; private set; } = "";

    public void AddModelCall(TokenUsage? usage)
    {
        ModelCalls++;
        if (usage is not null)
        {
            Tokens += usage.PromptTokens + usage.CompletionTokens;
        }
    }

    /// <summary>上游没报用量的调用：按估算计入，不能当 0。</summary>
    public void AddEstimatedModelCall(long tokens)
    {
        ModelCalls++;
        EstimatedCalls++;
        Tokens += Math.Max(0, tokens);
    }

    /// <summary>工具里花掉的 token（派遣专家）。不算这次运行自己的模型调用。</summary>
    public void AddExternal(long tokens)
    {
        Tokens += Math.Max(0, tokens);
    }

    /// <summary>用量是估算出来的模型调用次数。</summary>
    public int EstimatedCalls { get; private set; }

    /// <summary>离上限还剩多少（没有上限时是 long.MaxValue）。</summary>
    public long Remaining => MaxTokens > 0 ? Math.Max(0, MaxTokens - Tokens) : long.MaxValue;

    private readonly object _reserveGate = new();

    /// <summary>派出去还在跑的专家占着的预算。</summary>
    public long Reserved { get; private set; }

    /// <summary>
    /// 给一位专家占预算：最多 <paramref name="max"/>，并给主任务自己留 <paramref name="keep"/>。
    /// 能占到的不足 <paramref name="min"/> 时不占，返回 0。几位专家同时派出时各占各的，不会重复算同一份余量。
    /// </summary>
    public long TryReserve(long max, long keep, long min)
    {
        lock (_reserveGate)
        {
            var free = Remaining == long.MaxValue ? long.MaxValue : Remaining - Reserved - keep;
            var amount = Math.Min(max, free);
            if (amount < min)
            {
                return 0;
            }
            Reserved += amount;
            return amount;
        }
    }

    /// <summary>专家跑完：放回占着的预算（实际用掉的由 <see cref="AddExternal"/> 记入）。</summary>
    public void Release(long amount)
    {
        lock (_reserveGate)
        {
            Reserved = Math.Max(0, Reserved - amount);
        }
    }

    /// <summary>
    /// 一轮工具执行完后调用。progress：这一轮至少有一个成功的、之前没做过的操作，或者完成了计划里的一步、产出了文件。
    /// </summary>
    public void ObserveRound(bool progress)
    {
        NoProgressRounds = progress ? 0 : NoProgressRounds + 1;
        MaxNoProgressStreak = Math.Max(MaxNoProgressStreak, NoProgressRounds);
        if (progress)
        {
            _progressNudged = false;
        }
    }

    /// <summary>计划被改过（要做的事变了，不是勾掉完成的步骤）。</summary>
    public void ObserveReplan() => Replans++;

    /// <summary>调用模型之前检查。返回 Nudge 时 <paramref name="note"/> 是要附给模型的提醒。</summary>
    public BudgetVerdict Check(out string note)
    {
        note = "";
        if (MaxTokens > 0 && Tokens >= MaxTokens)
        {
            StopNote = $"这一轮已经用了 {Tokens:N0} tokens，到达单次任务的上限（{MaxTokens:N0}）";
            return BudgetVerdict.Stop;
        }
        if (NoProgressRounds >= NoProgressStopAt)
        {
            StopNote = $"已经连续 {NoProgressRounds} 轮没有新的进展（做的都是之前做过的操作，或者都失败了）";
            return BudgetVerdict.Stop;
        }
        if (NoProgressRounds >= NoProgressNudgeAt && !_progressNudged)
        {
            _progressNudged = true;
            note = $"""


                【系统提醒】已经连续 {NoProgressRounds} 轮没有新的进展：做的都是之前做过的操作，或者都失败了。
                目标已经达成就直接总结回答；方法不奏效就换一种；需要用户提供信息或确认就停下来问。不要再重复验证同样的东西。
                """;
            return BudgetVerdict.Nudge;
        }
        if (MaxTokens > 0 && !_tokenNudged && Tokens >= MaxTokens * TokenNudgeRatio)
        {
            _tokenNudged = true;
            note = $"""


                【系统提醒】这一轮已经用了 {Tokens:N0} tokens（上限 {MaxTokens:N0}）。请抓紧收尾：只做完成目标必需的步骤，
                已经达成目标就直接总结回答，不要再做额外的检查和美化。
                """;
            return BudgetVerdict.Nudge;
        }
        if (Replans >= ReplanNudgeAt && !_replanNudged)
        {
            _replanNudged = true;
            note = $"""


                【系统提醒】计划已经改了 {Replans} 次。先按现在的计划做下去，不要频繁推翻重来；如果确实走不通，停下来告诉用户卡在哪里。
                """;
            return BudgetVerdict.Nudge;
        }
        return BudgetVerdict.Ok;
    }
}
