using System.Text;
using System.Text.Json.Nodes;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Context;

public sealed class ContextOptions
{
    /// <summary>模型上下文长度未知时使用的默认值。</summary>
    public int DefaultContextLength { get; init; } = 131072;

    /// <summary>为模型回答预留的 token。</summary>
    public int ReserveOutputTokens { get; init; } = 8192;

    /// <summary>超过预算的这个比例时，先裁剪较早的工具输出（微压缩）。</summary>
    public double PruneRatio { get; init; } = 0.35;

    /// <summary>超过预算的这个比例时，把较早的对话压缩成摘要（自动压缩）。</summary>
    /// <remarks>
    /// 参照 Cursor/Claude Code 的设计，200K 上下文在约 187K 时触发（约 93%）。
    /// 但对于 128K 上下文，如果等到 75% 才压缩（~92K tokens），用户很难看到压缩动画。
    /// 降低到 50% 可以让压缩更早发生，同时后台 Session Memory 会提前准备好摘要。
    /// </remarks>
    public double CompactRatio { get; init; } = 0.50;

    /// <summary>压缩时保留原文的最近内容占预算的比例。</summary>
    public double KeepRecentRatio { get; init; } = 0.25;

    /// <summary>最近多少条工具输出不裁剪。</summary>
    public int KeepRecentToolResults { get; init; } = 4;

    /// <summary>裁剪后每条工具输出保留的字符数。</summary>
    public int PrunedToolChars { get; init; } = 1200;
}

/// <summary>一次压缩的结果，宿主据此保存摘要，下次对话直接从摘要之后开始。</summary>
/// <summary>压缩进度，用来在对话里显示进度条。</summary>
/// <param name="Phase">scanning（挑要压缩的部分）/ summarizing（让模型提炼）/ done</param>
public sealed record CompactionProgress(string Phase, int Percent, int MessagesCompacted);

public sealed class CompactionInfo
{
    public required string Summary { get; init; }

    /// <summary>被摘要覆盖的最后一条消息 ID（含）。</summary>
    public required string UptoMessageId { get; init; }

    public int MessagesCompacted { get; init; }
    public int TokensBefore { get; init; }
    public int TokensAfter { get; init; }
}

/// <summary>
/// 短期记忆（工作上下文）管理，参照 Claude Code 的 auto-compact：
/// 1. 微压缩：上下文超过预算一半时，较早的大段工具输出只保留开头，需要时让模型重新读取；
/// 2. 自动压缩：超过 75% 时，用模型把较早的对话整理成结构化摘要，最近几轮保留原文；
/// 3. 兜底：摘要失败时更激进地裁剪工具输出，保证请求不超长。
/// 
/// 优化：后台渐进式 Session Memory
/// - Session Memory Agent 在后台异步更新摘要，不影响主 Context
/// - 首次建立：对话达到 10K tokens 后
/// - 后续更新：距上次提取新增约 5K tokens
/// - Compact 时优先使用已有的 Session Memory，减少等待
/// 
/// 摘要放在系统提示词末尾（部分模型只接受一条开头的 system 消息）。
/// </summary>
public sealed class ContextManager
{
    private readonly IChatGateway _gateway;
    private readonly ContextOptions _options;
    private readonly string _baseSystemPrompt;
    private readonly SessionMemoryAgent? _sessionMemory;

    /// <summary>当前生效的摘要（之前的对话压缩结果）。</summary>
    public string? Summary { get; private set; }

    /// <summary>Session Memory Agent（后台渐进式维护摘要）。</summary>
    public SessionMemoryAgent? SessionMemory => _sessionMemory;

    /// <summary>模型上下文长度，收到服务端响应后更新。</summary>
    public int ContextLength { get; set; }

    /// <summary>最近一次请求模型返回的真实输入 token 数。</summary>
    public int? LastPromptTokens { get; private set; }

    /// <summary>压缩时使用的场景与模型（与对话一致）。</summary>
    public string Scene { get; init; } = Scenes.Agent;
    public int? ModelId { get; init; }

    public event Action<CompactionInfo>? Compacted;

    /// <summary>压缩过程中的进度（界面上的进度条）。</summary>
    public event Action<CompactionProgress>? Progress;

    /// <param name="enableSessionMemory">是否启用后台 Session Memory（渐进式更新）。</param>
    /// <param name="sessionMemoryState">已有的 Session Memory 状态（从上次会话恢复）。</param>
    public ContextManager(IChatGateway gateway, string baseSystemPrompt, string? existingSummary = null, int contextLength = 0, 
        ContextOptions? options = null, bool enableSessionMemory = true, SessionMemoryState? sessionMemoryState = null)
    {
        _gateway = gateway;
        _options = options ?? new ContextOptions();
        _baseSystemPrompt = baseSystemPrompt;
        Summary = string.IsNullOrWhiteSpace(existingSummary) ? null : existingSummary;
        ContextLength = contextLength;

        if (enableSessionMemory)
        {
            _sessionMemory = new SessionMemoryAgent(gateway);
            if (sessionMemoryState is not null)
            {
                _sessionMemory.LoadState(sessionMemoryState);
                // 如果有已初始化的 Session Memory，使用它的摘要
                if (sessionMemoryState.Initialized && !string.IsNullOrWhiteSpace(sessionMemoryState.Summary))
                {
                    Summary ??= sessionMemoryState.Summary;
                }
            }
        }
    }

    public int Budget => Math.Max(4096, (ContextLength > 0 ? ContextLength : _options.DefaultContextLength) - _options.ReserveOutputTokens);

    /// <summary>
    /// 当前的任务计划。较早的消息被压缩成摘要后，update_plan 的调用也跟着没了，模型会忘了计划走到哪一步；
    /// 所以有摘要时把计划原样（不经模型改写）附在摘要后面。
    /// </summary>
    public Func<IReadOnlyList<Tools.PlanItem>>? Plan { get; init; }

    /// <summary>系统提示词 + 摘要（+ 未完成的任务计划）。</summary>
    public string SystemPrompt
    {
        get
        {
            var prompt = WithSummary(_baseSystemPrompt, Summary);
            if (Summary is not null && Plan?.Invoke() is { Count: > 0 } plan && Tools.TaskPlan.HasOpenSteps(plan))
            {
                prompt += "\n\n" + Tools.TaskPlan.Render(plan);
            }
            return prompt;
        }
    }

    public static string WithSummary(string systemPrompt, string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return systemPrompt;
        }
        return systemPrompt.TrimEnd() + "\n\n<较早对话的摘要>\n以下是本次对话较早部分的摘要（原文已压缩），继续工作时以此为准：\n"
               + summary.Trim() + "\n</较早对话的摘要>";
    }

    /// <summary>记录模型返回的真实用量与上下文长度。</summary>
    public void Observe(ChatTurn turn)
    {
        if (turn.ContextLength > 0)
        {
            ContextLength = turn.ContextLength;
        }
        if (turn.Usage is { PromptTokens: > 0 } usage)
        {
            LastPromptTokens = usage.PromptTokens;
        }
    }

    /// <summary>当前上下文的 token 数：估算值与上次真实值取较大者（真实值更准，但新增内容只能估算）。</summary>
    public int Measure(IReadOnlyList<ChatMessage> history) => TokenEstimator.Estimate(history);

    /// <summary>每次调用模型前执行：必要时裁剪或压缩 history（原地替换元素，不修改消息对象本身）。</summary>
    public async Task<CompactionInfo?> PrepareAsync(List<ChatMessage> history, CancellationToken ct)
    {
        var before = Measure(history);

        // 后台 Session Memory：检查是否需要触发更新（不阻塞主流程）
        _sessionMemory?.CheckAndTrigger(history, before);

        if (before <= Budget * _options.PruneRatio)
        {
            return null;
        }

        PruneToolOutputs(history, _options.KeepRecentToolResults, _options.PrunedToolChars);
        if (Measure(history) <= Budget * _options.CompactRatio)
        {
            return null;
        }

        CompactionInfo? info = null;
        try
        {
            info = await CompactAsync(history, before, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 摘要失败时走兜底裁剪
        }

        if (Measure(history) > Budget * 0.9)
        {
            PruneToolOutputs(history, keepRecent: 1, keepChars: 400);
        }
        return info;
    }

    /// <summary>微压缩：较早的大段工具输出只保留开头。</summary>
    public static int PruneToolOutputs(List<ChatMessage> history, int keepRecent, int keepChars)
    {
        var toolIndexes = history.Select((m, i) => (m, i)).Where(x => x.m.Role == ChatRole.Tool).Select(x => x.i).ToList();
        var pruned = 0;
        foreach (var i in toolIndexes.Take(Math.Max(0, toolIndexes.Count - keepRecent)))
        {
            var m = history[i];
            if (m.Content.Length <= keepChars + 200)
            {
                continue;
            }
            history[i] = Clone(m, m.Content[..keepChars] + $"\n…（较早的工具输出已省略 {m.Content.Length - keepChars} 个字符，需要时请重新读取）");
            pruned++;
        }
        return pruned;
    }

    /// <summary>选出压缩边界：保留最近约 KeepRecentRatio 预算的原文，边界落在一条用户消息上，避免拆开工具调用与结果。</summary>
    public int FindBoundary(IReadOnlyList<ChatMessage> history)
    {
        var start = history.Count > 0 && history[0].Role == ChatRole.System ? 1 : 0;
        var keep = Budget * _options.KeepRecentRatio;
        var acc = 0;
        var boundary = history.Count;
        for (var i = history.Count - 1; i > start; i--)
        {
            acc += TokenEstimator.Estimate(history[i]);
            boundary = i;
            if (acc >= keep)
            {
                break;
            }
        }
        // 向后移动到最近的用户消息；没有的话退到一条不是工具结果的消息
        var userAt = Enumerable.Range(boundary, history.Count - boundary).FirstOrDefault(i => history[i].Role == ChatRole.User, -1);
        if (userAt > start)
        {
            return userAt;
        }
        for (var i = boundary; i < history.Count; i++)
        {
            if (history[i].Role == ChatRole.Assistant && i > start)
            {
                return i; // 单个超长任务内部：从某次模型调用处切开
            }
        }
        return -1;
    }

    private async Task<CompactionInfo?> CompactAsync(List<ChatMessage> history, int tokensBefore, CancellationToken ct)
    {
        var start = history.Count > 0 && history[0].Role == ChatRole.System ? 1 : 0;
        var boundary = FindBoundary(history);
        if (boundary <= start)
        {
            return null;
        }
        var old = history.Skip(start).Take(boundary - start).ToList();
        if (old.Count == 0)
        {
            return null;
        }

        Progress?.Invoke(new CompactionProgress("scanning", 5, old.Count));

        // 优先使用后台 Session Memory（如果已经准备好且覆盖了足够的内容）
        string? summary = null;
        var sessionState = _sessionMemory?.State;
        if (sessionState?.Initialized == true && !string.IsNullOrWhiteSpace(sessionState.Summary))
        {
            // 检查 Session Memory 是否覆盖了要压缩的部分
            var lastSummarizedIdx = old.FindIndex(m => m.Id == sessionState.LastSummarizedMessageId);
            if (lastSummarizedIdx >= old.Count * 0.5) // 至少覆盖了一半
            {
                Progress?.Invoke(new CompactionProgress("summarizing", 50, old.Count));
                summary = sessionState.Summary;
                // 只需要补充 Session Memory 之后的部分
                var remaining = old.Skip(lastSummarizedIdx + 1).ToList();
                if (remaining.Count > 0)
                {
                    var additionalSummary = await SummarizeAsync(remaining, ct);
                    if (!string.IsNullOrWhiteSpace(additionalSummary))
                    {
                        summary += "\n\n【后续进展】\n" + additionalSummary.Trim();
                    }
                }
            }
        }

        // 没有可用的 Session Memory，同步生成摘要
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = await SummarizeAsync(old, ct);
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            Progress?.Invoke(new CompactionProgress("done", 100, 0));
            return null;
        }

        var upto = old[^1].Id;
        Summary = summary.Trim();
        history.RemoveRange(start, boundary - start);
        if (start == 1)
        {
            history[0] = ChatMessage.System(SystemPrompt);
        }
        else
        {
            history.Insert(0, ChatMessage.System(SystemPrompt));
        }
        if (history.Count > 1 && history[1].Role != ChatRole.User)
        {
            // 在一个长任务中间切开时，补一条用户消息，保证消息顺序合法
            history.Insert(1, ChatMessage.User(ContinueMarker));
        }

        var info = new CompactionInfo
        {
            Summary = Summary,
            UptoMessageId = upto,
            MessagesCompacted = old.Count,
            TokensBefore = tokensBefore,
            TokensAfter = Measure(history),
        };
        Progress?.Invoke(new CompactionProgress("done", 100, old.Count));
        Compacted?.Invoke(info);
        return info;
    }

    private async Task<string?> SummarizeAsync(List<ChatMessage> old, CancellationToken ct)
    {
        var transcript = new StringBuilder();
        if (Summary is not null)
        {
            transcript.AppendLine("【之前的摘要】").AppendLine(Summary).AppendLine();
        }
        foreach (var m in old)
        {
            transcript.AppendLine(Render(m));
        }
        // 输入本身也要控制在预算内：太长时只保留每条消息的开头
        var text = transcript.ToString();
        var maxChars = (int)(Budget * 0.6 / 0.75);
        if (text.Length > maxChars)
        {
            text = text[..(maxChars / 2)] + "\n…（中间省略）…\n" + text[^(maxChars / 2)..];
        }

        var sink = new ProgressSink(old.Count, p => Progress?.Invoke(p));
        var turn = await _gateway.CompleteAsync(new ChatRequest
        {
            Scene = Scene,
            ModelId = ModelId,
            Stream = true,
            Temperature = 0.2,
            MaxTokens = 2048,
            ExtraBody = new Dictionary<string, JsonNode?> { ["enable_thinking"] = false },
            Messages = new[] { ChatMessage.System(CompactPrompt), ChatMessage.User(text) },
        }, sink, ct);
        return StripThink(turn.Content);
    }

    /// <summary>
    /// 摘要是流式返回的，按已经收到的字数折算进度（10% → 95%）。
    /// 摘要长度事先不知道，用 ExpectedSummaryChars 作参照，所以进度是估算，但始终单调递增、不会倒退。
    /// </summary>
    private sealed class ProgressSink : IStreamSink
    {
        private const int ExpectedSummaryChars = 1600;

        private readonly int _messages;
        private readonly Action<CompactionProgress> _report;
        private int _chars;
        private int _lastPercent = 10;

        public ProgressSink(int messages, Action<CompactionProgress> report)
        {
            _messages = messages;
            _report = report;
            report(new CompactionProgress("summarizing", 10, messages));
        }

        public void OnContent(string delta)
        {
            _chars += delta.Length;
            var percent = Math.Clamp(10 + _chars * 85 / ExpectedSummaryChars, 10, 95);
            if (percent > _lastPercent)
            {
                _lastPercent = percent;
                _report(new CompactionProgress("summarizing", percent, _messages));
            }
        }

        public void OnReasoning(string delta)
        {
        }
    }

    private static string Render(ChatMessage m)
    {
        switch (m.Role)
        {
            case ChatRole.User:
                var files = m.Attachments.Count > 0 ? $"（附件：{string.Join("、", m.Attachments.Select(a => a.LocalPath))}）" : "";
                return $"[用户] {m.Content}{files}";
            case ChatRole.Assistant:
                var sb = new StringBuilder("[助手] ").Append(m.Content);
                foreach (var c in m.ToolCalls)
                {
                    sb.Append($"\n  → 调用 {c.Name} {Clip(c.ArgumentsJson, 400)}");
                }
                return sb.ToString();
            case ChatRole.Tool:
                return $"[工具结果 {m.ToolName}] {Clip(m.Content, 800)}";
            default:
                return "";
        }
    }

    internal static string StripThink(string text)
    {
        var end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return (end >= 0 ? text[(end + 8)..] : text).Trim();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static ChatMessage Clone(ChatMessage m, string content) => new()
    {
        Id = m.Id,
        Role = m.Role,
        Content = content,
        Reasoning = m.Reasoning,
        ToolCalls = m.ToolCalls,
        ToolCallId = m.ToolCallId,
        ToolName = m.ToolName,
        Attachments = m.Attachments,
        CreatedAt = m.CreatedAt,
    };

    public const string ContinueMarker = "（较早的内容已压缩为摘要，请继续完成当前任务）";

    public const string CompactPrompt = """
        你负责压缩一段 AI 办公助手与用户的对话记录，让助手在看不到原文的情况下也能无缝继续工作。
        按下面的结构输出摘要（中文，Markdown，不超过 1200 字），没有内容的部分写“无”：

        ## 用户的目标与要求
        用户要完成什么，提出过的具体要求、格式、标准。
        ## 已完成的工作
        做过的关键操作和结果，涉及的文件、文件夹、命令要写完整路径。
        ## 重要信息与决定
        读到的关键数据和结论、双方确认过的决定、用户纠正过的地方、用户表达的偏好。
        ## 遇到的问题
        失败的操作、原因，以及是怎么解决的。
        ## 当前进展与下一步
        做到哪一步了，还剩哪些步骤没完成。

        要求：保留准确的数字、路径、文件名、专有名词；不要编造原文中没有的内容；只输出摘要本身。
        """;
}
