using System.Text.Json;
using Flyknit.Core.Context;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Memory;

/// <summary>记忆/任务之间的关联类型。</summary>
public enum RelationType
{
    /// <summary>主题相关（语义相似）。</summary>
    RelatesTo,

    /// <summary>因果关系（A 导致 B）。</summary>
    Causes,

    /// <summary>时序关系（A 之后是 B）。</summary>
    Follows,

    /// <summary>更新关系（A 被 B 取代）。</summary>
    UpdatedTo,

    /// <summary>派生关系（B 从 A 学到的教训）。</summary>
    LearnedFrom,

    /// <summary>继续关系（B 是 A 的后续任务）。</summary>
    Continues,

    /// <summary>矛盾关系（A 和 B 冲突）。</summary>
    Contradicts,
}

/// <summary>一条关联边。</summary>
public sealed record MemoryEdge(
    string FromId,
    string ToId,
    RelationType Type,
    double Strength = 1.0,
    string? Note = null)
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 记忆与任务的知识图谱：显式关联记忆之间、任务之间、记忆与任务之间的关系。
/// 支持的关系类型：相关、因果、时序、更新、派生、继续、矛盾。
/// </summary>
public sealed class MemoryGraph
{
    private readonly MemoryStore _memory;
    private readonly EpisodeStore _episodes;
    private readonly string _connectionString;
    private readonly object _lock = new();

    /// <summary>自动建立关联的相似度阈值。</summary>
    public const double AutoLinkThreshold = 0.5;

    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public MemoryGraph(MemoryStore memory, EpisodeStore episodes)
    {
        _memory = memory;
        _episodes = episodes;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = memory.DatabasePath, DefaultTimeout = 5, Pooling = false }.ToString();
        Migrate();
    }

    /// <summary>添加一条关联。</summary>
    public void AddEdge(MemoryEdge edge)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO memory_graph (from_id, to_id, type, strength, note, created_at)
                VALUES ($from, $to, $type, $strength, $note, $created)
                """;
            cmd.Parameters.AddWithValue("$from", edge.FromId);
            cmd.Parameters.AddWithValue("$to", edge.ToId);
            cmd.Parameters.AddWithValue("$type", edge.Type.ToString());
            cmd.Parameters.AddWithValue("$strength", edge.Strength);
            cmd.Parameters.AddWithValue("$note", (object?)edge.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", edge.CreatedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>获取某个节点的所有邻居。</summary>
    public List<(string Id, RelationType Type, double Strength, bool Outgoing)> GetNeighbors(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT from_id, to_id, type, strength FROM memory_graph
                WHERE from_id = $id OR to_id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            var neighbors = new List<(string, RelationType, double, bool)>();
            while (r.Read())
            {
                var fromId = r.GetString(0);
                var toId = r.GetString(1);
                var type = Enum.TryParse<RelationType>(r.GetString(2), out var t) ? t : RelationType.RelatesTo;
                var strength = r.GetDouble(3);
                var outgoing = fromId == id;
                neighbors.Add((outgoing ? toId : fromId, type, strength, outgoing));
            }
            return neighbors;
        }
    }

    /// <summary>
    /// 扩展检索：从直接匹配出发，沿着关联找到相关的记忆和任务。
    /// </summary>
    public List<string> ExpandedRetrieve(IEnumerable<string> seedIds, int depth = 1, double minStrength = 0.3)
    {
        var result = new HashSet<string>(seedIds);
        var frontier = new HashSet<string>(seedIds);

        for (var d = 0; d < depth && frontier.Count > 0; d++)
        {
            var newFrontier = new HashSet<string>();
            foreach (var id in frontier)
            {
                var neighbors = GetNeighbors(id)
                    .Where(n => n.Strength >= minStrength && !result.Contains(n.Id));
                foreach (var (neighborId, _, _, _) in neighbors)
                {
                    result.Add(neighborId);
                    newFrontier.Add(neighborId);
                }
            }
            frontier = newFrontier;
        }

        return result.ToList();
    }

    /// <summary>
    /// 自动为新记忆建立关联。
    /// </summary>
    public void AutoLinkMemory(MemoryItem newItem)
    {
        var existingItems = _memory.List().Where(i => i.Id != newItem.Id).ToList();

        foreach (var item in existingItems)
        {
            var similarity = TextSimilarity.Relevance(newItem.Text, item.Text);

            // 高度相似的建立 RelatesTo 关系
            if (similarity >= AutoLinkThreshold)
            {
                AddEdge(new MemoryEdge(newItem.Id, item.Id, RelationType.RelatesTo, similarity));
            }
        }

        // 检查是否是更新关系（通过 superseded_by 链）
        if (newItem.History.Count > 0)
        {
            foreach (var oldText in newItem.History)
            {
                var oldId = MemoryStore.IdOf(oldText);
                AddEdge(new MemoryEdge(oldId, newItem.Id, RelationType.UpdatedTo, 1.0));
            }
        }
    }

    /// <summary>
    /// 自动为新任务建立关联。
    /// </summary>
    public void AutoLinkEpisode(Episode newEpisode)
    {
        var existingEpisodes = _episodes.List().Where(e => e.Id != newEpisode.Id).ToList();

        // 1. 主题相关
        foreach (var ep in existingEpisodes)
        {
            var titleSim = TextSimilarity.Relevance(newEpisode.Title, ep.Title);
            var taskSim = TextSimilarity.Relevance(newEpisode.Task, ep.Task);
            var similarity = Math.Max(titleSim, taskSim * 0.8);

            if (similarity >= AutoLinkThreshold)
            {
                AddEdge(new MemoryEdge(newEpisode.Id, ep.Id, RelationType.RelatesTo, similarity));
            }
        }

        // 2. 时序关系（同一工作区、时间接近）
        var recentInSameWorkspace = existingEpisodes
            .Where(e => !string.IsNullOrEmpty(newEpisode.Workspace)
                && string.Equals(e.Workspace, newEpisode.Workspace, StringComparison.OrdinalIgnoreCase)
                && (newEpisode.CreatedAt - e.CreatedAt).TotalHours < 24)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault();

        if (recentInSameWorkspace is not null)
        {
            var titleSim = TextSimilarity.Relevance(newEpisode.Title, recentInSameWorkspace.Title);
            if (titleSim > 0.3)
            {
                // 可能是继续关系
                AddEdge(new MemoryEdge(recentInSameWorkspace.Id, newEpisode.Id, RelationType.Continues, 0.8));
            }
            else
            {
                // 至少是时序关系
                AddEdge(new MemoryEdge(recentInSameWorkspace.Id, newEpisode.Id, RelationType.Follows, 0.5));
            }
        }

        // 3. 教训关系
        foreach (var lessonId in newEpisode.LessonIds)
        {
            AddEdge(new MemoryEdge(newEpisode.Id, lessonId, RelationType.LearnedFrom, 1.0));
        }
    }

    /// <summary>
    /// 查找两个节点之间的路径。
    /// </summary>
    public List<MemoryEdge>? FindPath(string fromId, string toId, int maxDepth = 3)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<(string Id, List<MemoryEdge> Path)>();
        queue.Enqueue((fromId, new List<MemoryEdge>()));

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();
            if (path.Count >= maxDepth)
            {
                continue;
            }

            if (visited.Contains(current))
            {
                continue;
            }
            visited.Add(current);

            var neighbors = GetNeighbors(current);
            foreach (var (neighborId, type, strength, outgoing) in neighbors)
            {
                var edge = new MemoryEdge(
                    outgoing ? current : neighborId,
                    outgoing ? neighborId : current,
                    type,
                    strength
                );

                var newPath = new List<MemoryEdge>(path) { edge };

                if (neighborId == toId)
                {
                    return newPath;
                }

                queue.Enqueue((neighborId, newPath));
            }
        }

        return null;
    }

    /// <summary>获取图的统计信息。</summary>
    public GraphStats GetStats()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT type, COUNT(*), AVG(strength) FROM memory_graph GROUP BY type
                """;
            using var r = cmd.ExecuteReader();
            var byType = new Dictionary<RelationType, (int Count, double AvgStrength)>();
            while (r.Read())
            {
                var type = Enum.TryParse<RelationType>(r.GetString(0), out var t) ? t : RelationType.RelatesTo;
                byType[type] = (r.GetInt32(1), r.GetDouble(2));
            }

            cmd.CommandText = "SELECT COUNT(DISTINCT from_id) + COUNT(DISTINCT to_id) FROM memory_graph";
            var nodes = Convert.ToInt32(cmd.ExecuteScalar());

            cmd.CommandText = "SELECT COUNT(*) FROM memory_graph";
            var edges = Convert.ToInt32(cmd.ExecuteScalar());

            return new GraphStats
            {
                NodeCount = nodes,
                EdgeCount = edges,
                EdgesByType = byType,
            };
        }
    }

    /// <summary>
    /// 重建图：从记忆和任务数据重新生成所有关联。
    /// </summary>
    public void Rebuild()
    {
        lock (_lock)
        {
            using var c = Open();
            using var clear = c.CreateCommand();
            clear.CommandText = "DELETE FROM memory_graph";
            clear.ExecuteNonQuery();
        }

        // 为所有记忆建立关联
        foreach (var item in _memory.List())
        {
            AutoLinkMemory(item);
        }

        // 为所有任务建立关联
        foreach (var ep in _episodes.List())
        {
            AutoLinkEpisode(ep);
        }
    }

    private void Migrate()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS memory_graph (
                    from_id TEXT NOT NULL,
                    to_id TEXT NOT NULL,
                    type TEXT NOT NULL,
                    strength REAL NOT NULL DEFAULT 1.0,
                    note TEXT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (from_id, to_id, type)
                );
                CREATE INDEX IF NOT EXISTS ix_memory_graph_from ON memory_graph(from_id);
                CREATE INDEX IF NOT EXISTS ix_memory_graph_to ON memory_graph(to_id);
                CREATE INDEX IF NOT EXISTS ix_memory_graph_type ON memory_graph(type);
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

/// <summary>图的统计信息。</summary>
public sealed record GraphStats
{
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    public Dictionary<RelationType, (int Count, double AvgStrength)> EdgesByType { get; init; } = new();
}
