using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Flyknit.Setup;

// FlyknitBuddy 安装程序。
//   双击：安装（员工端 + 运维代理），装完打开员工端
//   /S：静默安装（组策略启动脚本、SCCM 用），不弹窗、不打开员工端，结果看退出码和日志
//   /uninstall [/S]：卸载（「应用和功能」里点卸载走这里）
Console.OutputEncoding = Encoding.UTF8;
var silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
var uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
var self = Environment.ProcessPath ?? "";
var log = Path.Combine(Installer.DataDir, "setup.log");

void Say(string message)
{
    Console.WriteLine(message);
    try
    {
        Directory.CreateDirectory(Installer.DataDir);
        File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
    }
    catch (Exception)
    {
        // 日志写不了不影响安装
    }
}

const string Title = "FlyknitBuddy 安装程序";
var installer = new Installer(Say);
try
{
    if (uninstall)
    {
        // 卸载程序就在要删的目录里：先把自己拷到临时目录再从那里跑，不然删不掉自己
        var temp = Path.GetTempPath();
        if (!self.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            var copy = Path.Combine(temp, $"FlyknitUninstall-{Guid.NewGuid():N}.exe");
            File.Copy(self, copy);
            Process.Start(new ProcessStartInfo(copy, string.Join(' ', args)) { UseShellExecute = false });
            return 0;
        }
        if (!silent && Native.Ask(Title, "确定要卸载 FlyknitBuddy 和运维代理吗？\n\n员工的对话记录、记忆等个人数据保留在各自的 Windows 账号里，不会删除。") != true)
        {
            return 2;
        }
        installer.Uninstall();
        if (!silent)
        {
            Native.Info(Title, "FlyknitBuddy 已卸载。");
        }
        return 0;
    }

    Say($"FlyknitBuddy 安装程序 {typeof(Installer).Assembly.GetName().Version?.ToString(3)}，{Environment.MachineName}\\{Environment.UserName}");
    var exe = installer.Install(self);
    if (!silent)
    {
        // 员工端要以当前登录的员工身份打开，不能用安装程序的管理员身份。交给资源管理器打开就是员工自己的身份
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
        Native.Info(Title, "FlyknitBuddy 和运维代理已安装好。\n\n员工端已经打开，员工点「授权登录」就能用；以后的版本升级由运维代理自动完成。");
    }
    return 0;
}
catch (Exception ex)
{
    Say($"失败：{ex.Message}");
    if (ex is not SetupException)
    {
        Say(ex.ToString());
    }
    if (!silent)
    {
        Native.Error(Title, $"安装没有完成：{ex.Message}\n\n详细记录：{log}");
    }
    return 1;
}

internal static partial class Native
{
    private const uint Ok = 0x0, YesNo = 0x4, IconError = 0x10, IconQuestion = 0x20, IconInfo = 0x40, Topmost = 0x40000;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static void Info(string title, string text) => MessageBox(IntPtr.Zero, text, title, Ok | IconInfo | Topmost);

    public static void Error(string title, string text) => MessageBox(IntPtr.Zero, text, title, Ok | IconError | Topmost);

    public static bool Ask(string title, string text) => MessageBox(IntPtr.Zero, text, title, YesNo | IconQuestion | Topmost) == 6;
}
