using System;
using System.IO;
using System.Linq;
using Flyknit.Core.Security;
using Xunit;

namespace Flyknit.Core.Tests;

public class PathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flyknit-paths-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _workspace;
    private readonly string _outside;

    public PathResolverTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _outside = Path.Combine(_root, "secret");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "payroll.txt"), "工资表");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* 清不掉就算了 */ }
    }

    // ---------- 相对路径：原来扫不到的那一类 ----------

    [Fact]
    public void RelativePathResolvesAgainstTheWorkingDirectory()
    {
        var r = PathResolver.Resolve("report.txt", _workspace);

        Assert.True(r.Known);
        Assert.Equal(Path.Combine(_workspace, "report.txt"), r.Full);
    }

    [Fact]
    public void DotDotEscapesAreFollowedOutOfTheWorkspace()
    {
        // 这正是原来的正则看不见的：它只认 X:\ 开头的绝对路径
        var r = PathResolver.Resolve(Path.Combine("..", "secret", "payroll.txt"), _workspace);

        Assert.True(r.Known);
        Assert.Equal(Path.Combine(_outside, "payroll.txt"), r.Full);
        Assert.False(PathResolver.IsUnder(r.Full!, _workspace));
    }

    [Fact]
    public void DeepDotDotChainsAreFolded()
    {
        var raw = string.Join(Path.DirectorySeparatorChar, "..", "..", "..", "..", "Windows");
        var r = PathResolver.Resolve(raw, _workspace);

        Assert.True(r.Known);
        Assert.DoesNotContain("..", r.Full!);
        Assert.False(PathResolver.IsUnder(r.Full!, _workspace));
    }

    [Fact]
    public void ADotPrefixStaysInside()
    {
        var r = PathResolver.Resolve(Path.Combine(".", "build", "out.log"), _workspace);

        Assert.True(r.Known);
        Assert.True(PathResolver.IsUnder(r.Full!, _workspace));
    }

    // ---------- 链接：junction / 符号链接绕过 ----------

    [Fact]
    public void ALinkInsideTheWorkspaceDoesNotLaunderAnOutsideTarget()
    {
        var link = Path.Combine(_workspace, "shortcut");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception)
        {
            return; // 没权限建链接（Windows 非管理员）时跳过，不是实现的问题
        }

        // 纯字符串比较会认为它在工作区里，因为路径前缀确实匹配
        Assert.StartsWith(_workspace, link, StringComparison.OrdinalIgnoreCase);
        // 跟到真实目标之后就不在了
        Assert.False(PathResolver.IsUnder(link, _workspace));

        var through = PathResolver.Resolve(Path.Combine("shortcut", "payroll.txt"), _workspace);
        Assert.True(through.Known);
        Assert.False(PathResolver.IsUnder(through.Full!, _workspace));
    }

    [Fact]
    public void APathUnderAWorkspaceThatIsItselfALinkStillCounts()
    {
        // 工作区本身是链接时，里面的文件仍该算在工作区内
        var linkedWorkspace = Path.Combine(_root, "ws-link");
        try
        {
            Directory.CreateSymbolicLink(linkedWorkspace, _workspace);
        }
        catch (Exception)
        {
            return;
        }
        var file = Path.Combine(_workspace, "note.txt");
        File.WriteAllText(file, "x");

        Assert.True(PathResolver.IsUnder(file, linkedWorkspace));
    }

    // ---------- 解析不出来的，一律当作不确定 ----------

    [Theory]
    [InlineData("%UNDEFINED_VAR_XYZ%\\file.txt")]
    [InlineData("$HOME/secret")]
    [InlineData("${HOME}/secret")]
    [InlineData("$(pwd)/file")]
    [InlineData("./build/*.log")]
    [InlineData("./out/?.txt")]
    [InlineData("`whoami`/x")]
    public void DynamicPathsAreNotTreatedAsKnown(string raw)
    {
        var r = PathResolver.Resolve(raw, _workspace);

        Assert.False(r.Known);
        Assert.Null(r.Full);
    }

    [Fact]
    public void DefinedEnvironmentVariablesAreExpanded()
    {
        var r = PathResolver.Resolve("%TEMP%", _workspace);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(r.Known);
        }
        // 非 Windows 上 %TEMP% 展不开，会被当成不确定——这正是想要的保守行为
    }

    [Fact]
    public void BlankIsNotKnown()
    {
        Assert.False(PathResolver.Resolve("", _workspace).Known);
        Assert.False(PathResolver.Resolve("   ", _workspace).Known);
    }

    // ---------- 从命令里提取 ----------

    [Fact]
    public void FindsRelativeTargetsInACommand()
    {
        var found = PathResolver.FromCommand($"rm -rf ..{Path.DirectorySeparatorChar}secret", _workspace);

        Assert.Contains(found, p => p.Known && p.Full == _outside);
    }

    [Fact]
    public void IgnoresSwitchesAndUrls()
    {
        var found = PathResolver.FromCommand("curl -sS https://example.com/a/b -o out.txt", _workspace);

        Assert.DoesNotContain(found, p => p.Raw.Contains("://"));
        Assert.DoesNotContain(found, p => p.Raw.StartsWith("-"));
        Assert.Contains(found, p => p.Raw == "out.txt");
    }

    [Fact]
    public void QuotedPathsAreUnquoted()
    {
        var r = PathResolver.Resolve("\"" + Path.Combine(_outside, "payroll.txt") + "\"", _workspace);

        Assert.True(r.Known);
        Assert.Equal(Path.Combine(_outside, "payroll.txt"), r.Full);
    }

    [Fact]
    public void ReportsEachDistinctPathOnce()
    {
        var found = PathResolver.FromCommand("copy a.txt b.txt && copy a.txt c.txt", _workspace);

        Assert.Equal(1, found.Count(p => p.Raw == "a.txt"));
    }

    // ---------- 范围判断 ----------

    [Fact]
    public void ScopeChecksAreCaseInsensitiveAndSeparatorAgnostic()
    {
        var file = Path.Combine(_workspace, "sub", "a.txt");

        Assert.True(PathResolver.IsUnder(file, _workspace));
        Assert.True(PathResolver.IsUnder(file, _workspace + Path.DirectorySeparatorChar));
        Assert.True(PathResolver.IsUnder(_workspace, _workspace));
    }

    [Fact]
    public void ASiblingWithASharedPrefixIsNotInside()
    {
        // workspace-backup 不在 workspace 里，尽管字符串前缀匹配
        Assert.False(PathResolver.IsUnder(_workspace + "-backup", _workspace));
    }

    [Fact]
    public void BlankRootIsNeverInside()
    {
        Assert.False(PathResolver.IsUnder(_workspace, ""));
        Assert.False(PathResolver.IsUnder(_workspace, "   "));
    }

    // ---------- Windows 路径：判定不该跟着分析器跑在哪个系统上变 ----------

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"D:/data/report.xlsx")]
    [InlineData(@"\\fileserver\share\qc")]
    public void WindowsAbsolutePathsResolveOnAnyHost(string raw)
    {
        var r = PathResolver.Resolve(raw, _workspace);

        Assert.True(r.Known);
        Assert.True(PathResolver.IsWindowsRooted(r.Full!));
        // 不能被当成相对路径拼到工作区后面
        Assert.False(PathResolver.IsUnder(r.Full!, _workspace));
    }

    [Fact]
    public void WindowsDotDotIsFoldedTextually()
    {
        var r = PathResolver.Resolve(@"D:\work\..\..\Windows\System32", _workspace);

        Assert.True(r.Known);
        Assert.DoesNotContain("..", r.Full!);
        Assert.Equal(@"D:\Windows\System32", r.Full);
    }

    [Fact]
    public void WindowsScopeChecksCompareWholeSegments()
    {
        Assert.True(PathResolver.IsUnder(@"D:\ws\sub\a.txt", @"D:\ws"));
        Assert.True(PathResolver.IsUnder(@"D:\WS\A.TXT", @"d:\ws"));
        // D:\ws-backup 不在 D:\ws 里，哪怕字符串前缀匹配
        Assert.False(PathResolver.IsUnder(@"D:\ws-backup\a.txt", @"D:\ws"));
    }

    [Fact]
    public void AWindowsPathIsNeverInsideAPosixWorkspace()
    {
        Assert.False(PathResolver.IsUnder(@"C:\Windows", _workspace));
    }
}
