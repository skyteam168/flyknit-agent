using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Hosting;
using Flyknit.Core.Storage;

namespace Flyknit.Core.Projects;

/// <summary>
/// 一个项目（参照 Claude 的 Projects）：一个工作区文件夹 + 名字 + 项目目标 + 补充说明。
/// 项目里的任务就是工作区在这个文件夹里的那些对话；AI 在项目里做事时，第一轮会看到目标和说明。
/// </summary>
public sealed record Project
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary>项目要做成什么（员工建项目时写的）。</summary>
    public string Goal { get; init; } = "";
    /// <summary>补充说明：约定、背景、要注意的（员工写的）。</summary>
    public string Notes { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset OpenedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>本机的项目清单（%APPDATA%\Flyknit\projects.json）。只记元数据，删除项目不会动文件夹。</summary>
public sealed class ProjectStore
{
    public const int MaxNameChars = 60;
    public const int MaxGoalChars = 2000;
    public const int MaxNotesChars = 8000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _file;
    private readonly object _gate = new();
    private List<Project>? _cache;

    public ProjectStore(string file) => _file = file;

    public IReadOnlyList<Project> All()
    {
        lock (_gate)
        {
            return Load().OrderByDescending(p => p.OpenedAt).ToList();
        }
    }

    public Project? Get(string id)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(p => p.Id == id);
        }
    }

    /// <summary>这个文件夹属于哪个项目（文件夹就是项目目录，或者在它里面；有嵌套时取最近的那个）。</summary>
    public Project? FindFor(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }
        var full = Normalize(folder);
        lock (_gate)
        {
            return Load()
                .Where(p => Inside(Normalize(p.Path), full))
                .OrderByDescending(p => p.Path.Length)
                .FirstOrDefault();
        }
    }

    public Project Add(Project project)
    {
        lock (_gate)
        {
            var list = Load();
            if (list.Any(p => Normalize(p.Path).Equals(Normalize(project.Path), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("这个文件夹已经是一个项目了");
            }
            list.Add(project);
            Save(list);
            return project;
        }
    }

    public Project? Update(string id, Func<Project, Project> change)
    {
        lock (_gate)
        {
            var list = Load();
            var i = list.FindIndex(p => p.Id == id);
            if (i < 0)
            {
                return null;
            }
            list[i] = change(list[i]);
            Save(list);
            return list[i];
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var list = Load();
            var removed = list.RemoveAll(p => p.Id == id) > 0;
            if (removed)
            {
                Save(list);
            }
            return removed;
        }
    }

    private List<Project> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }
        try
        {
            _cache = File.Exists(_file) ? JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(_file), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cache = new();
        }
        return _cache;
    }

    private void Save(List<Project> list)
    {
        _cache = list;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, _file, overwrite: true);
    }

    public static string Normalize(string path) => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

    public static bool Inside(string root, string path)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var rel = System.IO.Path.GetRelativePath(root, path);
        return !System.IO.Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

/// <summary>
/// 界面上的「项目」（左侧菜单）：列出、新建（选已有文件夹，或在某个文件夹里新建一个）、改目标和说明、移除。
/// 新建文件夹是员工自己在界面上点的（和编辑器里新建文件一样），不走 AI 的命令确认；移除项目只删清单，不碰文件夹。
/// </summary>
public sealed class ProjectFeature : IBridgeModule
{
    private readonly Action<string> _log;

    public ProjectFeature(ProjectStore store, Action<string> log)
    {
        Store = store;
        _log = log;
    }

    public ProjectStore Store { get; }

    public IReadOnlyCollection<string> Methods { get; } = new[] { "project.list", "project.create", "project.update", "project.remove", "project.open" };

    public async Task<object?> HandleAsync(string method, BridgeArgs args, CancellationToken ct)
    {
        await Task.Yield();
        return method switch
        {
            "project.list" => List(),
            "project.create" => Create(args.Str("name"), args.Str("goal"), args.Str("notes"), args.Str("path"), args.Str("parent")),
            // 没传的字段不改（传了空字符串就是清空）
            "project.update" => Update(args.Str("id"), args.OptStr("name"), Has(args, "goal") ? args.Str("goal") : null, Has(args, "notes") ? args.Str("notes") : null),
            "project.remove" => new { ok = Store.Remove(args.Str("id")) },
            "project.open" => Open(args.Str("id")),
            _ => null,
        };
    }

    private static bool Has(BridgeArgs args, string name) => args.Raw.ValueKind == JsonValueKind.Object && args.Raw.TryGetProperty(name, out _);

    public object List() => Store.All().Select(p => new
    {
        p.Id,
        p.Name,
        p.Path,
        p.Goal,
        p.Notes,
        p.CreatedAt,
        p.OpenedAt,
        exists = Directory.Exists(p.Path),
    }).ToList();

    /// <summary>
    /// 新建项目。<paramref name="path"/> 是已有的文件夹；没给时在 <paramref name="parent"/> 里新建一个以项目名命名的文件夹。
    /// </summary>
    public object Create(string name, string goal, string notes, string path, string parent)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > ProjectStore.MaxNameChars)
        {
            return new { ok = false, message = $"项目名不能为空，最多 {ProjectStore.MaxNameChars} 个字" };
        }
        if (goal.Length > ProjectStore.MaxGoalChars || notes.Length > ProjectStore.MaxNotesChars)
        {
            return new { ok = false, message = "目标或说明太长了" };
        }
        string folder;
        if (path.Trim().Length > 0)
        {
            folder = ProjectStore.Normalize(path.Trim());
            if (!Directory.Exists(folder))
            {
                return new { ok = false, message = "这个文件夹不存在" };
            }
        }
        else
        {
            if (parent.Trim().Length == 0 || !Directory.Exists(parent))
            {
                return new { ok = false, message = "先选新项目放在哪个文件夹里" };
            }
            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0
                || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
            {
                return new { ok = false, message = "项目名要用作文件夹名，不能带 \\ / : * ? \" < > | 这些字符" };
            }
            folder = ProjectStore.Normalize(System.IO.Path.Combine(parent, name));
            if (!ProjectStore.Inside(ProjectStore.Normalize(parent), folder))
            {
                return new { ok = false, message = "项目名不对" };
            }
            if (Directory.Exists(folder) || File.Exists(folder))
            {
                return new { ok = false, message = $"「{name}」已经存在了，换个名字，或者选「用已有的文件夹」" };
            }
            Directory.CreateDirectory(folder);
            _log($"新建项目文件夹：{folder}");
        }
        try
        {
            var project = Store.Add(new Project
            {
                Id = "pj_" + Guid.NewGuid().ToString("N")[..12],
                Name = name,
                Path = folder,
                Goal = goal.Trim(),
                Notes = notes.Trim(),
            });
            _log($"新建项目「{project.Name}」：{project.Path}");
            return new { ok = true, message = "", id = project.Id, path = project.Path };
        }
        catch (InvalidOperationException ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    public object Update(string id, string? name, string? goal, string? notes)
    {
        if (name is not null && (name.Trim().Length == 0 || name.Trim().Length > ProjectStore.MaxNameChars))
        {
            return new { ok = false, message = $"项目名不能为空，最多 {ProjectStore.MaxNameChars} 个字" };
        }
        if (goal?.Length > ProjectStore.MaxGoalChars || notes?.Length > ProjectStore.MaxNotesChars)
        {
            return new { ok = false, message = "目标或说明太长了" };
        }
        var updated = Store.Update(id, p => p with
        {
            Name = name?.Trim() ?? p.Name,
            Goal = goal?.Trim() ?? p.Goal,
            Notes = notes?.Trim() ?? p.Notes,
        });
        return updated is null ? new { ok = false, message = "项目不存在" } : new { ok = true, message = "" };
    }

    public object Open(string id)
    {
        var p = Store.Update(id, x => x with { OpenedAt = DateTimeOffset.Now });
        return new { ok = p is not null };
    }
}

/// <summary>
/// 运行扩展：任务的工作区在某个项目里时，这个对话第一次发消息时带上项目目标和说明（本轮参考资料，跟着那条消息存下来）。
/// 同一个对话只给一次（之后在历史里）；程序重启后会再给一次，没关系。项目没写目标和说明时什么都不加。
/// </summary>
public sealed class ProjectRunExtension : IRunExtension<RunScope>
{
    public const string ExtensionId = "project_context";

    private readonly ProjectStore _store;
    private readonly ConcurrentDictionary<string, string> _given = new(StringComparer.Ordinal);

    public ProjectRunExtension(ProjectStore store) => _store = store;

    public string Id => ExtensionId;

    public ValueTask<RunContribution?> PrepareAsync(RunScope scope, CancellationToken ct)
    {
        var conv = scope.Conversation;
        if (conv.Mode == ConversationMode.Translate || !scope.NewUserMessage || _store.FindFor(conv.Workspace) is not { } project)
        {
            return ValueTask.FromResult<RunContribution?>(null);
        }
        var key = project.Id + "|" + project.Goal.GetHashCode() + "|" + project.Notes.GetHashCode();
        if (project.Goal.Length == 0 && project.Notes.Length == 0 || _given.TryGetValue(conv.Id, out var seen) && seen == key)
        {
            return ValueTask.FromResult<RunContribution?>(null);
        }
        _given[conv.Id] = key;
        return ValueTask.FromResult<RunContribution?>(new RunContribution { TurnContext = Describe(project) });
    }

    public static string Describe(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<项目 名称=\"{project.Name}\" 说明=\"员工建项目时写的目标和说明，这个任务属于这个项目\">");
        sb.AppendLine($"位置：{project.Path}");
        if (project.Goal.Length > 0)
        {
            sb.AppendLine("目标：" + project.Goal);
        }
        if (project.Notes.Length > 0)
        {
            sb.AppendLine("说明：");
            sb.AppendLine(project.Notes);
        }
        sb.AppendLine("</项目>");
        return sb.ToString();
    }
}
