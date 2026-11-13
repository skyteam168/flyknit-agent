using System.Globalization;
using Flyknit.Core.Tools;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Git;

/// <summary>一个为拆分子任务准备的独立工作副本（docs/c4-parallel-writes.md）。</summary>
public sealed record Worktree(
    string Id,
    string ConversationId,
    string RepoDir,
    string Path,
    string Branch,
    string BaseCommit,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RemovedAt);

/// <summary>
/// git worktree：拆开的几件在没有 Docker 时各自的工作副本。
/// - 放在员工工作区之外（宿主给的根目录，%LOCALAPPDATA%\Flyknit\worktrees）；
/// - 从员工当前分支**已提交**的内容开出新分支 flyknit/&lt;时间&gt;-&lt;6位&gt;，不切换、不改员工当前分支，未提交的改动不带过去；
/// - 有未提交的改动就不删。
/// 所有命令按参数列表执行（GitRunner），不经过 Shell。
/// </summary>
public static class WorktreeManager
{
    public const string BranchPrefix = "flyknit/";

    public sealed record Created(string Path, string Branch, string BaseCommit, string RepoRoot);

    public static async Task<Created> CreateAsync(string repoDir, string root, CancellationToken ct, Func<DateTimeOffset>? now = null)
    {
        var top = await Git(repoDir, ct, "rev-parse", "--show-toplevel");
        var head = await Git(top, ct, "rev-parse", "HEAD");
        var id = Guid.NewGuid().ToString("N")[..6];
        var stamp = (now?.Invoke() ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var branch = $"{BranchPrefix}{stamp}-{id}";
        var name = System.IO.Path.GetFileName(top.TrimEnd('/', '\\'));
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, $"{name}-{stamp}-{id}");
        await Git(top, ct, "worktree", "add", "-b", branch, path, head);
        return new Created(path, branch, head, top);
    }

    /// <summary>工作副本里有没有没提交的改动（含未跟踪文件）。目录不在了算没有。</summary>
    public static async Task<bool> IsDirtyAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }
        var r = await GitRunner.RunAsync(path, new[] { "status", "--porcelain" }, ct);
        return r.ExitCode != 0 || r.Stdout.Trim().Length > 0;
    }

    /// <summary>
    /// 删除工作副本（分支保留：推上去的在远端，本地的员工可以自己清）。有未提交的改动不删，返回原因。
    /// </summary>
    public static async Task<(bool Removed, string Reason)> RemoveAsync(string repoDir, string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            await GitRunner.RunAsync(repoDir, new[] { "worktree", "prune" }, ct);
            return (true, "");
        }
        if (await IsDirtyAsync(path, ct))
        {
            return (false, "工作副本里还有没提交的改动");
        }
        var r = await GitRunner.RunAsync(repoDir, new[] { "worktree", "remove", path }, ct);
        return r.ExitCode == 0 ? (true, "") : (false, (r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim());
    }

    /// <summary>这个仓库登记的所有工作副本路径（git worktree list --porcelain）。</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(string repoDir, CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(repoDir, new[] { "worktree", "list", "--porcelain" }, ct);
        return r.Stdout.Split('\n').Where(l => l.StartsWith("worktree ", StringComparison.Ordinal)).Select(l => l["worktree ".Length..].Trim()).ToList();
    }

    private static async Task<string> Git(string dir, CancellationToken ct, params string[] args)
    {
        var r = await GitRunner.RunAsync(dir, args, ct);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args[0]} 失败：{(r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Trim()}");
        }
        return r.Stdout.Trim();
    }
}

/// <summary>几个分支相对起点各改了哪些文件，找出改到同一个文件的（合并时可能冲突）。</summary>
public static class ChangeOverlap
{
    public static async Task<IReadOnlyList<string>> FilesChangedAsync(string repoDir, string baseCommit, string branch, CancellationToken ct)
    {
        var r = await GitRunner.RunAsync(repoDir, new[] { "diff", "--name-only", $"{baseCommit}...{branch}" }, ct);
        return r.ExitCode != 0 ? Array.Empty<string>() : r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>文件 → 改了它的那几件（只列被两件及以上改到的）。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Find(IReadOnlyDictionary<string, IReadOnlyList<string>> filesByLabel)
    {
        var byFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, files) in filesByLabel)
        {
            foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                (byFile.TryGetValue(f, out var l) ? l : byFile[f] = new List<string>()).Add(label);
            }
        }
        return byFile.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>工作副本登记（和对话同一个库，只加不删）：清理时知道哪些是我们建的、属于哪个对话。</summary>
public sealed class WorktreeStore
{
    private readonly Func<SqliteConnection> _open;
    private readonly Func<DateTimeOffset> _now;

    public WorktreeStore(Func<SqliteConnection> open, Func<DateTimeOffset>? now = null)
    {
        _open = open;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static void Migrate(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS worktrees (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                repo_dir TEXT NOT NULL,
                path TEXT NOT NULL,
                branch TEXT NOT NULL,
                base_commit TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                removed_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_worktrees_conversation ON worktrees (conversation_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public Worktree Add(string conversationId, WorktreeManager.Created w)
    {
        var row = new Worktree("w" + Guid.NewGuid().ToString("N")[..12], conversationId, w.RepoRoot, w.Path, w.Branch, w.BaseCommit, _now(), null);
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO worktrees (id, conversation_id, repo_dir, path, branch, base_commit, created_at) VALUES ($id, $conv, $repo, $path, $branch, $base, $at)";
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$repo", row.RepoDir);
        cmd.Parameters.AddWithValue("$path", row.Path);
        cmd.Parameters.AddWithValue("$branch", row.Branch);
        cmd.Parameters.AddWithValue("$base", row.BaseCommit);
        cmd.Parameters.AddWithValue("$at", row.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return row;
    }

    /// <summary>这个对话还在用的工作副本（没删的）。</summary>
    public Worktree? ActiveFor(string conversationId) => Query("WHERE conversation_id = $p AND removed_at IS NULL", conversationId).FirstOrDefault();

    public IReadOnlyList<Worktree> Active() => Query("WHERE removed_at IS NULL", null);

    public void MarkRemoved(string id)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE worktrees SET removed_at = $at WHERE id = $id AND removed_at IS NULL";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$at", _now().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private List<Worktree> Query(string where, string? param)
    {
        using var c = _open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, conversation_id, repo_dir, path, branch, base_commit, created_at, removed_at FROM worktrees {where} ORDER BY created_at";
        if (param is not null)
        {
            cmd.Parameters.AddWithValue("$p", param);
        }
        using var r = cmd.ExecuteReader();
        var list = new List<Worktree>();
        while (r.Read())
        {
            list.Add(new Worktree(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
                r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture)));
        }
        return list;
    }
}
