using System.Text.Json;

namespace Flyknit.Core.Security;

/// <summary>
/// AI 专用文件夹（docs/trusted-folders.md）：员工把一个文件夹标成「专门给 AI 干活用的，里面的东西我放心」，
/// 任务的工作区是它时，里面的普通操作不再逐个确认——删除（进回收站）、认不出的命令（python 脚本这类）、
/// 只动这个文件夹的高危命令都自动执行。只放宽文件夹里面：碰到文件夹外的路径照样按权限档确认，
/// 禁止规则、系统级工具、网络白名单、读到疑似注入后的确认都不受影响。
///
/// 两道闸：
/// 1. IT 在安全中心打开「AI 专用文件夹」（trusted_folders，默认关、可锁定）——关着时标了也不生效；
/// 2. 有些地方不能标：磁盘根目录、网络共享 / 网络盘、系统目录（Windows、Program Files、ProgramData）、
///    用户目录本身和它下面第一层（桌面、文档、下载……）、AppData，以及包含这些地方的上层文件夹。
///    判断时把链接（junction / 符号链接）解开，防止用一个指向系统目录的链接绕过去。
///
/// 记在本机（%APPDATA%\Flyknit\trusted-folders.json），只对这台电脑、这个 Windows 用户有效。
/// </summary>
public sealed class TrustedFolders
{
    public const int MaxFolders = 50;

    private readonly string? _file;
    private readonly object _lock = new();
    private readonly List<string> _items = new();

    /// <param name="file">保存的文件；为空只在内存里（单测）。</param>
    public TrustedFolders(string? file)
    {
        _file = file;
        if (file is null || !File.Exists(file))
        {
            return;
        }
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? new();
            _items.AddRange(list.Where(x => !string.IsNullOrWhiteSpace(x)).Take(MaxFolders));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 读不了就当没标过：最多是多问几次，不会少问
        }
    }

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_lock)
            {
                return _items.ToList();
            }
        }
    }

    /// <summary>这个工作区是不是 AI 专用文件夹（标过、而且现在仍然能标——比如没被换成指向系统目录的链接）。</summary>
    public bool Contains(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return false;
        }
        bool marked;
        lock (_lock)
        {
            marked = _items.Any(x => Same(x, workspace));
        }
        return marked && Ineligible(workspace) is null;
    }

    /// <summary>标上 / 取消。返回 null 表示成功，否则是不能标的原因（给员工看）。</summary>
    public string? Set(string folder, bool trusted)
    {
        folder = Normalize(folder);
        if (trusted && Ineligible(folder) is { } why)
        {
            return why;
        }
        lock (_lock)
        {
            _items.RemoveAll(x => Same(x, folder));
            if (trusted)
            {
                if (_items.Count >= MaxFolders)
                {
                    return $"最多标 {MaxFolders} 个 AI 专用文件夹，先取消几个不用的";
                }
                _items.Add(folder);
            }
            Save();
        }
        return null;
    }

    /// <summary>不能标的原因；能标返回 null。</summary>
    public static string? Ineligible(string folder)
    {
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
            {
                return "要选一个完整的文件夹路径";
            }
            // 只写了盘符（D:）：按磁盘根目录算，不按「这个盘的当前目录」
            if (System.Text.RegularExpressions.Regex.IsMatch(folder.Trim().Trim('"'), @"^[A-Za-z]:[\\/]?$"))
            {
                return "磁盘根目录不能设为 AI 专用，请在里面新建一个文件夹专门给 AI 用";
            }
            full = Normalize(folder);
        }
        catch (Exception)
        {
            return "路径无效";
        }
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
        {
            return "网络共享文件夹不能设为 AI 专用（别人也在用，里面的东西不一定能随便改）";
        }
        if (!Directory.Exists(full))
        {
            return "文件夹不存在";
        }
        // 链接解开以后再判断：工作区本身是一个指向 C:\Windows 的 junction 时按 C:\Windows 算
        var real = Normalize(PathResolver.RealPath(full));
        foreach (var path in new[] { full, real }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Check(path) is { } why)
            {
                return why;
            }
        }
        return null;
    }

    private static string? Check(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || Same(full, root))
        {
            return "磁盘根目录不能设为 AI 专用，请在里面新建一个文件夹专门给 AI 用";
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (new DriveInfo(root).DriveType == DriveType.Network)
                {
                    return "网络盘上的文件夹不能设为 AI 专用（别人也在用，里面的东西不一定能随便改）";
                }
            }
            catch (Exception)
            {
                // 认不出盘的类型：按本地盘算，后面的检查照常
            }
        }
        foreach (var system in SystemFolders())
        {
            if (PathResolver.IsUnder(full, system) || PathResolver.IsUnder(system, full))
            {
                return $"{system} 是系统目录（或者在这个文件夹里面），不能设为 AI 专用";
            }
        }
        var profile = Folder(Environment.SpecialFolder.UserProfile);
        if (profile is not null)
        {
            if (PathResolver.IsUnder(profile, full))
            {
                return "用户目录本身（以及包含它的上层文件夹）不能设为 AI 专用，请选一个专门给 AI 用的子文件夹";
            }
            // 桌面、文档、下载这些第一层文件夹放的是员工自己的东西
            if (Same(Path.GetDirectoryName(full.TrimEnd('\\', '/')) ?? "", profile))
            {
                return $"「{Path.GetFileName(full.TrimEnd('\\', '/'))}」整个文件夹不能设为 AI 专用，请在里面新建一个文件夹专门给 AI 用";
            }
        }
        foreach (var data in new[] { Folder(Environment.SpecialFolder.ApplicationData), Folder(Environment.SpecialFolder.LocalApplicationData) })
        {
            if (data is not null && (PathResolver.IsUnder(full, data) || PathResolver.IsUnder(data, full)))
            {
                return "程序数据目录（AppData）不能设为 AI 专用";
            }
        }
        return null;
    }

    private static IEnumerable<string> SystemFolders()
    {
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                 })
        {
            if (Folder(f) is { } path)
            {
                yield return path;
            }
        }
    }

    private static string? Folder(Environment.SpecialFolder f)
    {
        var path = Environment.GetFolderPath(f);
        // Linux 上（单测）CommonApplicationData 是 /usr/share，按系统目录算；空的、根目录的跳过
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        path = Normalize(path);
        return Same(path, Path.GetPathRoot(path) ?? "") ? null : path;
    }

    /// <summary>
    /// 完整路径，去掉末尾的斜杠；磁盘根目录保留成 `D:\`。
    /// 不能把根目录规整成 `D:`：Windows 上 `D:` 是「D 盘的当前目录」，再规整一次就成了别的文件夹，会绕过「磁盘根目录不能标」。
    /// </summary>
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && Same(full, root))
        {
            return root;
        }
        return full.TrimEnd('\\', '/');
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private void Save()
    {
        if (_file is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_items));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不了：这次打开期间有效，下次要重新标
        }
    }
}

/// <summary>
/// AI 专用文件夹的界面方法（功能模块，docs/extension-architecture.md）：
/// - trust.get { workspace } → { allowed, trusted, reason }：IT 开没开、这个文件夹标没标、不能标的原因；
/// - trust.set { workspace, trusted } → { ok, message, trusted }。IT 没开时拒绝标（取消照样能取消）。
/// </summary>
public sealed class TrustedFolderFeature : Hosting.IBridgeModule
{
    private readonly TrustedFolders _store;
    private readonly Func<bool> _allowed;
    private readonly Action<string> _log;

    public TrustedFolderFeature(TrustedFolders store, Func<bool> allowed, Action<string> log)
    {
        _store = store;
        _allowed = allowed;
        _log = log;
    }

    public TrustedFolders Store => _store;

    public IReadOnlyCollection<string> Methods { get; } = new[] { "trust.get", "trust.set" };

    public Task<object?> HandleAsync(string method, Hosting.BridgeArgs args, CancellationToken ct)
    {
        var workspace = args.Str("workspace");
        if (method == "trust.get")
        {
            return Task.FromResult<object?>(new
            {
                allowed = _allowed(),
                trusted = _store.Contains(workspace),
                reason = workspace.Length > 0 ? TrustedFolders.Ineligible(workspace) ?? "" : "",
            });
        }
        var trusted = args.Bool("trusted");
        if (trusted && !_allowed())
        {
            return Task.FromResult<object?>(new { ok = false, message = "IT 没有开放「AI 专用文件夹」", trusted = false });
        }
        var error = _store.Set(workspace, trusted);
        if (error is null)
        {
            _log($"AI 专用文件夹：{(trusted ? "标上" : "取消")} {workspace}");
        }
        return Task.FromResult<object?>(new { ok = error is null, message = error ?? "", trusted = _store.Contains(workspace) });
    }
}
