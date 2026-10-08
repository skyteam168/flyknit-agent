using Flyknit.Core.Context;

namespace Flyknit.Core.Memory;

/// <summary>记忆的温度层级。</summary>
public enum MemoryTemperature
{
    /// <summary>热记忆：最近 30 天活跃的，优先检索。</summary>
    Hot,

    /// <summary>温记忆：30-180 天，按需加载。</summary>
    Warm,

    /// <summary>冷记忆：超过 180 天未活跃，只保留摘要，需要时加载原文。</summary>
    Cold,

    /// <summary>核心记忆：高确认次数/高使用率的，永不降级。</summary>
    Core,
}

/// <summary>
/// 分层记忆管理器：按访问频率和时间将记忆分为热/温/冷/核心四层。
/// - 热记忆：最近 30 天活跃，常驻内存，优先检索
/// - 温记忆：30-180 天，按需加载
/// - 冷记忆：超过 180 天未活跃，只保留摘要索引
/// - 核心记忆：高确认/高使用/置顶的，永不降级
/// </summary>
public sealed class MemoryTierManager
{
    private readonly MemoryStore _store;

    /// <summary>热记忆的天数阈值。</summary>
    public const int HotDays = 30;

    /// <summary>温记忆的天数阈值。</summary>
    public const int WarmDays = 180;

    /// <summary>成为核心记忆的最低确认次数。</summary>
    public const int CoreProofThreshold = 5;

    /// <summary>成为核心记忆的最低使用次数。</summary>
    public const int CoreUsesThreshold = 10;

    private List<MemoryItem>? _hotCache;
    private DateTime _hotCacheTime;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public MemoryTierManager(MemoryStore store)
    {
        _store = store;
    }

    /// <summary>获取记忆的温度层级。</summary>
    public MemoryTemperature GetTier(MemoryItem item)
    {
        var today = DateOnly.FromDateTime(Clock());
        var lastSeen = item.LastSeen ?? item.Date ?? today;
        var daysSince = Math.Max(0, today.DayNumber - lastSeen.DayNumber);

        // 核心记忆：置顶的、高确认次数的、高使用率的
        if (item.Pinned || item.ProofCount >= CoreProofThreshold || item.Uses >= CoreUsesThreshold)
        {
            return MemoryTemperature.Core;
        }

        // 按时间分层
        if (daysSince <= HotDays)
        {
            return MemoryTemperature.Hot;
        }
        if (daysSince <= WarmDays)
        {
            return MemoryTemperature.Warm;
        }
        return MemoryTemperature.Cold;
    }

    /// <summary>获取热记忆（带缓存）。</summary>
    public List<MemoryItem> GetHotMemories()
    {
        if (_hotCache is not null && Clock() - _hotCacheTime < _cacheExpiry)
        {
            return _hotCache;
        }

        _hotCache = _store.List()
            .Where(i => GetTier(i) is MemoryTemperature.Hot or MemoryTemperature.Core)
            .ToList();
        _hotCacheTime = Clock();
        return _hotCache;
    }

    /// <summary>使缓存失效。</summary>
    public void InvalidateCache()
    {
        _hotCache = null;
    }

    /// <summary>
    /// 分层检索：先搜热记忆，不够再扩展到温/冷。
    /// </summary>
    public List<(MemoryItem Item, double Score)> TieredSearch(
        string query,
        int max = 10,
        double minScore = 0.08,
        IReadOnlyDictionary<string, double>? semantic = null)
    {
        var today = DateOnly.FromDateTime(Clock());
        var all = _store.List();
        var results = new List<(MemoryItem Item, double Score, MemoryTemperature Tier)>();

        foreach (var item in all)
        {
            var tier = GetTier(item);
            var score = MemoryStore.Hybrid(TextSimilarity.Relevance(query, item.Text), item.Id, semantic);

            // 核心记忆和热记忆有加成
            var tierBonus = tier switch
            {
                MemoryTemperature.Core => 1.2,
                MemoryTemperature.Hot => 1.1,
                MemoryTemperature.Warm => 1.0,
                MemoryTemperature.Cold => 0.9,
                _ => 1.0,
            };

            var finalScore = score * tierBonus;
            if (finalScore >= minScore)
            {
                results.Add((item, finalScore, tier));
            }
        }

        // 排序：先按分数，同分时核心 > 热 > 温 > 冷
        return results
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Tier)
            .Take(max)
            .Select(x => (x.Item, x.Score))
            .ToList();
    }

    /// <summary>获取各层的统计信息。</summary>
    public TierStats GetStats()
    {
        var all = _store.List();
        var byTier = all.GroupBy(GetTier).ToDictionary(g => g.Key, g => g.ToList());

        return new TierStats
        {
            CoreCount = byTier.GetValueOrDefault(MemoryTemperature.Core)?.Count ?? 0,
            HotCount = byTier.GetValueOrDefault(MemoryTemperature.Hot)?.Count ?? 0,
            WarmCount = byTier.GetValueOrDefault(MemoryTemperature.Warm)?.Count ?? 0,
            ColdCount = byTier.GetValueOrDefault(MemoryTemperature.Cold)?.Count ?? 0,
            TotalCount = all.Count,
        };
    }
}

/// <summary>各层的统计信息。</summary>
public sealed record TierStats
{
    public int CoreCount { get; init; }
    public int HotCount { get; init; }
    public int WarmCount { get; init; }
    public int ColdCount { get; init; }
    public int TotalCount { get; init; }
}
