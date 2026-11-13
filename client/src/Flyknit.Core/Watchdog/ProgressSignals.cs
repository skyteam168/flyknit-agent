using System.Security.Cryptography;
using System.Text;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Watchdog;

/// <summary>最近一步做了什么（给模型判断用的摘要，已脱敏、已截短）。</summary>
public sealed record StepNote(string Tool, string Brief, string ResultHead, bool Ok);

/// <summary>
/// 一个任务的进展信号（docs/c3-watchdog.md 第 3.1 节）。只在内存里，跟着任务走、跨轮不清零。
///
/// 算进展：这个任务里没做过的操作（只读操作连着做太多次后不再算）、计划完成的步数增加、产出 / 改动了文件、
/// 回答员工、后台命令有新输出。
/// 暂停计时（不算进展也不算没进展）：工具正在执行（编译、跑测试）、等员工确认、等员工回答、暂停中。
/// 可疑 = 不暂停计时的时间里连续 N 分钟没进展，并且这段时间至少走了 <see cref="MinSteps"/> 步（「一直在动但没进展」）。
/// 只是慢、每一步都很久，不算可疑。
/// </summary>
public sealed class ProgressSignals
{
    /// <summary>只读类操作：连着做超过 <see cref="ReadOnlyStreakLimit"/> 次后，新的只读操作不再算进展（只看不做）。</summary>
    public static readonly IReadOnlySet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file", "list_dir", "search_files", "grep", "git_status", "git_diff", "git_log", "memory_search",
        "search_skills", "load_skill", "browser_read", "pr_status",
    };

    /// <summary>结果变了就算进展的工具（后台命令的新输出），不看参数是不是做过。</summary>
    public static readonly IReadOnlySet<string> PollTools = new HashSet<string>(StringComparer.Ordinal) { "shell_output" };

    public const int ReadOnlyStreakLimit = 15;
    public const int MinSteps = 8;
    public const int RecentSteps = 20;

    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastPollResult = new(StringComparer.Ordinal);
    private readonly LinkedList<StepNote> _recent = new();

    private DateTimeOffset _since;
    private TimeSpan _paused;
    private int _holds;
    private DateTimeOffset _holdSince;
    private int _stepsSinceProgress;
    private int _readOnlyStreak;
    private int _planDone;

    public ProgressSignals(string goal, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        Goal = goal;
        StartedAt = _since = _now();
    }

    /// <summary>这件事是什么（第一句话）。</summary>
    public string Goal { get; }
    public DateTimeOffset StartedAt { get; }
    public IReadOnlyList<PlanItem> Plan { get; private set; } = Array.Empty<PlanItem>();
    public int TotalSteps { get; private set; }

    public IReadOnlyList<StepNote> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToList();
            }
        }
    }

    // ---------- 暂停计时 ----------

    /// <summary>开始一段「不该算没进展」的时间（工具执行中、等确认、等回答、暂停）。可以嵌套。</summary>
    public void Hold()
    {
        lock (_gate)
        {
            if (_holds++ == 0)
            {
                _holdSince = _now();
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_holds == 0)
            {
                return;
            }
            if (--_holds == 0)
            {
                _paused += _now() - _holdSince;
            }
        }
    }

    /// <summary>一轮开始 / 结束时调：被停止的那一轮可能有没配对的 Hold（工具做到一半被停），别让计时一直暂停。</summary>
    public void ResetHolds()
    {
        lock (_gate)
        {
            if (_holds > 0)
            {
                _paused += _now() - _holdSince;
            }
            _holds = 0;
        }
    }

    public bool Holding
    {
        get
        {
            lock (_gate)
            {
                return _holds > 0;
            }
        }
    }

    // ---------- 信号 ----------

    public void ToolStarted() => Hold();

    /// <summary>一个工具做完了：记一步，按是不是新操作判断有没有进展。</summary>
    public void ToolFinished(ToolCall call, ToolResult result)
    {
        Release();
        lock (_gate)
        {
            TotalSteps++;
            _stepsSinceProgress++;
            _recent.AddLast(new StepNote(call.Name, WatchdogJudge.BriefOf(call), WatchdogJudge.Head(result.Output, 120), result.Ok));
            while (_recent.Count > RecentSteps)
            {
                _recent.RemoveFirst();
            }

            if (call.Name == "update_plan")
            {
                return; // 计划另算（PlanUpdated）
            }
            var progress = result.Outputs.Count > 0;
            if (PollTools.Contains(call.Name))
            {
                var hash = Hash(result.Output);
                var key = LoopGuard.Signature(call);
                progress |= !_lastPollResult.TryGetValue(key, out var last) || last != hash;
                _lastPollResult[key] = hash;
            }
            else if (_seen.Add(LoopGuard.Signature(call)) && result.Ok)
            {
                if (ReadOnlyTools.Contains(call.Name))
                {
                    _readOnlyStreak++;
                    progress |= _readOnlyStreak <= ReadOnlyStreakLimit;
                }
                else
                {
                    progress = true;
                }
            }
            if (!ReadOnlyTools.Contains(call.Name) && result.Ok)
            {
                _readOnlyStreak = 0;
            }
            if (progress)
            {
                ProgressLocked();
            }
        }
    }

    /// <summary>计划更新：完成的步数增加算进展。</summary>
    public void PlanUpdated(IReadOnlyList<PlanItem> plan)
    {
        lock (_gate)
        {
            Plan = plan.ToList();
            var done = plan.Count(p => p.Status == "completed");
            if (done > _planDone)
            {
                ProgressLocked();
            }
            _planDone = Math.Max(_planDone, done);
        }
    }

    /// <summary>产出 / 改动了文件、回答了员工。</summary>
    public void Progressed()
    {
        lock (_gate)
        {
            ProgressLocked();
        }
    }

    /// <summary>重新开始计时（模型说在推进，或者刚提醒过：再给 N 分钟看看）。</summary>
    public void Restart() => Progressed();

    private void ProgressLocked()
    {
        var now = _now();
        _since = now;
        _paused = TimeSpan.Zero;
        _stepsSinceProgress = 0;
        if (_holds > 0)
        {
            _holdSince = now;
        }
    }

    // ---------- 判断 ----------

    /// <summary>不暂停计时的时间里，多久没进展了。</summary>
    public TimeSpan IdleFor
    {
        get
        {
            lock (_gate)
            {
                var now = _now();
                var held = _paused + (_holds > 0 ? now - _holdSince : TimeSpan.Zero);
                var idle = now - _since - held;
                return idle < TimeSpan.Zero ? TimeSpan.Zero : idle;
            }
        }
    }

    public int StepsSinceProgress
    {
        get
        {
            lock (_gate)
            {
                return _stepsSinceProgress;
            }
        }
    }

    /// <summary>一直在动但没进展：N 分钟没进展且至少走了 <see cref="MinSteps"/> 步。正在暂停计时时不判。</summary>
    public bool Suspicious(TimeSpan threshold) => !Holding && IdleFor >= threshold && StepsSinceProgress >= MinSteps;

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.Trim())));
}
