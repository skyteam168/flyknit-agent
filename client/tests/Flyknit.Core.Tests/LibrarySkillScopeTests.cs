using Flyknit.Core.Skills;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>
/// 技能的使用范围在员工端的落实：IT 把技能限定给别的部门以后，这台电脑上从技能库装过的要删掉；
/// 员工从别处装的、机器级目录里的不能误删。
/// </summary>
public sealed class LibrarySkillScopeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "flyknit-scope-test");
    private static readonly string Org = Path.Combine(Root, "skills", "org");
    private static readonly string User = Path.Combine(Root, "skills");
    private static readonly string Machine = Path.Combine(Root, "ProgramData", "skills");

    private static SkillInfo Skill(string name, string dir, string origin) => new()
    {
        Name = name,
        Description = "",
        Directory = dir,
        Meta = origin.Length > 0 ? new Dictionary<string, string> { ["origin"] = origin } : new Dictionary<string, string>(),
    };

    [Fact]
    public void RemovesLibrarySkillsTheServerNoLongerSends()
    {
        var installed = new[]
        {
            Skill("attendance", Path.Combine(Org, "attendance"), LibrarySkillScope.Origin),       // 必装的，范围收回了
            Skill("excel-report", Path.Combine(User, "excel-report"), LibrarySkillScope.Origin),  // 员工从技能库装的，还在范围里
            Skill("payroll", Path.Combine(User, "payroll"), LibrarySkillScope.Origin),            // 员工从技能库装的，范围收回了
        };
        var stale = LibrarySkillScope.Stale(installed, new[] { "excel-report" }, new[] { Org, User });
        Assert.Equal(new[] { "attendance", "payroll" }, stale.Select(s => s.Name).OrderBy(n => n));
    }

    [Fact]
    public void NeverTouchesSkillsFromElsewhereOrMachineFolder()
    {
        var installed = new[]
        {
            Skill("my-own", Path.Combine(User, "my-own"), "https://github.com/x/y"),            // 员工自己从别处装的
            Skill("no-origin", Path.Combine(User, "no-origin"), ""),
            Skill("it-copied", Path.Combine(Machine, "it-copied"), LibrarySkillScope.Origin),   // 机器级目录：IT 拷的，只读
        };
        Assert.Empty(LibrarySkillScope.Stale(installed, Array.Empty<string>(), new[] { Org, User }));
    }

    [Theory]
    [InlineData("Attendance Report", "attendance-report")]   // 显示名 → 规整后的包名
    [InlineData("attendance-report", "ATTENDANCE-REPORT")]   // 大小写不敏感
    public void MatchesPackageNamesLikeTheInstaller(string skillName, string package)
    {
        var skill = Skill(skillName, Path.Combine(User, "whatever"), LibrarySkillScope.Origin);
        Assert.True(LibrarySkillScope.Matches(skill, package));
        Assert.Empty(LibrarySkillScope.Stale(new[] { skill }, new[] { package }, new[] { User }));
    }

    [Fact]
    public void MatchesByFolderName()
    {
        var skill = Skill("考勤助手", Path.Combine(User, "attendance"), LibrarySkillScope.Origin);
        Assert.True(LibrarySkillScope.Matches(skill, "attendance"));
    }
}
