using System.Diagnostics;

namespace Flyknit.Core.Agent;

/// <summary>一次任务里的一步。</summary>
public sealed record TraceStep
{
    /// <summary>在这次任务里的序号，从 1 开始。</summary>
    public int Index { get; init; }

    /// <summary>model（调用模型）/ tool（执行工具）/ compact（压缩上下文）/ error。</summary>
    public string Kind { get; init; } = "";

    /// <summary>工具名、或模型名。</summary>
    public string Name { get; init; } = "";

    /// <summary>给人看的一句话。</summary>
    public string Summary { get; init; } = "";

    /// <summary>ok / error / blocked / rejected / stopped。</summary>
    public string Status { get; init; } = "ok";

    public DateTimeOffset StartedAt { get; init; }
    public long DurationMs { get; init; }

    /// <summary>这一步用掉的 token（只有调用模型的步骤有）。</summary>
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }

    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// 一次任务的完整链路。
///
/// 以前的审计是一条条孤立的记录，排查时拼不出一次任务到底经过了哪些步骤、
/// 卡在哪一步、每步花了多久。用户说「结果不对」，除了翻日志没有别的手段。
///
/// 现在每次运行开一条 Trace，模型调用、工具执行、上下文压缩、出错都按顺序记进去，
/// 带耗时和 token。界面上点开就能顺着链路看到是哪一步出的问题——
/// 是模型判断错了，还是某个工具返回了不对的东西。
/// </summary>
public sealed class Trace
{
    /// <summary>一次任务最多记这么多步，避免长任务把内存和界面撑爆。</summary>
    public const int MaxSteps = 500;

    private readonly List<TraceStep> _steps = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    public string Id { get; } = Guid.NewGuid().ToString("N")[..16];
    public string ConversationId { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>整次任务的耗时。</summary>
    public long DurationMs => _clock.ElapsedMilliseconds;

    public Trace(string conversationId)
    {
        ConversationId = conversationId;
    }

    public IReadOnlyList<TraceStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return _steps.ToList();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _steps.Count;
            }
        }
    }

    /// <summary>开始计时一步；用返回的对象在结束时填上结果。</summary>
    public StepTimer Begin(string kind, string name, string summary = "") => new(this, kind, name, summary);

    internal void Add(TraceStep step)
    {
        lock (_gate)
        {
            if (_steps.Count >= MaxSteps)
            {
                return;
            }
            _steps.Add(step with { Index = _steps.Count + 1 });
        }
    }

    /// <summary>整次任务的汇总：步数、耗时、token、出错数。</summary>
    public TraceSummary Summarize()
    {
        var steps = Steps;
        return new TraceSummary(
            Id,
            ConversationId,
            StartedAt,
            DurationMs,
            steps.Count,
            steps.Count(s => s.Kind == "model"),
            steps.Count(s => s.Kind == "tool"),
            steps.Count(s => s.Status is "error" or "blocked"),
            steps.Sum(s => s.PromptTokens),
            steps.Sum(s => s.CompletionTokens),
            steps.OrderByDescending(s => s.DurationMs).FirstOrDefault());
    }

    /// <summary>计时一步。Dispose 时写入 Trace，所以用 using 包起来即可。</summary>
    public sealed class StepTimer : IDisposable
    {
        private readonly Trace _trace;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
        private readonly string _kind;
        private readonly string _name;
        private bool _written;

        public string Summary { get; set; }
        public string Status { get; set; } = "ok";
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }

        internal StepTimer(Trace trace, string kind, string name, string summary)
        {
            _trace = trace;
            _kind = kind;
            _name = name;
            Summary = summary;
        }

        public void Dispose()
        {
            if (_written)
            {
                return;
            }
            _written = true;
            _trace.Add(new TraceStep
            {
                Kind = _kind,
                Name = _name,
                Summary = Summary.Length > 300 ? Summary[..300] : Summary,
                Status = Status,
                StartedAt = _startedAt,
                DurationMs = _watch.ElapsedMilliseconds,
                PromptTokens = PromptTokens,
                CompletionTokens = CompletionTokens,
            });
        }
    }
}

/// <summary>一次任务的汇总指标。</summary>
public sealed record TraceSummary(
    string TraceId,
    string ConversationId,
    DateTimeOffset StartedAt,
    long DurationMs,
    int Steps,
    int ModelCalls,
    int ToolCalls,
    int Errors,
    int PromptTokens,
    int CompletionTokens,
    TraceStep? Slowest)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}
