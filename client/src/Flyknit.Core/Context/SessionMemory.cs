using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Context;

/// <summary>Session Memory 的状态。</summary>
public sealed class SessionMemoryState
{
    /// <summary>当前的摘要内容。</summary>
    public string Summary { get; set; } = "";

    /// <summary>最后一次提取时的消息 ID。</summary>
    public string? LastSummarizedMessageId { get; set; }

    /// <summary>最后一次提取时的 token 数。</summary>
    public int TokensAtLastExtraction { get; set; }

    /// <summary>Session Memory 是否已初始化。</summary>
    public bool Initialized { get; set; }

    /// <summary>版本号（每次更新递增）。</summary>
    public int Version { get; set; }

    /// <summary>上次更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.MinValue;
}

/// <summary>Session Memory 更新结果。</summary>
public sealed record SessionMemoryUpdate
{
    public bool Updated { get; init; }
    public int Version { get; init; }
    public int TokensProcessed { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Session Memory Agent：后台渐进式维护会话摘要。
/// 
/// 参照 Claude Code 的设计：
/// 1. 主 Context 和 Session Memory 两条线同时运行
/// 2. Session Memory 在后台异步更新，不影响主 Context
/// 3. 首次建立：对话达到 10K tokens 后
/// 4. 后续更新：距上次提取新增约 5K tokens
/// 5. Compact 时直接使用已有的 Session Memory
/// 
/// 关键区别：
/// - Session Memory 更新 ≠ Context Compact
/// - Session Memory 是提前准备的，Compact 时直接用
/// </summary>
public sealed class SessionMemoryAgent
{
    private readonly IChatGateway _gateway;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    private SessionMemoryState _state = new();
    private Task? _runningTask;
    private CancellationTokenSource? _cts;

    /// <summary>首次建立 Session Memory 的 token 阈值。</summary>
    public const int InitialThreshold = 10000;

    /// <summary>后续更新的 token 增量阈值。</summary>
    public const int IncrementalThreshold = 5000;

    /// <summary>触发更新的 Tool Call 数量门槛。</summary>
    public const int ToolCallThreshold = 3;

    /// <summary>场景。</summary>
    public string Scene { get; init; } = Scenes.Agent;

    /// <summary>模型 ID。</summary>
    public int? ModelId { get; init; }

    /// <summary>当前状态。</summary>
    public SessionMemoryState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>是否正在更新。</summary>
    public bool IsUpdating => _runningTask is { IsCompleted: false };

    /// <summary>更新完成事件。</summary>
    public event Action<SessionMemoryUpdate>? Updated;

    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public SessionMemoryAgent(IChatGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// 加载已保存的状态。
    /// </summary>
    public void LoadState(SessionMemoryState? state)
    {
        if (state is not null)
        {
            lock (_lock)
            {
                _state = state;
            }
        }
    }

    /// <summary>
    /// 检查是否需要更新，满足条件时在后台启动更新任务。
    /// 由 ContextManager 在每次请求前调用。
    /// </summary>
    /// <param name="history">当前的消息历史。</param>
    /// <param name="currentTokens">当前上下文的 token 数。</param>
    /// <returns>是否启动了更新任务。</returns>
    public bool CheckAndTrigger(IReadOnlyList<ChatMessage> history, int currentTokens)
    {
        if (IsUpdating)
        {
            return false; // 已经在更新中
        }

        lock (_lock)
        {
            // 首次建立：达到初始阈值
            if (!_state.Initialized && currentTokens >= InitialThreshold)
            {
                StartUpdate(history, currentTokens);
                return true;
            }

            // 后续更新：增量达到阈值
            if (_state.Initialized)
            {
                var tokensSinceLastExtraction = currentTokens - _state.TokensAtLastExtraction;
                if (tokensSinceLastExtraction >= IncrementalThreshold)
                {
                    // 额外检查：最近是否有足够的 Tool Call，或者对话有自然断点
                    var recentToolCalls = CountToolCallsSince(history, _state.LastSummarizedMessageId);
                    var hasNaturalBreak = HasNaturalBreakpoint(history);

                    if (recentToolCalls >= ToolCallThreshold || hasNaturalBreak)
                    {
                        StartUpdate(history, currentTokens);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 在后台启动更新任务。
    /// </summary>
    private void StartUpdate(IReadOnlyList<ChatMessage> history, int currentTokens)
    {
        if (!_updateLock.Wait(0))
        {
            return; // 已经在更新
        }

        _cts = new CancellationTokenSource();
        var messages = history.ToList(); // 复制一份，避免并发修改

        _runningTask = Task.Run(async () =>
        {
            try
            {
                await UpdateAsync(messages, currentTokens, _cts.Token);
            }
            finally
            {
                _updateLock.Release();
            }
        });
    }

    /// <summary>
    /// 同步更新（用于测试或强制更新）。
    /// </summary>
    public async Task<SessionMemoryUpdate> ForceUpdateAsync(IReadOnlyList<ChatMessage> history, int currentTokens, CancellationToken ct)
    {
        await _updateLock.WaitAsync(ct);
        try
        {
            return await UpdateAsync(history.ToList(), currentTokens, ct);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    /// <summary>
    /// 执行更新：增量提取新内容，与已有摘要合并。
    /// </summary>
    private async Task<SessionMemoryUpdate> UpdateAsync(List<ChatMessage> history, int currentTokens, CancellationToken ct)
    {
        try
        {
            // 找出上次摘要之后的新消息
            var startIndex = 0;
            if (_state.LastSummarizedMessageId is not null)
            {
                var idx = history.FindIndex(m => m.Id == _state.LastSummarizedMessageId);
                if (idx >= 0)
                {
                    startIndex = idx + 1;
                }
            }

            // 跳过系统消息
            if (startIndex == 0 && history.Count > 0 && history[0].Role == ChatRole.System)
            {
                startIndex = 1;
            }

            var newMessages = history.Skip(startIndex).ToList();
            if (newMessages.Count == 0)
            {
                return new SessionMemoryUpdate { Updated = false };
            }

            // 构建提示
            var lastMessageId = newMessages[^1].Id;
            var newSummary = await ExtractSummaryAsync(newMessages, ct);

            if (string.IsNullOrWhiteSpace(newSummary))
            {
                return new SessionMemoryUpdate { Updated = false, Error = "摘要为空" };
            }

            // 与已有摘要合并
            var mergedSummary = _state.Initialized
                ? await MergeSummariesAsync(_state.Summary, newSummary, ct)
                : newSummary;

            // 更新状态
            lock (_lock)
            {
                _state.Summary = mergedSummary.Trim();
                _state.LastSummarizedMessageId = lastMessageId;
                _state.TokensAtLastExtraction = currentTokens;
                _state.Initialized = true;
                _state.Version++;
                _state.UpdatedAt = Clock();
            }

            var update = new SessionMemoryUpdate
            {
                Updated = true,
                Version = _state.Version,
                TokensProcessed = currentTokens,
            };

            Updated?.Invoke(update);
            return update;
        }
        catch (OperationCanceledException)
        {
            return new SessionMemoryUpdate { Updated = false, Error = "已取消" };
        }
        catch (Exception ex)
        {
            return new SessionMemoryUpdate { Updated = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// 从新消息中提取摘要。
    /// </summary>
    private async Task<string> ExtractSummaryAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        var transcript = new StringBuilder();
        foreach (var m in messages)
        {
            transcript.AppendLine(RenderMessage(m));
        }

        var text = transcript.ToString();
        // 控制输入长度
        if (text.Length > 30000)
        {
            text = text[..15000] + "\n…（中间省略）…\n" + text[^15000..];
        }

        var turn = await _gateway.CompleteAsync(new ChatRequest
        {
            Scene = Scene,
            ModelId = ModelId,
            Stream = false,
            Temperature = 0.2,
            MaxTokens = 1024,
            ExtraBody = new Dictionary<string, JsonNode?> { ["enable_thinking"] = false },
            Messages = new[] { ChatMessage.System(ExtractPrompt), ChatMessage.User(text) },
        }, null, ct);

        return ContextManager.StripThink(turn.Content);
    }

    /// <summary>
    /// 合并新摘要和已有摘要。
    /// </summary>
    private async Task<string> MergeSummariesAsync(string existing, string newSummary, CancellationToken ct)
    {
        var prompt = $"""
            你需要合并两段会话摘要。第一段是之前的摘要，第二段是新增内容的摘要。
            合并时：
            - 保留所有重要信息（目标、进展、问题、决定）
            - 去除重复内容
            - 更新已完成的步骤状态
            - 保持结构清晰
            - 总长度不超过 1200 字

            【之前的摘要】
            {existing}

            【新增内容的摘要】
            {newSummary}

            请输出合并后的摘要，保持原有的结构（用户目标、已完成、重要信息、问题、当前进展）：
            """;

        var turn = await _gateway.CompleteAsync(new ChatRequest
        {
            Scene = Scene,
            ModelId = ModelId,
            Stream = false,
            Temperature = 0.2,
            MaxTokens = 1500,
            ExtraBody = new Dictionary<string, JsonNode?> { ["enable_thinking"] = false },
            Messages = new[] { ChatMessage.System(prompt) },
        }, null, ct);

        return ContextManager.StripThink(turn.Content);
    }

    /// <summary>
    /// 统计某个消息之后的 Tool Call 数量。
    /// </summary>
    private static int CountToolCallsSince(IReadOnlyList<ChatMessage> history, string? sinceMessageId)
    {
        var counting = sinceMessageId is null;
        var count = 0;
        foreach (var m in history)
        {
            if (!counting && m.Id == sinceMessageId)
            {
                counting = true;
                continue;
            }
            if (counting && m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0)
            {
                count += m.ToolCalls.Count;
            }
        }
        return count;
    }

    /// <summary>
    /// 检查最近是否有自然断点（最新的 Assistant 消息没有 Tool Call）。
    /// </summary>
    private static bool HasNaturalBreakpoint(IReadOnlyList<ChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.Assistant)
            {
                return history[i].ToolCalls.Count == 0;
            }
        }
        return false;
    }

    private static string RenderMessage(ChatMessage m)
    {
        return m.Role switch
        {
            ChatRole.User => $"[用户] {Clip(m.Content, 500)}",
            ChatRole.Assistant when m.ToolCalls.Count > 0 =>
                $"[助手] {Clip(m.Content, 300)}\n  → 调用工具: {string.Join(", ", m.ToolCalls.Select(c => c.Name))}",
            ChatRole.Assistant => $"[助手] {Clip(m.Content, 500)}",
            ChatRole.Tool => $"[工具 {m.ToolName}] {Clip(m.Content, 300)}",
            _ => "",
        };
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// 取消正在进行的更新。
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Cancel();
        _cts?.Dispose();
        _updateLock.Dispose();
    }

    private const string ExtractPrompt = """
        从以下对话片段中提取关键信息，输出简洁的摘要（不超过 500 字）：

        重点提取：
        1. 用户的目标或请求
        2. 已完成的操作和结果
        3. 重要的数据、路径、决定
        4. 遇到的问题和解决方法
        5. 当前的进展状态

        要求：
        - 保留准确的数字、路径、文件名
        - 不要编造原文中没有的内容
        - 按时间顺序组织
        - 只输出摘要本身
        """;
}
