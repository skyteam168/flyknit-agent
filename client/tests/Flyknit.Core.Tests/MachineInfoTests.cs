using System.Linq;
using Flyknit.Core.Gateway;
using Xunit;

namespace Flyknit.Core.Tests;

public class MachineInfoTests
{
    [Fact]
    public void CollectsTheBasicsWithoutThrowing()
    {
        var info = MachineInfo.Collect("0.9.0", "vi-VN");

        Assert.Equal("0.9.0", info.ClientVersion);
        Assert.Equal("vi-VN", info.UiLanguage);
        Assert.NotNull(info.IpAddresses);
        // 机器名在任何环境下都该拿得到；拿不到也只是空串，不能抛
        Assert.NotNull(info.MachineName);
    }

    [Fact]
    public void NullArgumentsBecomeEmptyNotNull()
    {
        var info = MachineInfo.Collect(null!, null!);

        Assert.Equal("", info.ClientVersion);
        Assert.Equal("", info.UiLanguage);
    }

    [Fact]
    public void NeverReportsLoopbackOrSelfAssignedAddresses()
    {
        var (ips, _) = MachineInfo.Network();

        // 127.x 和 169.254.x 报上去只会误导——后者是 DHCP 没拿到地址时的自分配
        Assert.DoesNotContain(ips, ip => ip.StartsWith("127."));
        Assert.DoesNotContain(ips, ip => ip.StartsWith("169.254."));
    }

    [Fact]
    public void AddressesAreUniqueAndLookLikeIpv4()
    {
        var (ips, _) = MachineInfo.Network();

        Assert.Equal(ips.Count, ips.Distinct().Count());
        foreach (var ip in ips)
        {
            var parts = ip.Split('.');
            Assert.Equal(4, parts.Length);
            Assert.All(parts, p => Assert.True(byte.TryParse(p, out _), $"不是 IPv4：{ip}"));
        }
    }

    [Fact]
    public void MacIsEmptyOrCanonicallyFormatted()
    {
        var (_, mac) = MachineInfo.Network();

        if (mac.Length > 0)
        {
            // AA-BB-CC-DD-EE-FF：大写、连字符分隔，后台按它做台账主键
            Assert.Matches("^([0-9A-F]{2}-)+[0-9A-F]{2}$", mac);
        }
    }
}
