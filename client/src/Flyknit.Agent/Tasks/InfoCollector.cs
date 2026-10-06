using System.Management;
using Microsoft.Win32;

namespace Flyknit.Agent.Tasks;

/// <summary>
/// 采集电脑信息（collect_info）：硬件、系统、网络、已装软件、补丁、启动项。
/// 结果以字典回传，后台存进这台代理的台账（inventory）。
/// </summary>
public static class InfoCollector
{
    public static Task<RunResult> RunAsync(CancellationToken ct)
    {
        var inventory = new Dictionary<string, object?>
        {
            ["collected_at"] = DateTimeOffset.Now.ToString("o"),
            ["machine_name"] = MachineIdentity.MachineName,
            ["machine_guid"] = MachineIdentity.MachineGuid(),
            ["os"] = Os(),
            ["cpu"] = Cpu(),
            ["memory"] = Memory(),
            ["disks"] = Disks(),
            ["gpu"] = Gpu(),
            ["baseboard"] = Baseboard(),
            ["network"] = Network(),
            ["software"] = InstalledSoftware(),
            ["hotfixes"] = Hotfixes(),
            ["startup"] = StartupItems(),
        };
        var count = (inventory["software"] as List<object>)?.Count ?? 0;
        return Task.FromResult(RunResult.Ok($"采集完成，已装软件 {count} 项。", inventory: inventory));
    }

    private static Dictionary<string, object?> Os()
    {
        var row = QueryOne("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, TotalVisibleMemorySize FROM Win32_OperatingSystem");
        return new()
        {
            ["name"] = MachineIdentity.OsVersion,
            ["caption"] = Str(row, "Caption"),
            ["version"] = Str(row, "Version"),
            ["build"] = Str(row, "BuildNumber"),
            ["architecture"] = Str(row, "OSArchitecture"),
            ["installed_at"] = Wmi(row, "InstallDate"),
            ["last_boot"] = Wmi(row, "LastBootUpTime"),
            ["user"] = SafeUser(),
        };
    }

    private static Dictionary<string, object?> Cpu()
    {
        var row = QueryOne("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        return new()
        {
            ["name"] = Str(row, "Name")?.Trim(),
            ["cores"] = Int(row, "NumberOfCores"),
            ["threads"] = Int(row, "NumberOfLogicalProcessors"),
            ["max_mhz"] = Int(row, "MaxClockSpeed"),
        };
    }

    private static Dictionary<string, object?> Memory()
    {
        long total = 0;
        var modules = new List<object>();
        foreach (var m in Query("SELECT Capacity, Speed, Manufacturer FROM Win32_PhysicalMemory"))
        {
            var cap = Long(m, "Capacity");
            total += cap;
            modules.Add(new Dictionary<string, object?>
            {
                ["size_gb"] = Math.Round(cap / 1024.0 / 1024 / 1024, 1),
                ["speed_mhz"] = Int(m, "Speed"),
                ["vendor"] = Str(m, "Manufacturer")?.Trim(),
            });
        }
        return new()
        {
            ["total_gb"] = Math.Round(total / 1024.0 / 1024 / 1024, 1),
            ["modules"] = modules,
        };
    }

    private static List<object> Disks()
    {
        var list = new List<object>();
        foreach (var d in Query("SELECT Model, Size, MediaType FROM Win32_DiskDrive"))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["model"] = Str(d, "Model")?.Trim(),
                ["size_gb"] = Math.Round(Long(d, "Size") / 1024.0 / 1024 / 1024, 1),
            });
        }
        foreach (var v in Query("SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["drive"] = Str(v, "DeviceID"),
                ["label"] = Str(v, "VolumeName"),
                ["size_gb"] = Math.Round(Long(v, "Size") / 1024.0 / 1024 / 1024, 1),
                ["free_gb"] = Math.Round(Long(v, "FreeSpace") / 1024.0 / 1024 / 1024, 1),
            });
        }
        return list;
    }

    private static List<object> Gpu()
    {
        var list = new List<object>();
        foreach (var g in Query("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController"))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = Str(g, "Name")?.Trim(),
                ["driver"] = Str(g, "DriverVersion"),
            });
        }
        return list;
    }

    private static Dictionary<string, object?> Baseboard()
    {
        var sys = QueryOne("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
        var bios = QueryOne("SELECT SerialNumber FROM Win32_BIOS");
        return new()
        {
            ["manufacturer"] = Str(sys, "Manufacturer")?.Trim(),
            ["model"] = Str(sys, "Model")?.Trim(),
            ["serial"] = Str(bios, "SerialNumber")?.Trim(),
        };
    }

    private static List<object> Network()
    {
        var list = new List<object>();
        foreach (var n in Query("SELECT Description, MACAddress, IPAddress, DefaultIPGateway FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=TRUE"))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["adapter"] = Str(n, "Description"),
                ["mac"] = Str(n, "MACAddress"),
                ["ips"] = (n?["IPAddress"] as string[])?.Where(ip => !ip.Contains(':')).ToArray() ?? Array.Empty<string>(),
                ["gateway"] = (n?["DefaultIPGateway"] as string[])?.FirstOrDefault(),
            });
        }
        return list;
    }

    /// <summary>从卸载注册表读已装软件，三个分支都扫（64 位、32 位、当前用户）。</summary>
    private static List<object> InstalledSoftware()
    {
        var seen = new HashSet<string>();
        var list = new List<object>();
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(sub);
                    var name = app?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name) || app?.GetValue("SystemComponent") as int? == 1)
                    {
                        continue;
                    }
                    var version = app?.GetValue("DisplayVersion") as string ?? "";
                    if (!seen.Add(name + "|" + version))
                    {
                        continue;
                    }
                    list.Add(new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["version"] = version,
                        ["publisher"] = app?.GetValue("Publisher") as string ?? "",
                        ["installed_on"] = app?.GetValue("InstallDate") as string ?? "",
                    });
                }
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"读取卸载注册表 {path} 失败", ex);
            }
        }
        return list.OrderBy(x => ((Dictionary<string, object?>)x)["name"] as string).ToList();
    }

    private static List<object> Hotfixes()
    {
        var list = new List<object>();
        foreach (var h in Query("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering"))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = Str(h, "HotFixID"),
                ["installed_on"] = Str(h, "InstalledOn"),
            });
        }
        return list;
    }

    private static List<object> StartupItems()
    {
        var list = new List<object>();
        foreach (var s in Query("SELECT Name, Command, Location FROM Win32_StartupCommand"))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = Str(s, "Name"),
                ["command"] = Str(s, "Command"),
                ["location"] = Str(s, "Location"),
            });
        }
        return list;
    }

    // ---------- WMI 辅助 ----------

    private static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        List<ManagementBaseObject> rows = new();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", wql);
            foreach (var o in searcher.Get())
            {
                rows.Add(o);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"WMI 查询失败：{wql}", ex);
        }
        return rows;
    }

    private static ManagementBaseObject? QueryOne(string wql) => Query(wql).FirstOrDefault();

    private static string? Str(ManagementBaseObject? row, string name)
    {
        try { return row?[name]?.ToString(); }
        catch (Exception) { return null; }
    }

    private static int Int(ManagementBaseObject? row, string name) =>
        int.TryParse(Str(row, name), out var v) ? v : 0;

    private static long Long(ManagementBaseObject? row, string name) =>
        long.TryParse(Str(row, name), out var v) ? v : 0;

    private static string? Wmi(ManagementBaseObject? row, string name)
    {
        var raw = Str(row, name);
        if (string.IsNullOrEmpty(raw)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(raw).ToString("o"); }
        catch (Exception) { return raw; }
    }

    private static string SafeUser()
    {
        // 当前登录的交互用户（代理是 SYSTEM，Environment.UserName 会是 SYSTEM）
        var row = QueryOne("SELECT UserName FROM Win32_ComputerSystem");
        return Str(row, "UserName") ?? "";
    }
}
