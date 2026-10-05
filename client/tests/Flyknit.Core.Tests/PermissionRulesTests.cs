using Flyknit.Core.Security;
using Xunit;

namespace Flyknit.Core.Tests;

public class PermissionRulesTests : IDisposable
{
    private static readonly CommandPolicy Policy = CommandPolicy.Default();
    private readonly string _ws = Directory.CreateTempSubdirectory("flyknit-ws").FullName;

    public void Dispose() => Directory.Delete(_ws, true);

    [Theory]
    [InlineData(PermissionMode.ReadOnly, RiskLevel.Auto)]
    [InlineData(PermissionMode.Workspace, RiskLevel.Auto)]
    [InlineData(PermissionMode.Full, RiskLevel.Auto)]
    public void ReadonlyCommandsRunInEveryMode(PermissionMode mode, RiskLevel expected)
    {
        Assert.Equal(expected, PermissionRules.ForCommand(Policy, mode, _ws, "ipconfig /all", _ws).Level);
    }

    [Theory]
    [InlineData(PermissionMode.ReadOnly, RiskLevel.Blocked)]
    [InlineData(PermissionMode.Workspace, RiskLevel.Confirm)]
    [InlineData(PermissionMode.Full, RiskLevel.Auto)]
    public void OrdinaryCommandsFollowTheMode(PermissionMode mode, RiskLevel expected)
    {
        Assert.Equal(expected, PermissionRules.ForCommand(Policy, mode, _ws, "npm install", _ws).Level);
    }

    [Theory]
    [InlineData(PermissionMode.ReadOnly)]
    [InlineData(PermissionMode.Workspace)]
    [InlineData(PermissionMode.Full)]
    public void DangerousCommandsAreBlockedInEveryMode(PermissionMode mode)
    {
        Assert.Equal(RiskLevel.Blocked, PermissionRules.ForCommand(Policy, mode, _ws, "Remove-Item -Recurse -Force C:\\data", _ws).Level);
        Assert.Equal(RiskLevel.Blocked, PermissionRules.ForCommand(Policy, mode, _ws, "format C: /q", _ws).Level);
    }

    [Fact]
    public void WorkspaceModeRejectsCommandsStartedOutsideWorkspace()
    {
        var outside = Path.GetTempPath();
        Assert.Equal(RiskLevel.Blocked, PermissionRules.ForCommand(Policy, PermissionMode.Workspace, _ws, "npm install", outside).Level);
    }

    [Fact]
    public void OutsidePathsAreNamedInTheReason()
    {
        var d = PermissionRules.ForCommand(Policy, PermissionMode.Workspace, _ws, "copy report.xlsx E:\\backup\\report.xlsx", _ws);
        Assert.Equal(RiskLevel.Confirm, d.Level);
        Assert.Contains("E:\\backup\\report.xlsx", d.Reason);
        Assert.Empty(PermissionRules.OutsidePaths("copy a.txt b.txt", _ws));
    }

    [Fact]
    public void WritesFollowWorkspaceBoundary()
    {
        var inside = Path.Combine(_ws, "out", "a.xlsx");
        var outside = Path.Combine(Path.GetTempPath(), "flyknit-elsewhere.txt");

        Assert.Equal(RiskLevel.Blocked, PermissionRules.ForWrite(Policy, PermissionMode.ReadOnly, _ws, inside, false).Level);
        Assert.Equal(RiskLevel.Auto, PermissionRules.ForWrite(Policy, PermissionMode.Workspace, _ws, inside, false).Level);
        Assert.Equal(RiskLevel.Blocked, PermissionRules.ForWrite(Policy, PermissionMode.Workspace, _ws, outside, false).Level);
        Assert.Equal(RiskLevel.Auto, PermissionRules.ForWrite(Policy, PermissionMode.Full, _ws, outside, false).Level);

        var del = PermissionRules.ForWrite(Policy, PermissionMode.Workspace, _ws, inside, true);
        Assert.Equal(RiskLevel.Confirm, del.Level);
        Assert.False(del.Rememberable);
        Assert.Equal(RiskLevel.Auto, PermissionRules.ForWrite(Policy, PermissionMode.Full, _ws, inside, true).Level);
        Assert.Equal(RiskLevel.Confirm, PermissionRules.ForWrite(Policy, PermissionMode.Full, _ws, outside, true).Level);
    }

    [Fact]
    public void NoWorkspaceMeansNothingIsWritableInWorkspaceMode()
    {
        Assert.Equal(RiskLevel.Blocked, PermissionRules.ForWrite(Policy, PermissionMode.Workspace, null, Path.Combine(_ws, "a.txt"), false).Level);
    }

    [Theory]
    [InlineData("readonly", PermissionMode.ReadOnly)]
    [InlineData("workspace", PermissionMode.Workspace)]
    [InlineData("full", PermissionMode.Full)]
    [InlineData("", PermissionMode.Workspace)]
    [InlineData(null, PermissionMode.Workspace)]
    public void ModesRoundTrip(string? text, PermissionMode mode)
    {
        Assert.Equal(mode, PermissionModes.Parse(text));
        if (!string.IsNullOrEmpty(text))
        {
            Assert.Equal(text, PermissionModes.ToText(mode));
        }
    }
}
