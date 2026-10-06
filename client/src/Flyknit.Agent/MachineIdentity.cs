using Microsoft.Win32;

namespace Flyknit.Agent;

/// <summary>
/// 这台电脑的稳定标识。用注册表 HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid——
/// 它在装系统时生成，之后一直不变，重装代理也认得出是同一台机器，正好用来和后台的
/// 设备台账对上号（员工客户端上报同一个 GUID）。
/// </summary>
public static class MachineIdentity
{
    public static string MachineGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length >= 8)
            {
                return guid.Trim().ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn("读取 MachineGuid 失败，改用机器名", ex);
        }
        // 极少数情况下读不到注册表，退而用机器名，至少能注册上
        return ("name-" + Environment.MachineName).ToLowerInvariant();
    }

    public static string MachineName => SafeGet(() => Environment.MachineName);

    public static string OsVersion
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var product = key?.GetValue("ProductName") as string ?? "Windows";
                var display = key?.GetValue("DisplayVersion") as string ?? "";
                var build = key?.GetValue("CurrentBuild") as string ?? "";
                return string.Join(" ", new[] { product, display, build.Length > 0 ? $"(Build {build})" : "" }
                    .Where(s => s.Length > 0));
            }
            catch (Exception)
            {
                return Environment.OSVersion.VersionString;
            }
        }
    }

    private static string SafeGet(Func<string> get)
    {
        try { return get() ?? ""; }
        catch (Exception) { return ""; }
    }
}
