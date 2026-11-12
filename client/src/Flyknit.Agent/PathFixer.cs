using System.Runtime.InteropServices;
using Flyknit.Setup;
using Microsoft.Win32;

namespace Flyknit.Agent;

/// <summary>
/// 把员工端目录加进系统 PATH，命令行里能直接敲 flyknit（docs/agent-self-update.md 4.3）。
///
/// 以前只有安装程序做这件事；命令行功能之前装的电脑，代理把员工端升级上来以后 flyknit.exe 有了、PATH 里却没有。
/// 只追加员工端这一个目录（来自代理自己的配置，不接受服务端或员工给的路径），去重、大小写不敏感，不动别的项。
/// 不受「运维代理自动升级」开关控制：只改 PATH、可逆，卸载时安装程序会去掉。
/// </summary>
public static class PathFixer
{
    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public static void Ensure(string clientDir)
    {
        if (!OperatingSystem.IsWindows() || clientDir.Length == 0)
        {
            return;
        }
        try
        {
            // 只认安装程序装的、归代理管的员工端目录，并且里面确实有 flyknit.exe
            if (!File.Exists(Path.Combine(clientDir, ClientUpdater.ManagedMarker)) || !File.Exists(Path.Combine(clientDir, "flyknit.exe")))
            {
                return;
            }
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(EnvironmentKey, writable: true);
            if (key is null)
            {
                return;
            }
            var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var updated = PathList.Add(current, clientDir);
            if (updated == current)
            {
                return;
            }
            key.SetValue("Path", updated, RegistryValueKind.ExpandString);
            // 通知资源管理器等环境变量变了：从它新开的命令行窗口就能用（已经开着的窗口不会变）
            SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
            AgentLog.Info($"已把 {clientDir} 加入系统 PATH（命令行 flyknit，新开的窗口生效）");
        }
        catch (Exception ex)
        {
            AgentLog.Warn("把员工端目录加入 PATH 失败（不影响别的功能）", ex);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
