namespace Flyknit.Core.Watchdog;

public enum WatchVerdict
{
    /// <summary>在推进。</summary>
    Progressing,

    /// <summary>卡住了 / 空转。</summary>
    Stalled,

    /// <summary>一直在做事，但不是员工要的。</summary>
    Drifting,
}

public enum WatchAction
{
    None,

    /// <summary>提醒 AI（附在下一个工具结果后面）+ 通知员工。</summary>
    Remind,

    /// <summary>再通知员工一次（没开自动停止，或者模型没判成）。</summary>
    Notify,

    /// <summary>走正常的停止。</summary>
    Stop,
}

/// <summary>
/// 一个任务的看门狗状态（docs/c3-watchdog.md 第 3.3 节）：正常 → 已提醒 → 已再通知 / 已停；员工说「不用管」后不再打扰。
/// 只有模型判成卡住 / 跑偏、而且之前已经提醒过、而且开了自动停止，才会停。模型调用失败（verdict 为 null）永远不停。
/// </summary>
public sealed class WatchdogPolicy
{
    public const int MaxJudgements = 6;
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(5);

    private enum Stage
    {
        Normal,
        Reminded,
        Notified,
        Dismissed,
        Stopped,
    }

    private Stage _stage = Stage.Normal;
    private DateTimeOffset? _lastJudged;

    public int Judgements { get; private set; }
    public bool Dismissed => _stage == Stage.Dismissed;
    public bool Reminded => _stage is Stage.Reminded or Stage.Notified;

    /// <summary>该不该请模型看一眼：程序判可疑、员工没说不用管、没超次数、和上次隔够 5 分钟。</summary>
    public bool ShouldJudge(bool suspicious, DateTimeOffset now) =>
        suspicious && _stage is Stage.Normal or Stage.Reminded or Stage.Notified
        && Judgements < MaxJudgements && (_lastJudged is not { } last || now - last >= MinGap);

    /// <param name="verdict">模型的判断；null = 调用失败或回答不可用（只提醒、不停）</param>
    public WatchAction OnVerdict(WatchVerdict? verdict, bool autoStop, DateTimeOffset now)
    {
        Judgements++;
        _lastJudged = now;
        if (_stage is Stage.Dismissed or Stage.Stopped)
        {
            return WatchAction.None;
        }
        if (verdict == WatchVerdict.Progressing)
        {
            _stage = Stage.Normal; // 缓过来了
            return WatchAction.None;
        }
        switch (_stage)
        {
            case Stage.Normal:
                _stage = Stage.Reminded;
                return WatchAction.Remind;
            case Stage.Reminded when verdict is not null && autoStop:
            case Stage.Notified when verdict is not null && autoStop:
                _stage = Stage.Stopped;
                return WatchAction.Stop;
            case Stage.Reminded:
                _stage = Stage.Notified;
                return WatchAction.Notify;
            default:
                return WatchAction.None;
        }
    }

    /// <summary>员工点了「不用管」：这个任务后面不再提醒。</summary>
    public void Dismiss() => _stage = Stage.Dismissed;
}
