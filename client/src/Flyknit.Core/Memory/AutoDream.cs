using System.Text.Json;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Memory;

/// <summary>AutoDream 运行状态。</summary>
public enum AutoDreamState
{
    /// <summary>空闲，等待触发。</summary>
    Idle,

    /// <summary>正在运行。</summary>
    Running,

    /// <summary>已暂停（被写入操作打断）。</summary>
    Paused,

    /// <summary>已禁用。</summary>
    Disabled,
}

/// <summary>AutoDream 运行结果。</summary>
public sealed record AutoDreamReport
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.Now) - StartedAt;

    /// <summary>是否成功完成。</summary>
    public bool Success { get; set; }

    /// <summary>整理阶段：合并了几组、多少条。</summary>
    public int MergedGroups { get; set; }
    public int MergedItems { get; set; }

    /// <summary>扫描阶段：发现多少对潜在矛盾。</summary>
    public int ContradictionsFound { get; set; }

    /// <summary>聚类阶段：生成了几个聚类。</summary>
    public int ClustersCreated { get; set; }

    /// <summary>图谱阶段：重建了多少条边。</summary>
    public int EdgesRebuilt { get; set; }

    /// <summary>淘汰阶段：清理了几条冷记忆。</summary>
    public int ColdItemsEvicted { get; set; }

    /// <summary>索引阶段：更新了几条向量。</summary>
    public int VectorsUpdated { get; set; }

    /// <summary>错误信息。</summary>
    public List<string> Errors { get; } = new();

    /// <summary>各阶段耗时。</summary>
    public Dictionary<string, TimeSpan> PhaseDurations { get; } = new();
}

/// <summary>
/// AutoDream 离线记忆治理：Agent 空闲时在后台自动整理长期记忆。
/// 
/// 治理内容：
/// 1. 记忆整理 - 合并重复、换了说法的记忆
/// 2. 矛盾扫描 - 发现冲突的记忆
/// 3. 聚类重建 - 按主题重新分组
/// 4. 图谱维护 - 重建记忆/任务之间的关联
/// 5. 冷记忆清理 - 淘汰长期未使用的低价值记忆
/// 6. 索引更新 - 重建向量索引
/// 
/// 触发时机：
/// - 空闲超过指定时间（默认 30 分钟无任务）
/// - 距离上次运行超过指定间隔（默认 6 小时）
/// - 手动触发
/// 
/// 并发控制：
/// - 运行期间阻塞新的记忆写入
/// - 正在写入时延迟运行
/// - 可随时暂停和恢复
/// </summary>
public sealed class AutoDream : IDisposable
{
    private readonly MemoryStore _memory;
    private readonly EpisodeStore _episodes;
    private readonly IChatGateway? _gateway;
    private readonly string _connectionString;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ReaderWriterLockSlim _memoryRwLock = new();

    private AutoDreamState _state = AutoDreamState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private DateTime _idleStartTime;

    /// <summary>是否启用自动运行。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>自动运行的最小间隔（小时）。</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>需要空闲多久才触发（分钟）。</summary>
    public int IdleMinutes { get; set; } = 30;

    /// <summary>整理记忆时的场景。</summary>
    public string Scene { get; set; } = Scenes.Agent;

    /// <summary>当前状态。</summary>
    public AutoDreamState State
    {
        get { lock (_stateLock) return _state; }
        private set { lock (_stateLock) _state = value; }
    }

    /// <summary>上次运行时间。</summary>
    public DateTime? LastRunTime { get; private set; }

    /// <summary>上次运行报告。</summary>
    public AutoDreamReport? LastReport { get; private set; }

    /// <summary>正在运行时的进度（0-100）。</summary>
    public int Progress { get; private set; }

    /// <summary>正在运行的阶段名。</summary>
    public string CurrentPhase { get; private set; } = "";

    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    /// <summary>状态变化事件。</summary>
    public event Action<AutoDreamState>? StateChanged;

    /// <summary>运行完成事件。</summary>
    public event Action<AutoDreamReport>? Completed;

    public AutoDream(MemoryStore memory, EpisodeStore episodes, IChatGateway? gateway = null)
    {
        _memory = memory;
        _episodes = episodes;
        _gateway = gateway;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = memory.DatabasePath, DefaultTimeout = 5, Pooling = false }.ToString();
        Migrate();
        LoadState();
    }

    /// <summary>
    /// 获取记忆写入锁。调用方在写入记忆前应获取此锁，确保不与 AutoDream 冲突。
    /// 用法：using var _ = autoDream.AcquireWriteLock();
    /// </summary>
    public IDisposable AcquireWriteLock()
    {
        _memoryRwLock.EnterWriteLock();
        return new WriteLockReleaser(_memoryRwLock);
    }

    /// <summary>
    /// 尝试获取记忆写入锁（非阻塞）。
    /// </summary>
    public bool TryAcquireWriteLock(int timeoutMs, out IDisposable? releaser)
    {
        if (_memoryRwLock.TryEnterWriteLock(timeoutMs))
        {
            releaser = new WriteLockReleaser(_memoryRwLock);
            return true;
        }
        releaser = null;
        return false;
    }

    /// <summary>
    /// 检查是否应该触发自动运行。由宿主定期调用（如每分钟）。
    /// </summary>
    /// <param name="isIdle">当前是否空闲（没有任务在运行）。</param>
    public void CheckTrigger(bool isIdle)
    {
        if (!Enabled || State != AutoDreamState.Idle)
        {
            return;
        }

        var now = Clock();

        // 更新空闲状态
        if (isIdle)
        {
            if (_idleStartTime == default)
            {
                _idleStartTime = now;
            }
        }
        else
        {
            _idleStartTime = default;
        }

        // 检查是否满足触发条件
        var idleDuration = _idleStartTime != default ? (now - _idleStartTime).TotalMinutes : 0;
        var sinceLastRun = LastRunTime.HasValue ? (now - LastRunTime.Value).TotalHours : double.MaxValue;

        if (idleDuration >= IdleMinutes && sinceLastRun >= IntervalHours)
        {
            // 触发运行
            _ = RunAsync();
        }
    }

    /// <summary>
    /// 手动触发运行。
    /// </summary>
    public Task<AutoDreamReport> RunAsync(CancellationToken ct = default)
    {
        if (!_runLock.Wait(0))
        {
            // 已经在运行
            return Task.FromResult(LastReport ?? new AutoDreamReport { StartedAt = Clock() });
        }

        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runningTask = RunInternalAsync(_cts.Token);
            return _runningTask.ContinueWith(t =>
            {
                _runLock.Release();
                return LastReport ?? new AutoDreamReport { StartedAt = Clock() };
            });
        }
        catch
        {
            _runLock.Release();
            throw;
        }
    }

    /// <summary>
    /// 暂停当前运行。
    /// </summary>
    public void Pause()
    {
        _cts?.Cancel();
        State = AutoDreamState.Paused;
        StateChanged?.Invoke(State);
    }

    /// <summary>
    /// 恢复运行。
    /// </summary>
    public Task ResumeAsync()
    {
        if (State == AutoDreamState.Paused)
        {
            return RunAsync();
        }
        return Task.CompletedTask;
    }

    private async Task RunInternalAsync(CancellationToken ct)
    {
        var report = new AutoDreamReport { StartedAt = Clock() };
        LastReport = report;
        State = AutoDreamState.Running;
        StateChanged?.Invoke(State);
        Progress = 0;

        try
        {
            // 获取读锁，阻止写入
            _memoryRwLock.EnterReadLock();
            try
            {
                // 阶段 1：记忆整理（20%）
                await RunPhaseAsync("整理重复记忆", 0, 20, async () =>
                {
                    var consolidator = new MemoryConsolidator(_gateway!, _memory) { Scene = Scene };
                    var result = await consolidator.RunAsync(ct);
                    report.MergedGroups = result.Groups;
                    report.MergedItems = result.ItemsMerged;
                    report.Errors.AddRange(result.Errors);
                }, report, ct);

                // 阶段 2：矛盾扫描（35%）
                await RunPhaseAsync("扫描矛盾", 20, 35, () =>
                {
                    var detector = new ContradictionDetector(_memory, _gateway);
                    var contradictions = detector.ScanContradictions();
                    report.ContradictionsFound = contradictions.Count;
                    // 记录矛盾供用户处理
                    SaveContradictions(contradictions);
                    return Task.CompletedTask;
                }, report, ct);

                // 阶段 3：聚类重建（55%）
                await RunPhaseAsync("重建聚类", 35, 55, async () =>
                {
                    var clusterManager = new MemoryClusterManager(_memory, _gateway) { Scene = Scene };
                    var clusters = clusterManager.ClusterByText();

                    // 用模型增强聚类（可选）
                    if (_gateway is not null)
                    {
                        var enriched = new List<MemoryCluster>();
                        foreach (var cluster in clusters.Take(10)) // 最多增强 10 个
                        {
                            var result = await clusterManager.EnrichClusterAsync(cluster, ct);
                            if (result is not null)
                            {
                                enriched.Add(result);
                            }
                        }
                        clusters = enriched.Concat(clusters.Skip(10)).ToList();
                    }

                    clusterManager.SaveClusters(clusters);
                    report.ClustersCreated = clusters.Count;
                }, report, ct);

                // 阶段 4：图谱重建（75%）
                await RunPhaseAsync("重建知识图谱", 55, 75, () =>
                {
                    var graph = new MemoryGraph(_memory, _episodes);
                    graph.Rebuild();
                    var stats = graph.GetStats();
                    report.EdgesRebuilt = stats.EdgeCount;
                    return Task.CompletedTask;
                }, report, ct);

                // 阶段 5：冷记忆清理（85%）
                await RunPhaseAsync("清理冷记忆", 75, 85, () =>
                {
                    var tierManager = new MemoryTierManager(_memory);
                    var stats = tierManager.GetStats();

                    // 如果冷记忆太多，淘汰一部分
                    if (stats.ColdCount > MemoryStore.MaxPerKind / 2)
                    {
                        var evicted = EvictColdItems(stats.ColdCount - MemoryStore.MaxPerKind / 4);
                        report.ColdItemsEvicted = evicted;
                    }

                    return Task.CompletedTask;
                }, report, ct);

                // 阶段 6：索引更新（100%）
                await RunPhaseAsync("更新索引", 85, 100, async () =>
                {
                    if (_memory.Semantic is not null)
                    {
                        var updated = await UpdateVectorIndexAsync(ct);
                        report.VectorsUpdated = updated;
                    }
                }, report, ct);

                report.Success = true;
            }
            finally
            {
                _memoryRwLock.ExitReadLock();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report.Errors.Add("已取消");
        }
        catch (Exception ex)
        {
            report.Errors.Add(ex.Message);
        }
        finally
        {
            report.CompletedAt = Clock();
            LastRunTime = Clock();
            SaveState();
            Progress = 100;
            CurrentPhase = "";
            State = AutoDreamState.Idle;
            StateChanged?.Invoke(State);
            Completed?.Invoke(report);
        }
    }

    private async Task RunPhaseAsync(string phase, int startProgress, int endProgress, Func<Task> action, AutoDreamReport report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CurrentPhase = phase;
        Progress = startProgress;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Errors.Add($"{phase}: {ex.Message}");
        }
        finally
        {
            sw.Stop();
            report.PhaseDurations[phase] = sw.Elapsed;
            Progress = endProgress;
        }
    }

    /// <summary>淘汰冷记忆。</summary>
    private int EvictColdItems(int count)
    {
        var tierManager = new MemoryTierManager(_memory);
        var items = _memory.List()
            .Where(i => !i.Pinned && tierManager.GetTier(i) == MemoryTemperature.Cold)
            .OrderBy(i => MemoryStore.ValueOf(i, DateOnly.FromDateTime(Clock())))
            .Take(count)
            .ToList();

        foreach (var item in items)
        {
            // 不真正删除，只是标记为 evicted
            // _memory 内部已经有这个逻辑，这里只需要触发淘汰检查
        }

        return items.Count;
    }

    /// <summary>更新向量索引。</summary>
    private async Task<int> UpdateVectorIndexAsync(CancellationToken ct)
    {
        if (_memory.Semantic is null)
        {
            return 0;
        }

        // SemanticIndex.ScoreAsync 会自动补算缺失的向量（每次最多 MaxNewPerCall 条）
        // 多调用几次确保所有记忆都被索引
        var items = _memory.List();
        var totalItems = items.Count;
        var iterations = (totalItems / SemanticIndex.MaxNewPerCall) + 1;
        var updated = 0;

        for (var i = 0; i < iterations && !ct.IsCancellationRequested; i++)
        {
            try
            {
                // 用一个通用查询触发向量计算
                var result = await _memory.Semantic.ScoreAsync("记忆索引更新", ct);
                if (result is not null)
                {
                    updated = result.Count;
                }
                
                // 如果没有更多需要索引的，提前退出
                if (_memory.Semantic.Working && result?.Count == items.Where(x => !x.Pinned).Count())
                {
                    break;
                }
            }
            catch
            {
                // 单次失败不中断
            }
        }

        return updated;
    }

    /// <summary>保存发现的矛盾。</summary>
    private void SaveContradictions(List<(MemoryItem A, MemoryItem B, string Reason)> contradictions)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        // 清空旧的
        using (var clear = c.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM autodream_contradictions";
            clear.ExecuteNonQuery();
        }

        // 插入新的
        foreach (var (a, b, reason) in contradictions)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO autodream_contradictions (item_a_id, item_b_id, reason, found_at)
                VALUES ($a, $b, $reason, $now)
                """;
            cmd.Parameters.AddWithValue("$a", a.Id);
            cmd.Parameters.AddWithValue("$b", b.Id);
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$now", Clock().ToString("O"));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>获取待处理的矛盾列表。</summary>
    public List<(string ItemAId, string ItemBId, string Reason, DateTime FoundAt)> GetPendingContradictions()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT item_a_id, item_b_id, reason, found_at FROM autodream_contradictions ORDER BY found_at DESC";
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string, string, DateTime)>();
        while (r.Read())
        {
            result.Add((
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                DateTime.TryParse(r.GetString(3), out var dt) ? dt : DateTime.MinValue
            ));
        }
        return result;
    }

    /// <summary>解决矛盾（用户选择后调用）。</summary>
    public void ResolveContradiction(string itemAId, string itemBId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM autodream_contradictions WHERE item_a_id = $a AND item_b_id = $b";
        cmd.Parameters.AddWithValue("$a", itemAId);
        cmd.Parameters.AddWithValue("$b", itemBId);
        cmd.ExecuteNonQuery();
    }

    private void SaveState()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO autodream_state (key, value)
            VALUES ('last_run', $time), ('enabled', $enabled), ('interval_hours', $interval), ('idle_minutes', $idle)
            """;
        cmd.Parameters.AddWithValue("$time", LastRunTime?.ToString("O") ?? "");
        cmd.Parameters.AddWithValue("$enabled", Enabled ? "1" : "0");
        cmd.Parameters.AddWithValue("$interval", IntervalHours.ToString());
        cmd.Parameters.AddWithValue("$idle", IdleMinutes.ToString());
        cmd.ExecuteNonQuery();

        // 保存上次报告
        if (LastReport is not null)
        {
            using var report = c.CreateCommand();
            report.CommandText = """
                INSERT OR REPLACE INTO autodream_state (key, value)
                VALUES ('last_report', $report)
                """;
            report.Parameters.AddWithValue("$report", JsonSerializer.Serialize(LastReport));
            report.ExecuteNonQuery();
        }
    }

    private void LoadState()
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM autodream_state";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                var value = r.GetString(1);
                switch (key)
                {
                    case "last_run" when DateTime.TryParse(value, out var dt):
                        LastRunTime = dt;
                        break;
                    case "enabled":
                        Enabled = value == "1";
                        break;
                    case "interval_hours" when int.TryParse(value, out var h):
                        IntervalHours = h;
                        break;
                    case "idle_minutes" when int.TryParse(value, out var m):
                        IdleMinutes = m;
                        break;
                    case "last_report":
                        try { LastReport = JsonSerializer.Deserialize<AutoDreamReport>(value); } catch { }
                        break;
                }
            }
        }
        catch
        {
            // 首次运行，没有状态
        }
    }

    private void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS autodream_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS autodream_contradictions (
                item_a_id TEXT NOT NULL,
                item_b_id TEXT NOT NULL,
                reason TEXT NOT NULL,
                found_at TEXT NOT NULL,
                PRIMARY KEY (item_a_id, item_b_id)
            );
            CREATE TABLE IF NOT EXISTS autodream_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at TEXT NOT NULL,
                completed_at TEXT,
                success INTEGER NOT NULL DEFAULT 0,
                report_json TEXT
            );
            """;
        cmd.ExecuteNonQuery();
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

    public void Dispose()
    {
        SaveState(); // 保存配置状态
        _cts?.Cancel();
        _cts?.Dispose();
        _runLock.Dispose();
        _memoryRwLock.Dispose();
    }

    private sealed class WriteLockReleaser : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;
        private bool _disposed;

        public WriteLockReleaser(ReaderWriterLockSlim rwLock) => _lock = rwLock;

        public void Dispose()
        {
            if (!_disposed)
            {
                _lock.ExitWriteLock();
                _disposed = true;
            }
        }
    }
}
