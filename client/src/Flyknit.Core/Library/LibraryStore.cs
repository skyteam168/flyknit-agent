using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Flyknit.Core.Library;

/// <summary>资料库里的一个文件。</summary>
public sealed class LibraryItem
{
    public string Id { get; init; } = "";

    /// <summary>显示名（可以重命名，不影响磁盘上的文件）。</summary>
    public string Name { get; set; } = "";

    /// <summary>文件在磁盘上的位置。</summary>
    public string Path { get; init; } = "";

    public string Mime { get; init; } = "application/octet-stream";

    /// <summary>image / document / sheet / slides / pdf / audio / video / archive / code / note / other</summary>
    public string Kind { get; init; } = "other";

    public long Size { get; init; }

    /// <summary>upload 用户上传 / paste 粘贴的截图 / output AI 生成 / library 在资料库里新建或上传 / note 备注</summary>
    public string Source { get; init; } = "";

    public string? ConversationId { get; init; }
    public string? FolderId { get; set; }
    public bool Favorite { get; set; }

    /// <summary>文件由资料库保管（复制进来的），删除时连文件一起删；否则只是登记了 AI 产出所在的位置。</summary>
    public bool Managed { get; init; }

    /// <summary>默认不显示的文件：点开头的、AI 干活时留下的中间脚本和日志、_work 目录里的东西。</summary>
    public bool Hidden { get; init; }

    public bool Exists { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed class LibraryFolder
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public int Count { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class LibraryQuery
{
    /// <summary>recent 推荐 / favorites 收藏 / folders 文件夹 / images 图片 / all 全部 / trash 回收站</summary>
    public string Tab { get; init; } = "all";

    public string? FolderId { get; init; }
    public string Search { get; init; } = "";

    /// <summary>只看某一类（image、document…），为空不过滤。</summary>
    public string Kind { get; init; } = "";

    /// <summary>updated 最近修改 / name 名称 / size 大小</summary>
    public string Sort { get; init; } = "updated";

    public bool IncludeHidden { get; init; }
    public int Limit { get; init; } = 500;
}

/// <summary>
/// 资料库：用户在对话里上传的文件、粘贴的截图、AI 生成的文件，以及在资料库里直接上传、新建的东西，都登记在这里。
/// 索引放在 history.db（library_items / library_folders）；上传和截图复制一份放进资料库目录（按内容去重，
/// 同一个文件传多次只存一份），这样原文件被移动、删除或临时目录被清理后还能找到；
/// AI 生成的文件本来就在工作区里，只登记位置，不复制。删除先进回收站，30 天后清理。
/// </summary>
public sealed class LibraryStore
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    /// <summary>超过这个大小的上传不复制，只登记位置。</summary>
    public const long MaxCopyBytes = 200L * 1024 * 1024;

    private static readonly string[] ScratchExtensions = { ".py", ".ps1", ".bat", ".cmd", ".js", ".vbs", ".log", ".tmp", ".pyc" };

    private readonly string _connectionString;
    private readonly object _lock = new();

    /// <summary>资料库自己保管的文件放在这里。</summary>
    public string FilesRoot { get; }

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    public LibraryStore(string databasePath, string filesRoot)
    {
        FilesRoot = filesRoot;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 5, Pooling = false }.ToString();
        Migrate();
    }

    // ---------- 收录 ----------

    /// <summary>
    /// 收录一个文件。<paramref name="copy"/> 为 true 时复制进资料库（同样内容的已有条目直接返回那一条）；
    /// 否则只登记位置（同一路径已登记过就更新大小和时间）。文件不存在返回 null。
    /// </summary>
    public LibraryItem? Add(string sourcePath, string source, bool copy, string? conversationId = null, string? displayName = null, string? folderId = null)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            return null;
        }
        var name = string.IsNullOrWhiteSpace(displayName) ? info.Name : displayName.Trim();
        copy = copy && info.Length <= MaxCopyBytes;
        lock (_lock)
        {
            using var c = Open();
            if (copy)
            {
                var hash = HashFile(info.FullName);
                var same = Find(c, "hash = $v AND managed = 1 AND deleted_at IS NULL", hash);
                if (same is not null && File.Exists(same.Path))
                {
                    return same;
                }
                var id = NewId();
                var dir = System.IO.Path.Combine(FilesRoot, Clock().ToString("yyyy-MM"));
                Directory.CreateDirectory(dir);
                var target = System.IO.Path.Combine(dir, $"{id}-{SafeName(info.Name)}");
                File.Copy(info.FullName, target, overwrite: true);
                Insert(c, id, name, target, info.Length, source, conversationId, folderId, managed: true, hash, info.FullName);
                return Find(c, "id = $v", id);
            }
            var existing = Find(c, "path = $v COLLATE NOCASE AND deleted_at IS NULL", info.FullName);
            if (existing is not null)
            {
                Exec(c, "UPDATE library_items SET size = $s, updated_at = $t WHERE id = $id",
                    ("$s", info.Length), ("$t", Clock().ToString("O")), ("$id", existing.Id));
                return Find(c, "id = $v", existing.Id);
            }
            var newId = NewId();
            Insert(c, newId, name, info.FullName, info.Length, source, conversationId, folderId, managed: false, null, info.FullName);
            return Find(c, "id = $v", newId);
        }
    }

    /// <summary>新建一条备注（Markdown 文本，存成资料库里的 .md 文件）。</summary>
    public LibraryItem AddNote(string title, string text, string? folderId = null)
    {
        title = string.IsNullOrWhiteSpace(title) ? $"备注 {Clock():yyyy-MM-dd HHmm}" : title.Trim();
        var dir = System.IO.Path.Combine(FilesRoot, "notes");
        Directory.CreateDirectory(dir);
        var id = NewId();
        var path = System.IO.Path.Combine(dir, $"{id}-{SafeName(title)}.md");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        lock (_lock)
        {
            using var c = Open();
            Insert(c, id, title.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? title : title + ".md", path,
                new FileInfo(path).Length, "note", null, folderId, managed: true, null, null);
            return Find(c, "id = $v", id)!;
        }
    }

    /// <summary>改备注内容（只能改资料库自己保管的文本文件）。</summary>
    public bool UpdateNote(string id, string text)
    {
        var item = Get(id);
        if (item is null || !item.Managed || item.Kind is not ("note" or "code" or "document") || !File.Exists(item.Path))
        {
            return false;
        }
        File.WriteAllText(item.Path, text, new UTF8Encoding(false));
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "UPDATE library_items SET size = $s, updated_at = $t WHERE id = $id",
                ("$s", new FileInfo(item.Path).Length), ("$t", Clock().ToString("O")), ("$id", id));
        }
        return true;
    }

    /// <summary>
    /// 把历史对话里的上传和产出补登进来（升级后第一次打开资料库时跑一次）。只登记位置，不复制：
    /// 以前的文件大多还在原处，全部复制一遍太占空间；临时目录里的截图例外，不复制就会丢。
    /// </summary>
    public int Backfill(IEnumerable<(string Path, string Source, string? ConversationId, DateTimeOffset At)> files, Func<string, bool> isTemp)
    {
        lock (_lock)
        {
            using var c = Open();
            if (GetMeta(c, "backfilled") is not null)
            {
                return 0;
            }
        }
        var added = 0;
        foreach (var (path, source, conv, at) in files)
        {
            try
            {
                if (Add(path, source, copy: isTemp(path), conv) is { } item)
                {
                    added++;
                    lock (_lock)
                    {
                        using var c = Open();
                        // 用当时的时间，不然所有历史文件都挤在“今天”
                        Exec(c, "UPDATE library_items SET created_at = MIN(created_at, $t), updated_at = MIN(updated_at, $t) WHERE id = $id",
                            ("$t", at.ToString("O")), ("$id", item.Id));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        lock (_lock)
        {
            using var c = Open();
            SetMeta(c, "backfilled", Clock().ToString("O"));
        }
        return added;
    }

    // ---------- 查询 ----------

    public LibraryItem? Get(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            return Find(c, "id = $v", id);
        }
    }

    public List<LibraryItem> List(LibraryQuery q)
    {
        var where = new List<string>();
        var args = new List<(string, object)>();
        where.Add(q.Tab == "trash" ? "deleted_at IS NOT NULL" : "deleted_at IS NULL");
        if (q.FolderId is not null)
        {
            where.Add("folder_id = $folder");
            args.Add(("$folder", q.FolderId));
        }
        switch (q.Tab)
        {
            case "favorites":
                where.Add("favorite = 1");
                break;
            case "images":
                where.Add("kind = 'image'");
                break;
            case "recent":
                // 推荐：收藏的，加上最近 30 天用过的
                where.Add("(favorite = 1 OR updated_at >= $since)");
                args.Add(("$since", Clock().AddDays(-30).ToString("O")));
                break;
        }
        if (q.Kind.Length > 0)
        {
            where.Add("kind = $kind");
            args.Add(("$kind", q.Kind));
        }
        if (q.Search.Trim().Length > 0)
        {
            where.Add("name LIKE $search ESCAPE '\\'");
            args.Add(("$search", "%" + q.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        }
        if (!q.IncludeHidden && q.Tab != "trash")
        {
            where.Add("hidden = 0");
        }
        var order = q.Sort switch
        {
            "name" => "name COLLATE NOCASE",
            "size" => "size DESC",
            _ when q.Tab == "trash" => "deleted_at DESC",
            _ => "updated_at DESC",
        };
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM library_items WHERE {string.Join(" AND ", where)} ORDER BY {order} LIMIT $limit";
            foreach (var (k, v) in args)
            {
                cmd.Parameters.AddWithValue(k, v);
            }
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(q.Limit, 1, 5000));
            return Read(cmd);
        }
    }

    public List<LibraryFolder> Folders()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT f.id, f.name, f.created_at, f.updated_at,
                       (SELECT COUNT(*) FROM library_items i WHERE i.folder_id = f.id AND i.deleted_at IS NULL)
                FROM library_folders f ORDER BY f.name COLLATE NOCASE
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<LibraryFolder>();
            while (r.Read())
            {
                list.Add(new LibraryFolder
                {
                    Id = r.GetString(0),
                    Name = r.GetString(1),
                    CreatedAt = DateTimeOffset.Parse(r.GetString(2)),
                    UpdatedAt = DateTimeOffset.Parse(r.GetString(3)),
                    Count = r.GetInt32(4),
                });
            }
            return list;
        }
    }

    // ---------- 整理 ----------

    public bool Rename(string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 200)
        {
            return false;
        }
        return Update("UPDATE library_items SET name = $n, updated_at = $t WHERE id = $id", ("$n", name), ("$id", id)) > 0;
    }

    public int SetFavorite(IEnumerable<string> ids, bool favorite) =>
        Each(ids, id => Update("UPDATE library_items SET favorite = $f WHERE id = $id", ("$f", favorite ? 1 : 0), ("$id", id)));

    /// <summary>移到文件夹；folderId 为 null 表示移出文件夹。</summary>
    public int Move(IEnumerable<string> ids, string? folderId)
    {
        if (folderId is not null && Folders().All(f => f.Id != folderId))
        {
            return 0;
        }
        return Each(ids, id => Update("UPDATE library_items SET folder_id = $f, updated_at = $t WHERE id = $id",
            ("$f", (object?)folderId ?? DBNull.Value), ("$id", id)));
    }

    /// <summary>删除：进回收站。</summary>
    public int Delete(IEnumerable<string> ids) =>
        Each(ids, id => Update("UPDATE library_items SET deleted_at = $t WHERE id = $id AND deleted_at IS NULL", ("$id", id)));

    public int Restore(IEnumerable<string> ids) =>
        Each(ids, id => Update("UPDATE library_items SET deleted_at = NULL, updated_at = $t WHERE id = $id AND deleted_at IS NOT NULL", ("$id", id)));

    /// <summary>彻底删除：资料库保管的文件一并删掉；AI 产出的文件留在工作区，只是不再登记。</summary>
    public int Purge(IEnumerable<string> ids)
    {
        var count = 0;
        foreach (var id in ids)
        {
            var item = Get(id);
            if (item is null)
            {
                continue;
            }
            if (item.Managed && IsInside(item.Path, FilesRoot))
            {
                // 同一份文件可能被另一条（比如从回收站恢复过的）引用，确认没人用了再删
                lock (_lock)
                {
                    using var c = Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM library_items WHERE path = $p AND id <> $id";
                    cmd.Parameters.AddWithValue("$p", item.Path);
                    cmd.Parameters.AddWithValue("$id", id);
                    if ((long)cmd.ExecuteScalar()! == 0)
                    {
                        TryDelete(item.Path);
                    }
                }
            }
            lock (_lock)
            {
                using var c = Open();
                count += Exec(c, "DELETE FROM library_items WHERE id = $id", ("$id", id));
            }
        }
        return count;
    }

    public int EmptyTrash() => Purge(List(new LibraryQuery { Tab = "trash", Limit = 5000 }).Select(i => i.Id).ToList());

    /// <summary>清理回收站里超过 30 天的。</summary>
    public int PurgeExpired()
    {
        var cutoff = Clock() - TrashRetention;
        return Purge(List(new LibraryQuery { Tab = "trash", Limit = 5000 }).Where(i => i.DeletedAt < cutoff).Select(i => i.Id).ToList());
    }

    public LibraryFolder CreateFolder(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            name = "新建文件夹";
        }
        var id = NewId();
        var now = Clock().ToString("O");
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "INSERT INTO library_folders(id, name, created_at, updated_at) VALUES ($id, $n, $t, $t)", ("$id", id), ("$n", name), ("$t", now));
        }
        return Folders().First(f => f.Id == id);
    }

    public bool RenameFolder(string id, string name)
    {
        name = name.Trim();
        return name.Length > 0 && Update("UPDATE library_folders SET name = $n, updated_at = $t WHERE id = $id", ("$n", name), ("$id", id)) > 0;
    }

    /// <summary>删除文件夹：里面的文件不删，移回资料库根目录。</summary>
    public bool DeleteFolder(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            Exec(c, "UPDATE library_items SET folder_id = NULL WHERE folder_id = $id", ("$id", id));
            return Exec(c, "DELETE FROM library_folders WHERE id = $id", ("$id", id)) > 0;
        }
    }

    // ---------- 工具方法 ----------

    public static string KindOf(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".ico" or ".tif" or ".tiff" or ".heic" => "image",
            ".doc" or ".docx" or ".rtf" or ".odt" or ".txt" or ".md" or ".markdown" => "document",
            ".xls" or ".xlsx" or ".xlsm" or ".csv" or ".ods" => "sheet",
            ".ppt" or ".pptx" or ".odp" => "slides",
            ".pdf" => "pdf",
            ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".wma" => "audio",
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".webm" => "video",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "archive",
            ".py" or ".ps1" or ".bat" or ".cmd" or ".js" or ".ts" or ".json" or ".xml" or ".html" or ".css" or ".sql" or ".cs" or ".java" or ".yaml" or ".yml" or ".mmd" or ".dot" or ".log" or ".vbs" => "code",
            _ => "other",
        };
    }

    /// <summary>默认不显示：点开头的文件、_work 目录里的、AI 产出的脚本和日志（一般是干活时的中间产物）。</summary>
    public static bool IsHidden(string path, string source)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith("~$"))
        {
            return true;
        }
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/_work/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/__pycache__/", StringComparison.Ordinal))
        {
            return true;
        }
        return source == "output" && ScratchExtensions.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant());
    }

    private const string Columns = "id, name, path, mime, kind, size, source, conversation_id, folder_id, favorite, managed, hidden, created_at, updated_at, deleted_at";

    private void Insert(SqliteConnection c, string id, string name, string path, long size, string source, string? conversationId, string? folderId, bool managed, string? hash, string? origin)
    {
        var now = Clock().ToString("O");
        var kind = source == "note" ? "note" : KindOf(name);
        Exec(c, """
            INSERT INTO library_items(id, name, path, mime, kind, size, source, conversation_id, folder_id, favorite, managed, hidden, hash, origin_path, created_at, updated_at)
            VALUES ($id, $name, $path, $mime, $kind, $size, $source, $conv, $folder, 0, $managed, $hidden, $hash, $origin, $t, $t)
            """,
            ("$id", id), ("$name", name), ("$path", path), ("$mime", MimeOf(name)), ("$kind", kind), ("$size", size),
            ("$source", source), ("$conv", (object?)conversationId ?? DBNull.Value), ("$folder", (object?)folderId ?? DBNull.Value),
            ("$managed", managed ? 1 : 0), ("$hidden", IsHidden(origin ?? path, source) ? 1 : 0),
            ("$hash", (object?)hash ?? DBNull.Value), ("$origin", (object?)origin ?? DBNull.Value), ("$t", now));
    }

    private LibraryItem? Find(SqliteConnection c, string where, string value)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM library_items WHERE {where} LIMIT 1";
        cmd.Parameters.AddWithValue("$v", value);
        return Read(cmd).FirstOrDefault();
    }

    private static List<LibraryItem> Read(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<LibraryItem>();
        while (r.Read())
        {
            var path = r.GetString(2);
            list.Add(new LibraryItem
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Path = path,
                Mime = r.GetString(3),
                Kind = r.GetString(4),
                Size = r.GetInt64(5),
                Source = r.GetString(6),
                ConversationId = r.IsDBNull(7) ? null : r.GetString(7),
                FolderId = r.IsDBNull(8) ? null : r.GetString(8),
                Favorite = r.GetInt32(9) == 1,
                Managed = r.GetInt32(10) == 1,
                Hidden = r.GetInt32(11) == 1,
                CreatedAt = DateTimeOffset.Parse(r.GetString(12)),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(13)),
                DeletedAt = r.IsDBNull(14) ? null : DateTimeOffset.Parse(r.GetString(14)),
                Exists = File.Exists(path),
            });
        }
        return list;
    }

    private int Update(string sql, params (string, object)[] args)
    {
        lock (_lock)
        {
            using var c = Open();
            return Exec(c, sql, args.Append(("$t", Clock().ToString("O"))).ToArray());
        }
    }

    private static int Each(IEnumerable<string> ids, Func<string, int> action) => ids.Distinct().Sum(action);

    private static int Exec(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args)
        {
            if (!cmd.Parameters.Contains(k))
            {
                cmd.Parameters.AddWithValue(k, v);
            }
        }
        return cmd.ExecuteNonQuery();
    }

    private static string? GetMeta(SqliteConnection c, string key)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM library_meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection c, string key, string value) =>
        Exec(c, "INSERT INTO library_meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS library_items (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                path TEXT NOT NULL,
                mime TEXT NOT NULL,
                kind TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL,
                conversation_id TEXT NULL,
                folder_id TEXT NULL,
                favorite INTEGER NOT NULL DEFAULT 0,
                managed INTEGER NOT NULL DEFAULT 0,
                hidden INTEGER NOT NULL DEFAULT 0,
                hash TEXT NULL,
                origin_path TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_library_items_updated ON library_items(deleted_at, updated_at);
            CREATE INDEX IF NOT EXISTS ix_library_items_hash ON library_items(hash);
            CREATE INDEX IF NOT EXISTS ix_library_items_path ON library_items(path);
            CREATE TABLE IF NOT EXISTS library_folders (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS library_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];

    private static string SafeName(string name)
    {
        var safe = string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToArray())).Trim();
        if (safe.Length > 80)
        {
            var ext = System.IO.Path.GetExtension(safe);
            safe = safe[..(80 - Math.Min(ext.Length, 20))] + ext[..Math.Min(ext.Length, 20)];
        }
        return safe.Length == 0 ? "file" : safe;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsInside(string path, string root)
    {
        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string MimeOf(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".pdf" => "application/pdf",
        ".txt" or ".log" => "text/plain",
        ".md" or ".markdown" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".html" => "text/html",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };
}
