using Flyknit.Agent;
using Flyknit.Setup;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>运维代理自升级 + 补 PATH（docs/agent-self-update.md）里不碰 Windows 的那部分判断。</summary>
public class AgentSelfUpdateTests
{
    // ---------- 版本比较：和服务端 services/versions.py 同一套规则 ----------

    [Theory]
    [InlineData("0.10.0", "0.9.0", 1)]          // 按数字比，不按字典序
    [InlineData("1.2", "1.2.0", 0)]             // 段数不同补零
    [InlineData("1.0.0", "1.0.0-rc1", 1)]       // 正式版比预发布版新
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", 1)]
    [InlineData("v0.3.0", "0.3.0", 0)]
    [InlineData("乱写的", "0.0.1", -1)]          // 认不出的当 0
    public void ComparesVersionsLikeTheServer(string left, string right, int sign)
    {
        Assert.Equal(sign, Math.Sign(SelfUpdatePolicy.Compare(left, right)));
    }

    [Theory]
    [InlineData("0.3.0", "0.3.1", true)]
    [InlineData("0.3.0", "0.3.0", false)]       // 相同不动
    [InlineData("0.3.0", "0.2.9", false)]       // 只升不降
    [InlineData("0.3.0", "", false)]
    [InlineData("0.3.0", null, false)]
    [InlineData("0.3.0", "不是版本", false)]     // 认不出的不换
    [InlineData("0.3.0", "0.4.0", false)]       // 0.4.0 以前换失败过，见 skipped
    [InlineData("0.3.0", "0.4.1", true)]        // 后来的版本照常换
    public void UpdatesOnlyToNewerVersionsThatHaventFailed(string current, string? candidate, bool expected)
    {
        Assert.Equal(expected, SelfUpdatePolicy.ShouldUpdate(current, candidate, new[] { "0.4.0" }));
    }

    [Fact]
    public void AVersionIsGivenUpOnlyAfterTwoFailures()
    {
        // 一次失败可能只是正好断网：再给一次机会
        Assert.Empty(SelfUpdatePolicy.GivenUp(new[] { "0.4.0" }));
        Assert.True(SelfUpdatePolicy.ShouldUpdate("0.3.0", "0.4.0", SelfUpdatePolicy.GivenUp(new[] { "0.4.0" })));
        // 两次都不行：不再试，但更新的版本照常
        var given = SelfUpdatePolicy.GivenUp(new[] { "0.4.0", " 0.4.0 ", "", "0.4.1" });
        Assert.Equal(new[] { "0.4.0" }, given);
        Assert.False(SelfUpdatePolicy.ShouldUpdate("0.3.0", "0.4.0", given));
        Assert.True(SelfUpdatePolicy.ShouldUpdate("0.3.0", "0.4.1", given));
    }

    [Fact]
    public void ProductVersionCommitHashIsIgnored()
    {
        Assert.Equal("0.3.1", SelfUpdatePolicy.CleanVersion("0.3.1+a0dc5b8ac23f"));
        Assert.Equal("", SelfUpdatePolicy.CleanVersion(null));
    }

    [Fact]
    public void HelperWaitsThenKeepsOrRollsBack()
    {
        Assert.Equal(SelfUpdatePolicy.HealthOutcome.Waiting, SelfUpdatePolicy.CheckHealth(false, TimeSpan.FromSeconds(30)));
        Assert.Equal(SelfUpdatePolicy.HealthOutcome.Healthy, SelfUpdatePolicy.CheckHealth(true, TimeSpan.FromSeconds(30)));
        // 超时也看标记：最后一刻连上了就不回滚
        Assert.Equal(SelfUpdatePolicy.HealthOutcome.Healthy, SelfUpdatePolicy.CheckHealth(true, SelfUpdatePolicy.HealthTimeout));
        Assert.Equal(SelfUpdatePolicy.HealthOutcome.RollBack, SelfUpdatePolicy.CheckHealth(false, SelfUpdatePolicy.HealthTimeout));
    }

    // ---------- 安装程序参数：搭桥 ----------

    [Fact]
    public void DeferredInstallNeedsSilentMode()
    {
        var ok = SetupArgs.Parse(new[] { "/S", "/deferred" });
        Assert.True(ok.Silent && ok.Deferred && ok.Error is null);

        Assert.NotNull(SetupArgs.Parse(new[] { "/deferred" }).Error);               // 代理那边没人能点对话框
        Assert.NotNull(SetupArgs.Parse(new[] { "/S", "/deferred", "/uninstall" }).Error);
    }

    [Theory]
    [InlineData(new[] { "/S", "/deferred-run", "4321" }, 4321, false)]
    [InlineData(new[] { "/quiet", "/DEFERRED-RUN", "88" }, 88, false)]
    [InlineData(new[] { "/S", "/deferred-run" }, null, true)]         // 没给进程 ID
    [InlineData(new[] { "/S", "/deferred-run", "abc" }, null, true)]
    [InlineData(new[] { "/S", "/deferred-run", "-5" }, null, true)]
    [InlineData(new[] { "/deferred-run", "4321" }, 4321, true)]       // 没有 /S
    public void DeferredRunCarriesTheParentProcess(string[] args, int? pid, bool error)
    {
        var parsed = SetupArgs.Parse(args);
        Assert.Equal(error, parsed.Error is not null);
        if (!error)
        {
            Assert.Equal(pid, parsed.DeferredRunAfter);
        }
    }

    [Fact]
    public void PlainInvocationsAreUnchanged()
    {
        var click = SetupArgs.Parse(Array.Empty<string>());
        Assert.False(click.Silent || click.Uninstall || click.Deferred);
        Assert.Null(click.DeferredRunAfter);
        Assert.Null(click.Error);
        Assert.True(SetupArgs.Parse(new[] { "/uninstall", "/S" }) is { Uninstall: true, Silent: true, Error: null });
    }

    // ---------- 补 PATH ----------

    private const string Dir = @"C:\Program Files\FlyknitBuddy";

    [Fact]
    public void AddsClientDirOnce()
    {
        var path = @"%SystemRoot%\system32;%SystemRoot%;C:\Tools";
        var added = PathList.Add(path, Dir);
        Assert.Equal(path + ";" + Dir, added);
        // 已经有了（大小写、结尾斜杠不同也算）：原样返回，不重复写注册表
        Assert.Same(added, PathList.Add(added, Dir));
        Assert.Equal(path + @";c:\program files\flyknitbuddy\", PathList.Add(path + @";c:\program files\flyknitbuddy\", Dir));
    }

    [Fact]
    public void LeavesOtherEntriesAlone()
    {
        var path = @"C:\A;;C:\B; C:\Program Files\Other ";
        var updated = PathList.Add(path, Dir);
        // 只在末尾追加这一个目录；别的项内容不变（空项、首尾空格会被规整掉）
        Assert.Equal(@"C:\A;C:\B;C:\Program Files\Other;" + Dir, updated);
        Assert.Equal(@"C:\A;C:\B;C:\Program Files\Other", PathList.Remove(updated, Dir));
    }
}
