using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Memory;

/// <summary>一组相关记忆的聚类。</summary>
public sealed class MemoryCluster
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>聚类的主题标签。</summary>
    public string Theme { get; set; } = "";

    /// <summary>聚类的摘要描述。</summary>
    public string Summary { get; set; } = "";

    /// <summary>属于这个聚类的记忆 ID。</summary>
    public List<string> MemberIds { get; set; } = new();

    /// <summary>聚类的中心向量（如果有）。</summary>
    public float[]? Centroid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 记忆聚类管理器：将相似的记忆自动分组，便于发现用户的知识主题和工作习惯。
/// 聚类基于文本相似度（无向量时）或语义相似度（有向量时）。
/// </summary>
public sealed class MemoryClusterManager
{
    private readonly MemoryStore _store;
    private readonly IChatGateway? _gateway;
    private readonly string _connectionString;
    private readonly object _lock = new();

    /// <summary>一个聚类最少需要多少条记忆。</summary>
    public const int MinClusterSize = 3;

    /// <summary>聚类的相似度阈值。</summary>
    public const double ClusterThreshold = 0.4;

    public string Scene { get; init; } = Scenes.Agent;

    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public MemoryClusterManager(MemoryStore store, IChatGateway? gateway = null)
    {
        _store = store;
        _gateway = gateway;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, DefaultTimeout = 5, Pooling = false }.ToString();
        Migrate();
    }

    /// <summary>
    /// 基于文本相似度对记忆进行聚类（不需要向量模型）。
    /// 使用简单的层次聚类算法。
    /// </summary>
    public List<MemoryCluster> ClusterByText()
    {
        var items = _store.List();
        if (items.Count < MinClusterSize)
        {
            return new List<MemoryCluster>();
        }

        // 计算相似度矩阵
        var n = items.Count;
        var similarity = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var s = TextSimilarity.Relevance(items[i].Text, items[j].Text);
                similarity[i, j] = s;
                similarity[j, i] = s;
            }
        }

        // 简单的贪心聚类
        var clusters = new List<List<int>>();
        var assigned = new HashSet<int>();

        for (var i = 0; i < n; i++)
        {
            if (assigned.Contains(i))
            {
                continue;
            }

            var cluster = new List<int> { i };
            assigned.Add(i);

            // 找所有与当前簇相似的
            for (var j = 0; j < n; j++)
            {
                if (assigned.Contains(j))
                {
                    continue;
                }

                // 检查与簇内任意成员的相似度
                var maxSim = cluster.Max(k => similarity[k, j]);
                if (maxSim >= ClusterThreshold)
                {
                    cluster.Add(j);
                    assigned.Add(j);
                }
            }

            if (cluster.Count >= MinClusterSize)
            {
                clusters.Add(cluster);
            }
        }

        // 为每个聚类生成主题
        return clusters.Select(c => new MemoryCluster
        {
            MemberIds = c.Select(i => items[i].Id).ToList(),
            Theme = InferTheme(c.Select(i => items[i]).ToList()),
            Summary = GenerateLocalSummary(c.Select(i => items[i]).ToList()),
        }).ToList();
    }

    /// <summary>
    /// 使用模型为聚类生成摘要和主题。
    /// </summary>
    public async Task<MemoryCluster?> EnrichClusterAsync(MemoryCluster cluster, CancellationToken ct)
    {
        if (_gateway is null || cluster.MemberIds.Count == 0)
        {
            return cluster;
        }

        var items = _store.List().Where(i => cluster.MemberIds.Contains(i.Id)).ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var itemList = string.Join("\n", items.Select((i, n) => $"{n + 1}. [{i.Kind}] {i.Text}"));
        var prompt = """
            分析以下一组相关的用户记忆，给出：
            1. 一个 2-6 字的主题标签（如"文件命名偏好""Excel 报表习惯""命令行使用"）
            2. 一句话总结这组记忆的共同点

            记忆内容：
            """ + itemList + """


            只输出 JSON：{"theme": "主题标签", "summary": "一句话总结"}
            """;

        try
        {
            var turn = await _gateway.CompleteAsync(new ChatRequest
            {
                Scene = Scene,
                Stream = false,
                Temperature = 0.2,
                MaxTokens = 256,
                ExtraBody = new Dictionary<string, JsonNode?> { ["enable_thinking"] = false },
                Messages = new[] { ChatMessage.System(prompt) },
            }, null, ct);

            var content = turn.Content;
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(content[start..(end + 1)]);
                var root = doc.RootElement;
                cluster.Theme = root.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? cluster.Theme
                    : cluster.Theme;
                cluster.Summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? cluster.Summary
                    : cluster.Summary;
            }
        }
        catch
        {
            // 模型调用失败不影响聚类本身
        }

        return cluster;
    }

    /// <summary>保存聚类结果。</summary>
    public void SaveClusters(List<MemoryCluster> clusters)
    {
        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            // 先清空旧的
            using (var clear = c.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM memory_clusters";
                clear.ExecuteNonQuery();
            }

            // 插入新的
            foreach (var cluster in clusters)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO memory_clusters (id, theme, summary, member_ids, created_at, updated_at)
                    VALUES ($id, $theme, $summary, $members, $created, $updated)
                    """;
                cmd.Parameters.AddWithValue("$id", cluster.Id);
                cmd.Parameters.AddWithValue("$theme", cluster.Theme);
                cmd.Parameters.AddWithValue("$summary", cluster.Summary);
                cmd.Parameters.AddWithValue("$members", JsonSerializer.Serialize(cluster.MemberIds));
                cmd.Parameters.AddWithValue("$created", cluster.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$updated", cluster.UpdatedAt.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>加载已保存的聚类。</summary>
    public List<MemoryCluster> LoadClusters()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, theme, summary, member_ids, created_at, updated_at FROM memory_clusters";
            using var r = cmd.ExecuteReader();
            var clusters = new List<MemoryCluster>();
            while (r.Read())
            {
                clusters.Add(new MemoryCluster
                {
                    Id = r.GetString(0),
                    Theme = r.GetString(1),
                    Summary = r.GetString(2),
                    MemberIds = JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? new(),
                    CreatedAt = DateTimeOffset.TryParse(r.GetString(4), out var created) ? created : DateTimeOffset.MinValue,
                    UpdatedAt = DateTimeOffset.TryParse(r.GetString(5), out var updated) ? updated : DateTimeOffset.MinValue,
                });
            }
            return clusters;
        }
    }

    /// <summary>
    /// 基于聚类的增强检索：先匹配聚类主题，再展开到具体记忆。
    /// </summary>
    public List<(MemoryItem Item, double Score, string? ClusterTheme)> ClusterAwareSearch(
        string query,
        int max = 10,
        double minScore = 0.1)
    {
        var clusters = LoadClusters();
        var items = _store.List();
        var itemById = items.ToDictionary(i => i.Id);

        var results = new List<(MemoryItem Item, double Score, string? ClusterTheme)>();

        // 1. 直接匹配记忆
        foreach (var item in items)
        {
            var score = TextSimilarity.Relevance(query, item.Text);
            if (score >= minScore)
            {
                var cluster = clusters.FirstOrDefault(c => c.MemberIds.Contains(item.Id));
                results.Add((item, score, cluster?.Theme));
            }
        }

        // 2. 通过聚类主题匹配，展开到成员
        foreach (var cluster in clusters)
        {
            var themeScore = TextSimilarity.Relevance(query, cluster.Theme + " " + cluster.Summary);
            if (themeScore >= minScore * 0.8)
            {
                foreach (var memberId in cluster.MemberIds)
                {
                    if (itemById.TryGetValue(memberId, out var item))
                    {
                        // 如果还没加进去，通过聚类加进去（分数稍低）
                        if (!results.Any(r => r.Item.Id == memberId))
                        {
                            results.Add((item, themeScore * 0.8, cluster.Theme));
                        }
                    }
                }
            }
        }

        return results
            .GroupBy(r => r.Item.Id)
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .Take(max)
            .ToList();
    }

    /// <summary>从文本推断主题（简单实现）。</summary>
    private static string InferTheme(List<MemoryItem> items)
    {
        // 找出最常见的关键词
        var words = items
            .SelectMany(i => i.Text.Split(new[] { ' ', '，', '、', '。', '：', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length >= 2)
            .GroupBy(w => w.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => g.Key);

        return string.Join("·", words);
    }

    /// <summary>生成本地摘要（不调用模型）。</summary>
    private static string GenerateLocalSummary(List<MemoryItem> items)
    {
        if (items.Count == 0)
        {
            return "";
        }

        // 取第一条作为代表
        var first = items[0].Text;
        if (first.Length > 50)
        {
            first = first[..50] + "…";
        }

        return items.Count == 1
            ? first
            : $"{first}（等 {items.Count} 条相关记忆）";
    }

    private void Migrate()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS memory_clusters (
                    id TEXT PRIMARY KEY,
                    theme TEXT NOT NULL DEFAULT '',
                    summary TEXT NOT NULL DEFAULT '',
                    member_ids TEXT NOT NULL DEFAULT '[]',
                    centroid BLOB NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_memory_clusters_theme ON memory_clusters(theme);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return c;
    }
}
