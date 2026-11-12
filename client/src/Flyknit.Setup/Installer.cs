using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Flyknit.Updater;
using Microsoft.Win32;

namespace Flyknit.Setup;

/// <summary>
/// 一次装好员工端和运维代理（IT 现场双击，弹一次 UAC 输管理员密码）：
///
/// - 员工端 → C:\Program Files\FlyknitBuddy（所有 Windows 用户都能用）；
/// - 运维代理 → C:\Program Files\FlyknitAgent，注册成 SYSTEM 身份的 Windows 服务；
/// - 代理用安装包里的安装凭证注册（和员工端登录同一张），不用再填注册密钥；
/// - 开始菜单、桌面快捷方式，「应用和功能」里能卸载；
/// - 员工账号写不了 Program Files，员工端以后的升级由代理（SYSTEM）替它装（见 flyknit.managed）。
///
/// 重复运行就是覆盖升级：员工端的本机数据在各自的 %APPDATA%，不受影响；代理沿用原来的身份。
/// </summary>
public sealed class Installer(Action<string> say)
{
    public const string ServiceName = "FlyknitAgent";
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FlyknitBuddy";
    /// <summary>员工端目录里的这个文件表示「这份程序由运维代理负责升级」。员工端看到它就不自己替换目录了。</summary>
    public const string ManagedMarker = "flyknit.managed";
    public const string ProvisionFile = "flyknit.provision.json";

    public static string ClientDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlyknitBuddy");
    public static string AgentDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlyknitAgent");
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Flyknit");
    public static string AgentConfig => Path.Combine(DataDir, "agent.json");

    // ---------- 安装 ----------

    /// <returns>员工端主程序的路径（装完要打开它）。</returns>
    public string Install(string selfPath)
    {
        // .NET 8 和员工端的下限是 Windows 10 1809；更老的系统装上也打不开，不如一开始就说清楚
        var os = Environment.OSVersion.Version;
        if (!RuntimeCheck.IsWindowsSupported(os))
        {
            throw new SetupException(
                $"这台电脑的系统版本是 {os}，FlyknitBuddy 需要 Windows 10 1809（内部版本 {RuntimeCheck.MinWindowsBuild}）或更新的 Windows 10 / 11。\n\n" +
                "不支持 Windows 7 / 8 / 8.1，请先升级系统。");
        }
        var temp = Path.Combine(Path.GetTempPath(), $"FlyknitSetup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            say("正在解压安装文件…");
            var zip = Path.Combine(temp, "payload.zip");
            if (!Payload.Extract(selfPath, zip))
            {
                // 员工端文件夹里这份是不带安装文件的外壳（装好后当卸载程序用），从解压出来的 zip 里点到它就会走到这里
                throw new SetupException(
                    "这是安装程序的外壳，不能单独安装（员工端文件夹里这份是装好后卸载用的）。\n\n" +
                    "请在管理后台「员工端版本」→「下载员工端安装包」，格式选「安装程序 .exe」，" +
                    "下载到的是一个文件 FlyknitBuddy-Setup-版本号.exe，直接双击它，不用解压。\n\n" +
                    "如果选了 .exe 下载到的却是 zip，说明服务端更新代码后还没重启，重启服务端再下载。");
            }
            var unpacked = Path.Combine(temp, "files");
            ZipFile.ExtractToDirectory(zip, unpacked);
            File.Delete(zip);
            var root = Payload.FindRoot(unpacked) ?? throw new SetupException("安装文件里找不到 FlyknitBuddy.exe，安装包可能损坏了，请重新下载。");
            var provision = ReadProvision(Path.Combine(root, ProvisionFile));

            // 员工端界面靠 WebView2：没装或太旧时，用包里带的离线安装包装上（没带就只提醒）。
            // 离线安装包不进 Program Files
            WebViewSetup.EnsureRuntime(root, say);
            WebViewSetup.RemoveRedist(root);

            say("正在关闭正在运行的 FlyknitBuddy…");
            StopProcesses("FlyknitBuddy", "FlyknitUpdater", "flyknit");
            StopAgent();

            var agentSource = Path.Combine(root, "agent", "FlyknitAgent.exe");
            var withAgent = File.Exists(agentSource);
            if (withAgent)
            {
                File.WriteAllText(Path.Combine(root, ManagedMarker), "运维代理负责这份程序的升级。\r\n");
            }

            say($"正在安装员工端到 {ClientDir}…");
            var backup = ClientDir + ".old";
            var result = Swap.Replace(root, ClientDir, backup);
            if (!result.Ok)
            {
                StartAgent();  // 别把原来在跑的代理留在停止状态
                throw new SetupException($"安装员工端失败：{result.Message}");
            }
            TryDelete(backup);

            if (withAgent)
            {
                say($"正在安装运维代理到 {AgentDir}…");
                Directory.CreateDirectory(AgentDir);
                File.Copy(agentSource, Path.Combine(AgentDir, "FlyknitAgent.exe"), overwrite: true);
                // 卸载程序放在代理目录（只有管理员能改）：卸载时要用管理员身份运行它
                File.Copy(StubFor(root, selfPath), Path.Combine(AgentDir, "FlyknitSetup.exe"), overwrite: true);
                WriteAgentConfig(provision);
                InstallService(Path.Combine(AgentDir, "FlyknitAgent.exe"));
            }
            else
            {
                say("安装包里没有运维代理，只安装员工端");
            }

            var exe = Path.Combine(ClientDir, "FlyknitBuddy.exe");
            say("正在创建快捷方式…");
            CreateShortcuts(exe);
            RegisterUninstall(exe, withAgent);
            if (File.Exists(Path.Combine(ClientDir, "flyknit.exe")))
            {
                // 命令行 flyknit：加进系统 PATH，在任何命令行窗口里都能直接敲 flyknit（新开的窗口才生效）
                AddToPath();
            }
            say("安装完成");
            return exe;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// 卸载程序用发布目录里那份不带安装文件的外壳（小）；老包里没有就用自己（带着安装文件，大一些但能用）。
    /// </summary>
    private static string StubFor(string root, string selfPath)
    {
        var stub = Path.Combine(root, "FlyknitSetup.exe");
        return File.Exists(stub) ? stub : selfPath;
    }

    private static JsonObject ReadProvision(string path)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj ? obj : new JsonObject();
        }
        catch (Exception)
        {
            return new JsonObject();
        }
    }

    /// <summary>
    /// 代理配置：服务器地址、安装凭证、员工端目录。原来装过代理且是同一台服务器的，沿用它的身份（agent_id、token），
    /// 后台里还是同一台；换了服务器就清掉身份重新注册。
    /// </summary>
    private void WriteAgentConfig(JsonObject provision)
    {
        var server = (string?)provision["server_url"] ?? "";
        var ticket = (string?)provision["ticket"] ?? "";
        if (server.Length == 0 || ticket.Length == 0)
        {
            say("警告：安装包里没有服务器地址或安装凭证，运维代理装上了但注册不了。请从管理后台重新下载安装包");
        }
        var config = AgentSettings.Merge(File.Exists(AgentConfig) ? File.ReadAllText(AgentConfig) : null, server, ticket, ClientDir);
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(AgentConfig, config, new UTF8Encoding(false));
        // 里面有凭证和代理令牌：只给 SYSTEM 和管理员
        Run("icacls.exe", $"\"{AgentConfig}\" /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F");
    }

    private void InstallService(string exe)
    {
        var exists = Run("sc.exe", $"query {ServiceName}").ExitCode == 0;
        var bin = $"binPath= \"\\\"{exe}\\\"\" start= auto obj= LocalSystem";
        var (code, output) = exists
            ? Run("sc.exe", $"config {ServiceName} {bin}")
            : Run("sc.exe", $"create {ServiceName} {bin} DisplayName= \"FlyknitBuddy 运维代理\"");
        if (code != 0)
        {
            throw new SetupException($"注册运维代理服务失败：{output.Trim()}");
        }
        Run("sc.exe", $"description {ServiceName} \"FlyknitBuddy IT 运维代理：采集电脑信息、安装软件、升级员工端。\"");
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        StartAgent();
    }

    private void StartAgent()
    {
        if (Run("sc.exe", $"query {ServiceName}").ExitCode == 0)
        {
            Run("sc.exe", $"start {ServiceName}");
        }
    }

    private void StopAgent()
    {
        if (Run("sc.exe", $"query {ServiceName}").ExitCode != 0)
        {
            return;
        }
        say("正在停止运维代理…");
        Run("sc.exe", $"stop {ServiceName}");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("FlyknitAgent").Length > 0)
        {
            Thread.Sleep(500);
        }
        // 代理自升级的更新助手（docs/agent-self-update.md）正好在跑的话也停掉，免得和这次安装同时改代理目录
        StopProcesses("FlyknitAgent", "FlyknitAgentUpdate");
    }

    private static void StopProcesses(params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(10_000);
                    }
                    catch (Exception)
                    {
                        // 已经退了，或者杀不掉；后面替换目录时会报出来
                    }
                }
            }
        }
    }

    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "FlyknitBuddy.lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "FlyknitBuddy.lnk");

    private void CreateShortcuts(string exe)
    {
        // 用系统自带的 WScript.Shell 建快捷方式：不用在程序里写 COM 接口
        var script = new StringBuilder("$s = New-Object -ComObject WScript.Shell;");
        foreach (var link in new[] { StartMenuLink, DesktopLink })
        {
            script.Append($"$l = $s.CreateShortcut('{Ps(link)}'); $l.TargetPath = '{Ps(exe)}'; $l.WorkingDirectory = '{Ps(ClientDir)}'; ");
            script.Append($"$l.Description = 'FlyknitBuddy 智能办公助手'; $l.Save();");
        }
        var (code, output) = Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"");
        if (code != 0)
        {
            say($"创建快捷方式失败（不影响使用，可以直接打开 {exe}）：{output.Trim()}");
        }
    }

    private static string Ps(string s) => s.Replace("'", "''");

    private static void RegisterUninstall(string exe, bool withAgent)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(UninstallKey);
        var version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] ?? "";
        // 卸载程序在代理目录（员工改不了）；没装代理的老包退回员工端目录里那份
        var uninstaller = withAgent ? Path.Combine(AgentDir, "FlyknitSetup.exe") : Path.Combine(ClientDir, "FlyknitSetup.exe");
        key.SetValue("DisplayName", "FlyknitBuddy 智能办公助手");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Flyknit");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", ClientDir);
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /S");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(DirectorySize(ClientDir) / 1024), RegistryValueKind.DWord);
    }

    // ---------- 卸载 ----------

    /// <summary>员工端在各用户的 HKCU 里写的资源管理器右键菜单（见 Flyknit.Core.Setup.ExplorerMenu）。</summary>
    internal static readonly string[] ExplorerMenuKeys =
    {
        @"Software\Classes\*\shell\FlyknitBuddy",
        @"Software\Classes\Directory\shell\FlyknitBuddy",
        @"Software\Classes\Directory\Background\shell\FlyknitBuddy",
    };

    /// <summary>
    /// 卸载时把右键菜单一起删掉，不然资源管理器里会留着点了没反应的菜单。
    /// 卸载程序以管理员身份运行，HKCU 未必是员工自己的，所以遍历 HKEY_USERS 里已加载的每个用户都删一遍
    /// （没登录的用户下次启动员工端前菜单还在，点了提示找不到程序，影响不大）。
    /// </summary>
    private void RemoveExplorerMenus()
    {
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (var key in ExplorerMenuKeys)
                {
                    try
                    {
                        Registry.Users.DeleteSubKeyTree($@"{sid}\{key}", throwOnMissingSubKey: false);
                    }
                    catch (Exception)
                    {
                        // 别的用户的配置单元没权限就算了
                    }
                }
            }
        }
        catch (Exception ex)
        {
            say($"清理右键菜单失败：{ex.Message}");
        }
    }

    public void Uninstall()
    {
        say("正在关闭 FlyknitBuddy…");
        StopProcesses("FlyknitBuddy", "FlyknitUpdater", "flyknit");
        RemoveFromPath();
        if (Run("sc.exe", $"query {ServiceName}").ExitCode == 0)
        {
            StopAgent();
            say("正在删除运维代理服务…");
            Run("sc.exe", $"delete {ServiceName}");
        }
        say("正在删除程序文件…");
        TryDelete(ClientDir);
        TryDelete(ClientDir + ".old");
        TryDelete(AgentDir);
        // 代理配置里有凭证和令牌，一起删；日志留着方便排查
        TryDeleteFile(AgentConfig);
        TryDelete(Path.Combine(DataDir, "client-updates"));
        TryDeleteFile(Path.Combine(DataDir, "client-update.json"));
        TryDeleteFile(StartMenuLink);
        TryDeleteFile(DesktopLink);
        RemoveExplorerMenus();
        try
        {
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            say($"清理卸载信息失败：{ex.Message}");
        }
        say("卸载完成。员工的对话记录、记忆等个人数据保留在各自的 Windows 账号里（%APPDATA%\\Flyknit）");
    }

    // ---------- 命令行 flyknit 的 PATH ----------

    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>把员工端目录加进系统 PATH（已经有了就不重复加）。读写原始值，不展开 %SystemRoot% 这类变量。</summary>
    private void AddToPath()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(EnvironmentKey, writable: true);
            if (key is null)
            {
                return;
            }
            var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var updated = PathList.Add(current, ClientDir);
            if (updated != current)
            {
                key.SetValue("Path", updated, RegistryValueKind.ExpandString);
                BroadcastEnvironmentChange();
                say("已把 flyknit 命令行加入系统 PATH（新开的命令行窗口生效）");
            }
        }
        catch (Exception ex)
        {
            say($"把 flyknit 加入 PATH 失败（不影响员工端）：{ex.Message}");
        }
    }

    private void RemoveFromPath()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(EnvironmentKey, writable: true);
            if (key is null)
            {
                return;
            }
            var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var updated = PathList.Remove(current, ClientDir);
            if (updated != current)
            {
                key.SetValue("Path", updated, RegistryValueKind.ExpandString);
                BroadcastEnvironmentChange();
            }
        }
        catch (Exception ex)
        {
            say($"从 PATH 里去掉 flyknit 失败：{ex.Message}");
        }
    }

    /// <summary>通知已经开着的程序（资源管理器等）环境变量变了，从它们新开的命令行窗口就能用。</summary>
    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
        }
        catch (Exception)
        {
            // 广播失败只是要重新登录才生效，不影响安装
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    // ---------- 工具 ----------

    private (int ExitCode, string Output) Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void TryDelete(string dir)
    {
        try
        {
            Swap.Delete(dir);
        }
        catch (Exception ex)
        {
            say($"删除 {dir} 失败：{ex.Message}");
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception)
        {
            // 没有就算了
        }
    }
}

public sealed class SetupException(string message) : Exception(message);
