using System;
using System.IO;
using Flyknit.Core.Setup;
using Microsoft.Win32;

namespace Flyknit.Client.Services;

/// <summary>
/// 把 FlyknitBuddy 挂进资源管理器的右键菜单（当前用户，HKCU，不需要管理员权限）：
/// 文件上「发给 FlyknitBuddy 提问 / 总结 / 翻译」，文件夹和文件夹空白处「在这里用 FlyknitBuddy 办事」。
/// 每次启动按当前程序路径和界面语言重写一遍：装到别的目录、换了语言都会跟着变。
/// Windows 11 上这类菜单在「显示更多选项」里。
/// </summary>
public static class ExplorerMenuRegistration
{
    // 启动时在后台写、换语言和改设置在界面线程写：别让两次「先删再写」交错
    private static readonly object Gate = new();

    public static void Apply(bool enabled, string uiLanguage)
    {
        lock (Gate)
        {
            ApplyCore(enabled, uiLanguage);
        }
    }

    private static void ApplyCore(bool enabled, string uiLanguage)
    {
        try
        {
            if (!enabled)
            {
                Remove();
                return;
            }
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                return;
            }
            Remove(); // 先清掉旧的：菜单项改过名、删过项，残留的不会自己消失

            // 文件：一个「FlyknitBuddy」子菜单，里面三项
            using (var root = Registry.CurrentUser.CreateSubKey(ExplorerMenu.ShellKey(ShellTarget.File)))
            {
                root.SetValue("MUIVerb", ExplorerMenu.Title);
                root.SetValue("Icon", $"\"{exe}\",0");
                root.SetValue("SubCommands", "");
                foreach (var entry in ExplorerMenu.Entries)
                {
                    if (entry.Target != ShellTarget.File)
                    {
                        continue;
                    }
                    using var verb = root.CreateSubKey($@"shell\{entry.Key}");
                    verb.SetValue("MUIVerb", ExplorerMenu.Label(entry.Action, uiLanguage));
                    using var command = verb.CreateSubKey("command");
                    command.SetValue("", ExplorerMenu.Command(exe, entry));
                }
            }

            // 文件夹、文件夹空白处：只有一项，直接放在菜单里
            foreach (var entry in ExplorerMenu.Entries)
            {
                if (entry.Target == ShellTarget.File)
                {
                    continue;
                }
                using var verb = Registry.CurrentUser.CreateSubKey(ExplorerMenu.ShellKey(entry.Target));
                verb.SetValue("MUIVerb", ExplorerMenu.Label(entry.Action, uiLanguage));
                verb.SetValue("Icon", $"\"{exe}\",0");
                using var command = verb.CreateSubKey("command");
                command.SetValue("", ExplorerMenu.Command(exe, entry));
            }
        }
        catch (Exception ex)
        {
            // 组策略禁写注册表之类：菜单没有而已，不影响别的
            Log.Warn("写资源管理器右键菜单失败", ex);
        }
    }

    public static void Remove()
    {
        foreach (var target in new[] { ShellTarget.File, ShellTarget.Folder, ShellTarget.Background })
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(ExplorerMenu.ShellKey(target), throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                Log.Warn("删除资源管理器右键菜单失败", ex);
            }
        }
    }
}
