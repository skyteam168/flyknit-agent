using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace Flyknit.Core.Gateway;

/// <summary>
/// 这台机器的身份信息，随请求上报给服务端做资产台账。
///
/// 注册时采一次是不够的——IP 跟着 DHCP 租约变，用户换人登录，客户端会升级。
/// 所以这些字段每次拉配置时都重新采，服务端按最新一次覆盖。
/// </summary>
public sealed class MachineInfo
{
    [JsonPropertyName("machine_name")] public string MachineName { get; set; } = "";
    [JsonPropertyName("user_name")] public string UserName { get; set; } = "";
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("os_version")] public string OsVersion { get; set; } = "";
    [JsonPropertyName("client_version")] public string ClientVersion { get; set; } = "";
    [JsonPropertyName("ui_language")] public string UiLanguage { get; set; } = "";

    /// <summary>本机的内网地址。多网卡很常见（有线 + 无线），所以是列表。</summary>
    [JsonPropertyName("ip_addresses")] public List<string> IpAddresses { get; set; } = new();

    /// <summary>主网卡的 MAC。比 IP 稳定，适合当资产台账的主键。</summary>
    [JsonPropertyName("mac_address")] public string MacAddress { get; set; } = "";

    /// <summary>
    /// 这台电脑的系统标识（HKLM MachineGuid）。运维代理用同一个值注册，后台靠它
    /// 把员工客户端和运维代理关联到同一台电脑上。
    /// </summary>
    [JsonPropertyName("machine_guid")] public string MachineGuid { get; set; } = "";

    public static MachineInfo Collect(string clientVersion, string uiLanguage)
    {
        var info = new MachineInfo
        {
            MachineName = Safe(() => Environment.MachineName),
            UserName = Safe(() => Environment.UserName),
            Domain = Safe(() => Environment.UserDomainName),
            OsVersion = Safe(() => Environment.OSVersion.VersionString),
            ClientVersion = clientVersion ?? "",
            UiLanguage = uiLanguage ?? "",
            MachineGuid = ReadMachineGuid(),
        };
        var (ips, mac) = Network();
        info.IpAddresses = ips;
        info.MacAddress = mac;
        return info;
    }

    /// <summary>
    /// 枚举网卡拿内网地址和 MAC。只要正在工作的以太网和 Wi-Fi，
    /// 回环、隧道、以及 Hyper-V / VPN / VMware 造出来的虚拟网卡都排除掉，
    /// 否则一台装了 Docker 的机器会报出一串没人认识的 172.x 地址。
    /// </summary>
    public static (List<string> Ips, string Mac) Network()
    {
        var ips = new List<string>();
        var mac = "";
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsPhysical(nic))
                {
                    continue;
                }
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue; // 只要 IPv4，工厂网络里 IPv6 地址对定位机器没帮助
                    }
                    var text = addr.Address.ToString();
                    if (IPAddress.IsLoopback(addr.Address) || text.StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        continue; // 169.254 是 DHCP 没拿到地址时的自分配，报上去只会误导
                    }
                    if (!ips.Contains(text))
                    {
                        ips.Add(text);
                    }
                }
                if (mac.Length == 0 && ips.Count > 0)
                {
                    mac = Format(nic.GetPhysicalAddress());
                }
            }
        }
        catch (Exception)
        {
            // 网卡枚举在某些受限环境下会抛，报不上来就报不上来，不能影响正常使用
        }
        return (ips, mac);
    }

    private static bool IsPhysical(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }
        var name = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return !VirtualHints.Any(h => name.Contains(h, StringComparison.Ordinal));
    }

    private static readonly string[] VirtualHints =
    {
        "virtual", "vmware", "vbox", "hyper-v", "loopback", "pseudo", "tap-", "tunnel", "docker", "wsl",
    };

    private static string Format(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "" : string.Join("-", bytes.Select(b => b.ToString("X2")));
    }

    private static string Safe(Func<string> get)
    {
        try { return get() ?? ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// 读系统标识 HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid。它在装系统时生成、
    /// 之后不变，和运维代理用的是同一个值，后台据此把员工端和代理对到同一台电脑。
    /// </summary>
    /// <summary>注册时也报上系统标识：同一台电脑上同一个员工重新登录，服务端沿用原来那条设备记录。</summary>
    public static string SystemGuid() => ReadMachineGuid();

    private static string ReadMachineGuid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "";
        }
        try
        {
            using var key = Microsoft.Win32.RegistryKey
                .OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length >= 8)
            {
                return guid.Trim().ToLowerInvariant();
            }
        }
        catch (Exception)
        {
            // 读不到就空着，不影响其它信息上报
        }
        return "";
    }
}
