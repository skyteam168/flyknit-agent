using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Flyknit.Setup;

// FlyknitBuddy 安装程序。
//   双击：安装（员工端 + 运维代理），装完打开员工端
//   /S：静默安装（组策略启动脚本、SCCM 用），不弹窗、不打开员工端，结果看退出码和日志
//   /uninstall [/S]：卸载（「应用和功能」里点卸载走这里）
//   /S /deferred：给旧运维代理「搭桥」（见 SetupArgs）
Console.OutputEncoding = Encoding.UTF8;
var parsed = SetupArgs.Parse(args);
var silent = parsed.Silent;
var uninstall = parsed.Uninstall;
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
if (parsed.Error is { } argError)
{
    Say($"参数不对：{argError}");
    return 2;
}
try
{
    if (parsed.Deferred)
    {
        // 被运维代理以「安装软件」任务启动：这里直接装的话，一停代理就会被代理连带结束。
        // 复制一份自己到只有管理员能写的代理目录（不放 Windows\Temp：普通用户能往那里放文件），启动副本，马上退出，
        // 代理把这个任务回报成功；副本等这个进程退出、再等代理回报完，才开始真正安装
        var dir = Path.Combine(Installer.AgentDir, "setup-deferred");
        if (Directory.Exists(dir))
        {
            foreach (var old in Directory.GetFiles(dir))
            {
                try { File.Delete(old); } catch (Exception) { /* 上次的副本还在跑 */ }
            }
        }
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, $"FlyknitSetup-{Guid.NewGuid():N}.exe");
        File.Copy(self, copy);
        // 用 ShellExecute 启动：不继承这个进程的句柄。代理是通过管道读安装程序输出的，副本要是拿着管道，
        // 代理会一直等它结束、回报不了任务
        Process.Start(new ProcessStartInfo(copy, $"/S {SetupArgs.DeferredRunFlag} {Environment.ProcessId}")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = dir,
        });
        Say("已安排安装：运维代理回报完这个任务后自动开始，大约 1～3 分钟装完。结果看这台电脑的 setup.log，或后台里它的代理版本");
        return 0;
    }
    if (parsed.DeferredRunAfter is { } parentPid)
    {
        Say($"搭桥安装：等进程 {parentPid} 退出");
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit(10 * 60 * 1000);
        }
        catch (ArgumentException)
        {
            // 已经退出了
        }
        Thread.Sleep(TimeSpan.FromSeconds(15)); // 让代理把任务结果回报给服务端，再停它
    }
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
