using System;
using System.IO;
using Flyknit.Core.Security;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 递归强制删除按**目标**判，不按动词判。
/// rm -rf node_modules 和 rm -rf C:\ 不该是同一个待遇。
/// </summary>
public class RecursiveDeleteTests : IDisposable
{
    private static readonly CommandPolicy Policy = new(PolicyConfig.LoadDefault());

    private readonly string _root = Path.Combine(Path.GetTempPath(), "flyknit-rm-" + Guid.NewGuid().ToString("N")[..8]);

    public RecursiveDeleteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* 清不掉算了 */ }
    }

    // ---------- 普通目录：确认一次就能做 ----------

    [Theory]
    [InlineData("rm -rf node_modules")]
    [InlineData("rm -rf ./build")]
    [InlineData("rm -rf dist")]
    [InlineData("rd /s /q obj")]
    [InlineData("Remove-Item -Recurse -Force .\\bin")]
    public void OrdinaryTargetsOnlyNeedConfirming(string command)
    {
        var d = Policy.EvaluateCommand(command, _root);

        Assert.Equal(RiskLevel.Confirm, d.Level);
        Assert.Equal(CommandEffect.Destructive, d.Effect);
    }

    [Fact]
    public void ADeepProjectPathUnderTheUserFolderIsFine()
    {
        var d = Policy.EvaluateCommand(@"rm -rf C:\Users\nguyen\project\build", _root);

        Assert.Equal(RiskLevel.Confirm, d.Level);
    }

    // ---------- 系统位置：继续硬拦 ----------

    [Theory]
    [InlineData(@"rm -rf C:\")]
    [InlineData(@"rm -fr C:\Users")]
    [InlineData(@"rm -rf C:\Windows")]
    [InlineData(@"rd /s /q C:\Program Files")]
    [InlineData(@"Remove-Item -Recurse -Force C:\ProgramData")]
    [InlineData(@"rm -rf C:\Windows\System32")]
    public void SystemLocationsStayBlocked(string command)
    {
        var d = Policy.EvaluateCommand(command, _root);

        Assert.Equal(RiskLevel.Blocked, d.Level);
    }

    [Fact]
    public void TheReasonNamesTheTargetAndWhy()
    {
        var d = Policy.EvaluateCommand(@"rm -rf C:\Users", _root);

        Assert.Contains(@"C:\Users", d.Reason);
        Assert.Contains("系统关键目录", d.Reason);
    }

    // ---------- 判断不了的，一律拦 ----------

    [Theory]
    [InlineData("rm -rf $TARGET")]
    [InlineData("rm -rf ${DIR}/sub")]
    [InlineData("rm -rf %BUILD_DIR%")]
    [InlineData("rm -rf $(cat list.txt)")]
    [InlineData("rd /s /q *")]
    [InlineData("rm -rf ./out/*")]
    public void UnresolvableTargetsAreBlocked(string command)
    {
        var d = Policy.EvaluateCommand(command, _root);

        Assert.Equal(RiskLevel.Blocked, d.Level);
        // 要说清楚是「说不准」而不是「这条命令本身坏」，否则用户不知道怎么改
        Assert.Contains("运行时", d.Reason);
    }

    [Fact]
    public void ARecursiveDeleteWithNoTargetAtAllIsBlocked()
    {
        var d = Policy.EvaluateCommand("rm -rf", _root);

        Assert.Equal(RiskLevel.Blocked, d.Level);
    }

    // ---------- 相对路径逃逸 ----------

    [Fact]
    public void DotDotEscapesAreJudgedByWhereTheyLand()
    {
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);

        // ..\.. 走出去落在普通目录上：确认即可
        var ordinary = Policy.EvaluateCommand($"rm -rf ..{Path.DirectorySeparatorChar}sibling", workspace);
        Assert.Equal(RiskLevel.Confirm, ordinary.Level);

        // 但落在系统目录上要拦住——靠的是解析后的位置，不是字面量
        var escaped = Policy.EvaluateCommand(@"rm -rf C:\ws\..\..\Windows", workspace);
        Assert.Equal(RiskLevel.Blocked, escaped.Level);
    }

    // ---------- 和脚本的关系 ----------

    [Fact]
    public void PuttingItInAScriptDoesNotGetAroundIt()
    {
        var script = Path.Combine(_root, "clean.ps1");
        File.WriteAllText(script, "Write-Host start\nrm -rf $env:TARGET\n");

        var d = Policy.EvaluateCommand($"powershell -File \"{script}\"", _root);

        Assert.Equal(RiskLevel.Blocked, d.Level);
        Assert.Contains("第 2 行", d.Reason);
    }

    // ---------- 一直都该拦的那些，没被这次改动放松 ----------

    [Theory]
    [InlineData("format C: /q")]
    [InlineData("diskpart")]
    [InlineData("vssadmin delete shadows /all")]
    [InlineData("bcdedit /set testsigning on")]
    [InlineData("netsh advfirewall set allprofiles state off")]
    public void CommandsWithNoLegitimateTargetAreStillBlocked(string command)
    {
        Assert.Equal(RiskLevel.Blocked, Policy.EvaluateCommand(command, _root).Level);
    }

    [Fact]
    public void ADestructiveCommandStillCannotBecomeARememberedRule()
    {
        // 否则点一次「以后自动执行」就等于永久放行 rm -rf
        var d = Policy.EvaluateCommand("rm -rf build", _root);

        Assert.Equal(RiskLevel.Confirm, d.Level);
        Assert.Null(d.Rule);
        Assert.False(d.Rememberable);
    }
}
