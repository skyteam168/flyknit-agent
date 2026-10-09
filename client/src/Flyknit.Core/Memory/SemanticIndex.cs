using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Flyknit.Core.Gateway;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Memory;

/// <summary>
/// 记忆的语义检索：每条记忆算一次向量存在 memory_vectors（和条目同一个库），每轮任务只需把用户这句话向量化一次，
/// 再和已有向量算余弦。结果交给 <see cref="MemoryStore.BuildPrompt"/> / <see cref="MemoryStore.Search"/> 和字面相关度合用。
///
/// 这是锦上添花，绝不能拖慢或卡住对话：
/// - 服务端没配向量模型（503）时停用 30 分钟，其他错误停用 5 分钟，期间直接按字面匹配；
/// - 一次最多等 <see cref="Timeout"/>，超时同样当这次没有；
/// - 新记忆一次最多补算 <see cref="MaxNewPerCall"/> 条，剩下的下次再算。
/// 条目改了字（哈希对不上）会重算；被删除时向量跟着删（见 MemoryStore.Scrub）；换了向量模型，旧模型的向量清掉重算。
/// </summary>
public sealed class SemanticIndex
{
    /// <summary>一次请求最多几段文字（通义 text-embedding-v4 的上限是 10）。</summary>
    public const int BatchSize = 10;

    public const int MaxNewPerCall = 40;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <summary>技能的向量 ID 前缀（记忆条目的 ID 不会以它开头）。</summary>
    public const string SkillPrefix = "skill:";

    private readonly MemoryStore _store;
    private readonly IEmbeddingGateway _gateway;
    private readonly string _connectionString;
    private readonly object _lock = new();
    private DateTime _pausedUntil = DateTime.MinValue;
    private string? _cleanedFor;

    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>最近一次失败的原因（日志用）。</summary>
    public string? LastError { get; private set; }

    public SemanticIndex(MemoryStore store, IEmbeddingGateway gateway)
    {
        _store = store;
        _gateway = gateway;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, DefaultTimeout = 5, Pooling = false }.ToString();
    }

    /// <summary>现在是否在停用期。</summary>
    public bool Paused => Clock() < _pausedUntil;

    /// <summary>最近一次是否成功算出了语义相似度（界面上据此显示“语义检索：已启用”）。</summary>
    public bool Working { get; private set; }

    /// <summary>
    /// 和 <paramref name="query"/> 的语义相似度（记忆条目 ID → 余弦，-1..1）。不可用、超时、出错时返回 null，调用方只用字面匹配。
    /// 置顶的不需要（每次都放），不算。
    /// </summary>
    /// <param name="extra">记忆以外也要比一比的文字（ID → 文字），比如技能的名称和描述。ID 用 <see cref="SkillPrefix"/> 开头，
    /// 向量和记忆存在同一张表里，用同一次“把用户这句话向量化”，不多花一次请求。</param>
    public async Task<IReadOnlyDictionary<string, double>?> ScoreAsync(string query, CancellationToken ct, IReadOnlyDictionary<string, string>? extra = null)
    {
        query = query.Trim();
        if (query.Length == 0 || Paused)
        {
            return null;
        }
        var items = _store.List().Where(i => !i.Pinned).Select(i => (i.Id, i.Text))
            .Concat((extra ?? new Dictionary<string, string>()).Where(e => e.Key.StartsWith(SkillPrefix, StringComparison.Ordinal)).Select(e => (Id: e.Key, Text: e.Value)))
            .ToList();
        if (items.Count == 0)
        {
            return null;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            // 先算用户这句话：顺便知道服务端现在用的是哪个向量模型（管理员可能换过），再按这个模型补算缺的
            var first = await _gateway.EmbedAsync(new[] { query }, cts.Token);
            var model = first.Model;
            var q = Normalize(first.Vectors[0]);
            var stored = Load(model);
            var missing = items.Where(i => !stored.TryGetValue(i.Id, out var v) || v.Hash != Hash(i.Text)).Take(MaxNewPerCall).ToList();
            // 几批同时发：升级后第一次要补算几十条，一批一批排队容易超时
            var batches = missing.Chunk(BatchSize).ToList();
            var results = await Task.WhenAll(batches.Select(b => _gateway.EmbedAsync(b.Select(i => i.Text).ToList(), cts.Token)));
            var fresh = new List<(string Id, string Hash, float[] Vector)>();
            for (var n = 0; n < batches.Count; n++)
            {
                if (results[n].Model != model)
                {
                    continue; // 中途换了模型：这批不存，下次重算
                }
                for (var k = 0; k < batches[n].Length; k++)
                {
                    fresh.Add((batches[n][k].Id, Hash(batches[n][k].Text), Normalize(results[n].Vectors[k])));
                }
            }
            Save(model, fresh);
            // 存着的向量只有同一个模型算的才能比
            var vectors = Load(model);
            var scores = new Dictionary<string, double>();
            foreach (var item in items)
            {
                if (vectors.TryGetValue(item.Id, out var v) && v.Hash == Hash(item.Text) && v.Vector.Length == q.Length)
                {
                    scores[item.Id] = Dot(q, v.Vector);
                }
            }
            LastError = null;
            Working = true;
            return scores;
        }
        catch (GatewayException ex) when (ex.StatusCode is 503 or 404)
        {
            Pause(TimeSpan.FromMinutes(30), ex.Message); // 服务端没配向量模型（或版本太旧没有这个接口）
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Pause(TimeSpan.FromMinutes(5), ex is OperationCanceledException ? "向量服务超时" : ex.Message);
            return null;
        }
    }

    private void Pause(TimeSpan span, string reason)
    {
        _pausedUntil = Clock() + span;
        LastError = reason;
        Working = false;
    }

    /// <summary>某个模型算的向量（不同模型的向量不能互相比）。</summary>
    private Dictionary<string, (string Hash, float[] Vector)> Load(string model)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT item_id, text_hash, vector FROM memory_vectors WHERE model = $m";
            cmd.Parameters.AddWithValue("$m", model);
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, (string, float[])>();
            while (r.Read())
            {
                map[r.GetString(0)] = (r.GetString(1), MemoryMarshal.Cast<byte, float>((byte[])r.GetValue(2)).ToArray());
            }
            return map;
        }
    }

    private void Save(string model, List<(string Id, string Hash, float[] Vector)> fresh)
    {
        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            if (_cleanedFor != model)
            {
                // 换了向量模型：旧模型算的向量用不上了
                using var clean = c.CreateCommand();
                clean.Transaction = tx;
                clean.CommandText = "DELETE FROM memory_vectors WHERE model <> $m";
                clean.Parameters.AddWithValue("$m", model);
                clean.ExecuteNonQuery();
                _cleanedFor = model;
            }
            foreach (var (id, hash, vector) in fresh)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO memory_vectors(item_id, model, text_hash, vector)
                    SELECT $id, $m, $h, $v WHERE substr($id, 1, 6) = 'skill:' OR EXISTS (SELECT 1 FROM memory_items WHERE id = $id AND status = 'active')
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$m", model);
                cmd.Parameters.AddWithValue("$h", hash);
                cmd.Parameters.AddWithValue("$v", MemoryMarshal.AsBytes(vector.AsSpan()).ToArray());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.Trim())))[..16];

    private static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
        {
            sum += x * x;
        }
        var norm = Math.Sqrt(sum);
        return norm == 0 ? v : v.Select(x => (float)(x / norm)).ToArray();
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
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
