namespace Flyknit.Core.Setup;

/// <summary>从资源管理器右键菜单发过来的动作。</summary>
public enum ShellAction
{
    /// <summary>把文件作为附件开一个新对话。</summary>
    Ask,

    /// <summary>总结这个文件。</summary>
    Summarize,

    /// <summary>把这个文件翻译成界面语言。</summary>
    Translate,

    /// <summary>在这个文件夹里办事：把它设为工作区，开一个新对话。</summary>
    Workspace,
}

/// <summary>
/// 一次右键菜单调用：<c>Flyknit.Client.exe --shell summarize "D:\报表.xlsx"</c>。
/// 程序已经在运行时，新进程把参数从命名管道转给原来的进程就退出。
/// </summary>
public sealed record ShellRequest(ShellAction Action, string Path)
{
    public const string Flag = "--shell";

    public static ShellRequest? Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 2 < args.Count; i++)
        {
            if (!string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!Enum.TryParse<ShellAction>(args[i + 1], ignoreCase: true, out var action) || !Enum.IsDefined(action))
            {
                return null;
            }
            // 盘符根目录时 "%V" 会变成 D:\" —— 反斜杠把结尾的引号转义了，参数里会多出一个引号
            var path = args[i + 2].Trim().Trim('"');
            return path.Length == 0 ? null : new ShellRequest(action, path);
        }
        return null;
    }

    public string[] ToArgs() => new[] { Flag, Action.ToString().ToLowerInvariant(), Path };
}

/// <summary>右键菜单里的一项：挂在哪类对象上（文件、文件夹、文件夹空白处），点了做什么。</summary>
public sealed record ShellMenuEntry(string Key, ShellTarget Target, ShellAction Action);

public enum ShellTarget
{
    /// <summary>任意文件（HKCU\Software\Classes\*）。</summary>
    File,

    /// <summary>文件夹本身。</summary>
    Folder,

    /// <summary>在文件夹空白处点右键。</summary>
    Background,
}

/// <summary>
/// 资源管理器右键菜单的内容。写注册表在客户端（只有 Windows 有），这里只管「有哪几项、叫什么、命令行怎么拼」，方便测试。
/// 写在当前用户（HKCU），不需要管理员权限。Windows 11 上这类菜单在「显示更多选项」里。
/// </summary>
public static class ExplorerMenu
{
    /// <summary>注册表里的键名，卸载、关掉开关时按这个删。</summary>
    public const string KeyName = "FlyknitBuddy";

    public const string Title = "FlyknitBuddy";

    public static IReadOnlyList<ShellMenuEntry> Entries { get; } = new[]
    {
        new ShellMenuEntry("01ask", ShellTarget.File, ShellAction.Ask),
        new ShellMenuEntry("02summarize", ShellTarget.File, ShellAction.Summarize),
        new ShellMenuEntry("03translate", ShellTarget.File, ShellAction.Translate),
        new ShellMenuEntry("workspace", ShellTarget.Folder, ShellAction.Workspace),
        new ShellMenuEntry("workspace", ShellTarget.Background, ShellAction.Workspace),
    };

    /// <summary>注册表里挂菜单的位置（相对 HKCU）。</summary>
    public static string ShellKey(ShellTarget target) => target switch
    {
        ShellTarget.File => $@"Software\Classes\*\shell\{KeyName}",
        ShellTarget.Folder => $@"Software\Classes\Directory\shell\{KeyName}",
        _ => $@"Software\Classes\Directory\Background\shell\{KeyName}",
    };

    /// <summary>命令行。文件夹空白处要用 %V（当前文件夹），其余用 %1（点中的那个）。</summary>
    public static string Command(string exe, ShellMenuEntry entry) =>
        $"\"{exe}\" {ShellRequest.Flag} {entry.Action.ToString().ToLowerInvariant()} \"{(entry.Target == ShellTarget.Background ? "%V" : "%1")}\"";

    public static string Label(ShellAction action, string uiLanguage)
    {
        var lang = uiLanguage.StartsWith("vi", StringComparison.OrdinalIgnoreCase) ? "vi"
            : uiLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        return (action, lang) switch
        {
            (ShellAction.Ask, "vi") => "Gửi cho FlyknitBuddy để hỏi",
            (ShellAction.Ask, "en") => "Ask FlyknitBuddy about it",
            (ShellAction.Ask, _) => "发给 FlyknitBuddy 提问",
            (ShellAction.Summarize, "vi") => "Tóm tắt bằng FlyknitBuddy",
            (ShellAction.Summarize, "en") => "Summarize with FlyknitBuddy",
            (ShellAction.Summarize, _) => "用 FlyknitBuddy 总结",
            (ShellAction.Translate, "vi") => "Dịch bằng FlyknitBuddy",
            (ShellAction.Translate, "en") => "Translate with FlyknitBuddy",
            (ShellAction.Translate, _) => "用 FlyknitBuddy 翻译",
            (ShellAction.Workspace, "vi") => "Làm việc ở đây với FlyknitBuddy",
            (ShellAction.Workspace, "en") => "Work here with FlyknitBuddy",
            _ => "在这里用 FlyknitBuddy 办事",
        };
    }
}

/// <summary>
/// 多选文件时，资源管理器给每个文件各起一次命令。把短时间内同一个动作的请求攒成一批，
/// 几个文件进同一个对话，而不是开好几个。
/// </summary>
public sealed class ShellRequestBatcher
{
    private readonly List<ShellRequest> _pending = new();
    private readonly object _gate = new();

    public void Add(ShellRequest request)
    {
        lock (_gate)
        {
            _pending.Add(request);
        }
    }

    /// <summary>取走攒着的请求：同一个动作合成一批（路径去重、保持顺序）。工作区只留最后一个。</summary>
    public IReadOnlyList<(ShellAction Action, IReadOnlyList<string> Paths)> Drain()
    {
        List<ShellRequest> taken;
        lock (_gate)
        {
            taken = _pending.ToList();
            _pending.Clear();
        }
        var result = new List<(ShellAction, IReadOnlyList<string>)>();
        foreach (var group in taken.GroupBy(r => r.Action))
        {
            var paths = group.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Add((group.Key, group.Key == ShellAction.Workspace ? new[] { paths[^1] } : paths));
        }
        return result;
    }
}
