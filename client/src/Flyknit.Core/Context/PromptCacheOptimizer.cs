using System.Security.Cryptography;
using System.Text;
using Flyknit.Core.Chat;

namespace Flyknit.Core.Context;

/// <summary>Prompt 前缀的缓存状态。</summary>
public sealed record PrefixCacheInfo
{
    /// <summary>前缀的哈希值（用于判断是否变化）。</summary>
    public string PrefixHash { get; init; } = "";

    /// <summary>前缀的 token 估算。</summary>
    public int PrefixTokens { get; init; }

    /// <summary>前缀是否和上次相同（可能命中 Prompt Cache）。</summary>
    public bool StablePrefix { get; init; }

    /// <summary>上次请求后 API 返回的 cached_tokens（如果有）。</summary>
    public int? CachedTokens { get; init; }
}

/// <summary>
/// Prompt Cache 优化器：优化上下文编排以提高 Prompt Cache 命中率。
/// 
/// 参照 Claude Code 的设计：
/// - 缓存是 Model/API 推理层的能力，不是 Harness 自己实现
/// - Harness 的作用是组织 Context，让稳定内容尽量稳定、连续
/// - 稳定 Prefix = System Prompt + Rules + Tools + Memory + History 前半部分
/// 
/// 优化策略：
/// 1. 将稳定内容放在前面（System、指令、工具定义、记忆）
/// 2. 动态内容放在后面（当前问题、最新的工具结果）
/// 3. 跟踪前缀的变化，统计 Cache 命中情况
/// </summary>
public sealed class PromptCacheOptimizer
{
    private string? _lastPrefixHash;
    private int _lastPrefixTokens;
    private int _cacheHits;
    private int _cacheMisses;

    /// <summary>Cache 命中次数。</summary>
    public int CacheHits => _cacheHits;

    /// <summary>Cache 未命中次数。</summary>
    public int CacheMisses => _cacheMisses;

    /// <summary>Cache 命中率。</summary>
    public double HitRate => _cacheHits + _cacheMisses == 0 ? 0 : (double)_cacheHits / (_cacheHits + _cacheMisses);

    /// <summary>
    /// 分析当前上下文的 Prompt Cache 状态。
    /// </summary>
    /// <param name="systemPrompt">系统提示词（含指令、记忆等）。</param>
    /// <param name="history">消息历史。</param>
    /// <param name="toolDefinitions">工具定义（JSON）。</param>
    /// <returns>缓存状态信息。</returns>
    public PrefixCacheInfo Analyze(string systemPrompt, IReadOnlyList<ChatMessage> history, string? toolDefinitions = null)
    {
        // 计算稳定前缀：System Prompt + Tool Definitions + 历史消息的前半部分
        var prefixBuilder = new StringBuilder();
        prefixBuilder.AppendLine(systemPrompt);

        if (!string.IsNullOrEmpty(toolDefinitions))
        {
            prefixBuilder.AppendLine(toolDefinitions);
        }

        // 历史消息的前半部分（较早的消息更稳定）
        var stableMessageCount = Math.Max(1, history.Count / 2);
        for (var i = 0; i < stableMessageCount && i < history.Count; i++)
        {
            var msg = history[i];
            prefixBuilder.AppendLine($"{msg.Role}:{msg.Content?.Length ?? 0}");
            // 只用消息的哈希特征，不包含完整内容
            if (!string.IsNullOrEmpty(msg.Id))
            {
                prefixBuilder.AppendLine(msg.Id);
            }
        }

        var prefixContent = prefixBuilder.ToString();
        var prefixHash = ComputeHash(prefixContent);
        var prefixTokens = TokenEstimator.Estimate(systemPrompt) +
                          (toolDefinitions is not null ? TokenEstimator.Estimate(toolDefinitions) : 0) +
                          history.Take(stableMessageCount).Sum(m => TokenEstimator.Estimate(m));

        var stablePrefix = prefixHash == _lastPrefixHash;

        if (stablePrefix)
        {
            Interlocked.Increment(ref _cacheHits);
        }
        else
        {
            Interlocked.Increment(ref _cacheMisses);
        }

        _lastPrefixHash = prefixHash;
        _lastPrefixTokens = prefixTokens;

        return new PrefixCacheInfo
        {
            PrefixHash = prefixHash,
            PrefixTokens = prefixTokens,
            StablePrefix = stablePrefix,
        };
    }

    /// <summary>
    /// 记录 API 返回的缓存 token 数。
    /// </summary>
    public void RecordCachedTokens(int cachedTokens)
    {
        // 可以用于统计实际的 Cache 效果
        // API 返回的 cached_tokens > 0 说明 Prompt Cache 真正命中了
    }

    /// <summary>
    /// 优化消息顺序以提高 Cache 命中率。
    /// 主要是确保 System 消息在最前面，且内容稳定。
    /// </summary>
    public static List<ChatMessage> OptimizeOrder(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();

        // 1. System 消息放最前面
        var system = messages.FirstOrDefault(m => m.Role == ChatRole.System);
        if (system is not null)
        {
            result.Add(system);
        }

        // 2. 其余消息按原顺序
        foreach (var msg in messages)
        {
            if (msg.Role != ChatRole.System)
            {
                result.Add(msg);
            }
        }

        return result;
    }

    /// <summary>
    /// 构建优化的系统提示词。
    /// 将最稳定的内容放在最前面。
    /// </summary>
    public static string BuildOptimizedSystemPrompt(
        string basePrompt,
        string? instructions,
        string? memory,
        string? summary)
    {
        var sb = new StringBuilder();

        // 1. 基础 Prompt（最稳定）
        sb.AppendLine(basePrompt.Trim());

        // 2. 指令（较稳定，只有文件变化时才改）
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sb.AppendLine();
            sb.AppendLine(instructions.Trim());
        }

        // 3. 记忆（较稳定，变化较慢）
        if (!string.IsNullOrWhiteSpace(memory))
        {
            sb.AppendLine();
            sb.AppendLine(memory.Trim());
        }

        // 4. 摘要（可能变化，放最后）
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine();
            sb.AppendLine("<较早对话的摘要>");
            sb.AppendLine(summary.Trim());
            sb.AppendLine("</较早对话的摘要>");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 估算 Prompt Cache 能节省的 token 成本。
    /// 假设 cached tokens 的成本是原来的 10%。
    /// </summary>
    public int EstimateSavedTokens(int totalPromptTokens)
    {
        if (_lastPrefixHash is null)
        {
            return 0;
        }
        // 假设前缀部分可以被缓存，节省 90% 的成本
        return (int)(_lastPrefixTokens * 0.9);
    }

    /// <summary>
    /// 获取缓存统计信息。
    /// </summary>
    public CacheStats GetStats()
    {
        return new CacheStats
        {
            Hits = _cacheHits,
            Misses = _cacheMisses,
            HitRate = HitRate,
            LastPrefixTokens = _lastPrefixTokens,
        };
    }

    /// <summary>
    /// 重置统计。
    /// </summary>
    public void ResetStats()
    {
        _cacheHits = 0;
        _cacheMisses = 0;
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16];
    }
}

/// <summary>缓存统计信息。</summary>
public sealed record CacheStats
{
    public int Hits { get; init; }
    public int Misses { get; init; }
    public double HitRate { get; init; }
    public int LastPrefixTokens { get; init; }
}
