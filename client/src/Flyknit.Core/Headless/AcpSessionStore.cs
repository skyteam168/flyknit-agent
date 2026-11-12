using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Flyknit.Core.Chat;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Headless;

/// <summary>保存下来的一个 IDE 会话。</summary>
public sealed class SavedAcpSession
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";

    /// <summary>工作区（IDE 打开的项目文件夹）。</summary>
    public string Cwd { get; set; } = "";

    /// <summary>第一句话的开头，给 IDE 的会话列表看。</summary>
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    public List<PlanItem> Plan { get; set; } = new();
    public List<string> LoadedTools { get; set; } = new();

    /// <summary>/compact 之后较早对话的摘要（老文件没有）。</summary>
    public string? Summary { get; set; }
}

/// <summary>会话列表里的一行（不带消息）。</summary>
public sealed record AcpSessionInfo(string Id, string Cwd, string Title, DateTimeOffset UpdatedAt);

/// <summary>
/// IDE 会话存在本机：%APPDATA%\Flyknit\ide-sessions\&lt;会话 ID&gt;.json，和员工端的对话记录一样只在这台电脑上，
/// 不上传、不进员工端的历史。IDE 关掉、插件重启以后可以在会话列表里找回来接着聊（ACP session/list、session/load）。
///
/// 只存对话本身（消息、计划、加载过的工具）；权限档、安全设置每次按当时的配置来，不从文件里读，
/// 免得旧文件把已经收紧的设置带回来。最多留 <see cref="MaxSessions"/> 个、<see cref="KeepFor"/> 以内的。
/// </summary>
public sealed class AcpSessionStore
{
    public const int MaxSessions = 50;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
    public const int TitleChars = 60;

    /// <summary>会话 ID 只认 acp- 开头的十六进制：ID 来自 IDE，要拿来拼文件名，不能带路径。</summary>
    private static readonly Regex IdPattern = new("^acp-[0-9a-f]{12,32}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();

    public AcpSessionStore(string dir, Func<DateTimeOffset>? now = null)
    {
        _dir = dir;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    public static string TitleOf(string text)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        while (line.Contains("  ", StringComparison.Ordinal))
        {
            line = line.Replace("  ", " ", StringComparison.Ordinal);
        }
        return line.Length > TitleChars ? line[..TitleChars] + "…" : line;
    }

    private string PathOf(string id) => Path.Combine(_dir, id + ".json");

    /// <summary>保存（整份覆盖，先写临时文件再换名，写一半断电不会留下坏文件）。失败不抛：存不下来不能让这一轮失败。</summary>
    public bool Save(HeadlessSession session, string firstPrompt, DateTimeOffset createdAt, Action<string>? log = null)
    {
        if (!IsValidId(session.Id) || session.History.Count == 0)
        {
            return false;
        }
        var saved = new SavedAcpSession
        {
            Id = session.Id,
            Cwd = session.Workspace,
            Title = TitleOf(firstPrompt),
            CreatedAt = createdAt,
            UpdatedAt = _now(),
            Messages = session.History.ToList(),
            Plan = session.Plan.ToList(),
            LoadedTools = session.LoadedTools.ToList(),
            Summary = session.Summary,
        };
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var path = PathOf(session.Id);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
                // 清理按文件时间排：和记录里的更新时间对齐
                File.SetLastWriteTimeUtc(path, saved.UpdatedAt.UtcDateTime);
                PruneLocked();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                log?.Invoke($"保存 IDE 会话失败：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>读一个会话；没有、ID 不对、文件坏了返回 null。</summary>
    public SavedAcpSession? Load(string? id)
    {
        if (!IsValidId(id))
        {
            return null;
        }
        lock (_lock)
        {
            return Read(PathOf(id!));
        }
    }

    /// <summary>会话列表，最近的在前。给了 cwd 就只列这个工作区的。</summary>
    public IReadOnlyList<AcpSessionInfo> List(string? cwd = null)
    {
        lock (_lock)
        {
            if (!Directory.Exists(_dir))
            {
                return Array.Empty<AcpSessionInfo>();
            }
            var want = string.IsNullOrWhiteSpace(cwd) ? null : Normalize(cwd);
            return Directory.EnumerateFiles(_dir, "acp-*.json")
                .Select(Read)
                .Where(s => s is not null && IsValidId(s.Id) && (want is null || Normalize(s.Cwd) == want))
                .Select(s => new AcpSessionInfo(s!.Id, s.Cwd, s.Title, s.UpdatedAt))
                .OrderByDescending(s => s.UpdatedAt)
                .ToList();
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.ToLowerInvariant();
        }
    }

    private static SavedAcpSession? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<SavedAcpSession>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>太旧的、超出个数的删掉。</summary>
    private void PruneLocked()
    {
        var files = Directory.EnumerateFiles(_dir, "acp-*.json")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        var cutoff = _now() - KeepFor;
        for (var i = 0; i < files.Count; i++)
        {
            if (i >= MaxSessions || files[i].LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                try
                {
                    files[i].Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
