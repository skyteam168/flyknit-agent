using System.Text.Json;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 安全中心里的开关，必须真的管住事。
///
/// 这组测试是补一个已经发生过的问题：system_tools、sandbox、command_policy 三项
/// 服务端下发了、界面画出来了、客户端常量也定义了，但**没有任何代码读过它们**。
/// 后台显示「系统级工具：已禁用」，而 wmic 和 schtasks /create 照跑。
/// 一个显示为关、实际是开的安全开关，比没有这个开关更糟——它让人据此做判断。
///
/// 所以每加一个开关，这里就要有一条「关着时真的做不到」的测试。
/// </summary>
public class SecuritySwitchTests
{
    private static ToolContext Context(
        PermissionMode permission = PermissionMode.Workspace,
        bool systemTools = false,
        bool sandbox = true,
        string? workspace = null)
    {
        var security = new SecuritySettings(new Dictionary<string, SecurityItem>
        {
            [SecuritySettings.SystemTools] = new() { Value = systemTools, Locked = true },
            [SecuritySettings.Sandbox] = new() { Value = sandbox, Locked = true },
            [SecuritySettings.NetworkAllowlist] = new() { Value = false, Locked = true },
        });
        return new ToolContext
        {
            Policy = CommandPolicy.Default(),
            ConversationId = "c1",
            Workspace = workspace ?? Directory.CreateTempSubdirectory("flyknit-switch").FullName,
            Permission = permission,
            Security = security,
        };
    }

    private static JsonElement Args(string command) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { command })).RootElement.Clone();

    // ---------- 系统级工具 ----------

    [Theory]
    // cmd 那套
    [InlineData("reg add HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v x /d evil.exe /f")]
    [InlineData("reg delete HKCU\\Software\\Test /f")]
    [InlineData("regedit /s payload.reg")]
    [InlineData("sc config windefend start= disabled")]
    [InlineData("sc create backdoor binPath= C:\\evil.exe")]
    [InlineData("net stop windefend")]
    [InlineData("schtasks /create /tn backdoor /tr evil.exe /sc onlogon")]
    [InlineData("wmic process call create calc.exe")]
    // PowerShell 那套干的是同一件事，只拦前者等于没拦
    [InlineData("Set-ItemProperty -Path 'HKLM:\\Software\\Test' -Name x -Value 1")]
    [InlineData("New-Item -Path HKCU:\\Software\\Evil")]
    [InlineData("Set-Service -Name windefend -StartupType Disabled")]
    [InlineData("Stop-Service windefend")]
    [InlineData("Register-ScheduledTask -TaskName backdoor -Action $a")]
    [InlineData("Invoke-CimMethod -ClassName Win32_Process -MethodName Create")]
    public void SystemToolsOffBlocksThem(string command)
    {
        var d = new RunShellTool().Assess(Args(command), Context(systemTools: false));
        Assert.Equal(RiskLevel.Blocked, d.Level);
        Assert.Contains("系统级工具", d.Reason);
    }

    [Theory]
    [InlineData("reg query HKLM\\Software\\Microsoft\\Windows\\CurrentVersion")]
    [InlineData("sc query windefend")]
    [InlineData("Get-Service windefend")]
    [InlineData("Get-CimInstance Win32_OperatingSystem")]
    [InlineData("dir C:\\Windows")]
    [InlineData("python regedit_notes.py")]
    [InlineData("copy sc.txt backup\\sc.txt")]
    public void LookingIsStillFine(string command)
    {
        // 查询不改东西，没必要连看一眼都拦——拦了只会逼人去用别的办法
        var d = new RunShellTool().Assess(Args(command), Context(systemTools: false));
        Assert.NotEqual(RiskLevel.Blocked, d.Level);
    }

    [Fact]
    public void SystemToolsOnLetsThemThroughToTheNormalRules()
    {
        // 打开不等于放行：命令回到原来的分级里，该确认还是要确认
        var d = new RunShellTool().Assess(Args("schtasks /create /tn 备份 /tr backup.bat /sc daily"),
                                          Context(systemTools: true));
        Assert.NotEqual(RiskLevel.Blocked, d.Level);
    }

    [Theory]
    [InlineData("sc config windefend start= disabled")]
    [InlineData("reg add HKLM\\Software\\Test /v x /d 1 /f")]
    [InlineData("Set-Service -Name windefend -StartupType Disabled")]
    public void TheBaselineRulesStillBlockTheWorstOnesEvenWithTheSwitchOn(string command)
    {
        // 这几条在 default-policy.json 里是硬拦的。开关是额外一层，不是总开关——
        // 打开它不该把基线规则一起打开
        var d = new RunShellTool().Assess(Args(command), Context(systemTools: true));
        Assert.Equal(RiskLevel.Blocked, d.Level);
    }

    [Fact]
    public void OpenAppCannotGoAroundTheSwitch()
    {
        // run_shell 拦住了但 open_app 放行，等于没拦
        Assert.NotNull(SystemToolPolicy.Evaluate("regedit /s payload.reg", allowed: false));
        Assert.Null(SystemToolPolicy.Evaluate("regedit /s payload.reg", allowed: true));
    }

    // ---------- 工作区隔离 ----------

    [Fact]
    public void SandboxCapsFullPermission()
    {
        // 隔离开着时，员工在输入框里选「完全权限」也越不过工作区
        Assert.Equal(PermissionMode.Workspace,
            Context(PermissionMode.Full, sandbox: true).EffectivePermission);
        Assert.Equal(PermissionMode.Full,
            Context(PermissionMode.Full, sandbox: false).EffectivePermission);
    }

    [Fact]
    public void SandboxDoesNotLoosenTheOtherModes()
    {
        // 这一档只会更严。关掉隔离不会把「仅可查看」变成能写
        Assert.Equal(PermissionMode.ReadOnly,
            Context(PermissionMode.ReadOnly, sandbox: false).EffectivePermission);
        Assert.Equal(PermissionMode.Workspace,
            Context(PermissionMode.Workspace, sandbox: false).EffectivePermission);
    }

    [Fact]
    public void WritingOutsideTheWorkspaceIsBlockedEvenWithFullPermission()
    {
        var ctx = Context(PermissionMode.Full, sandbox: true);
        var outside = Path.Combine(Path.GetTempPath(), "flyknit-outside", "report.xlsx");
        Assert.Equal(RiskLevel.Blocked, ctx.AssessWrite(outside).Level);

        // 给这台机器关掉隔离之后才可以
        var opened = Context(PermissionMode.Full, sandbox: false);
        Assert.NotEqual(RiskLevel.Blocked, opened.AssessWrite(outside).Level);
    }

    [Fact]
    public void CommandsStillRunOutsideOnlyWhenTheSandboxIsOff()
    {
        var ctx = Context(PermissionMode.Full, sandbox: true);
        var d = new RunShellTool().Assess(Args("del D:\\数据\\旧报表.xlsx"), ctx);
        Assert.NotEqual(RiskLevel.Auto, d.Level);
    }

    // ---------- 开关不应该悄悄消失 ----------

    [Fact]
    public void EverySwitchWeShipIsActuallyRead()
    {
        // 这条是给以后的人看的：在 SecuritySettings 里加一个常量，就要有代码读它。
        // 下面这几个都有对应的使用点；加新的请一并加测试，别让它变成画在界面上的摆设。
        var wired = new[]
        {
            SecuritySettings.Sandbox,
            SecuritySettings.SystemTools,
            SecuritySettings.NetworkAllowlist,
            SecuritySettings.DeleteProtection,
            SecuritySettings.AutoBackup,
            SecuritySettings.BackupQuotaMb,
            SecuritySettings.BatchDeleteThreshold,
            SecuritySettings.Notifications,
            SecuritySettings.NotificationSound,
            // 学习策略：LearningPolicy.From 读，复盘和 memory_write 据此跳过（见 MemoryPhase3Tests）
            SecuritySettings.LearnPreferences,
            SecuritySettings.LearnFacts,
            SecuritySettings.LearnExperience,
            SecuritySettings.LearnEpisodes,
            SecuritySettings.LearnSkills,
        };
        var declared = typeof(SecuritySettings)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(declared.OrderBy(x => x, StringComparer.Ordinal),
                     wired.OrderBy(x => x, StringComparer.Ordinal));
    }
}
