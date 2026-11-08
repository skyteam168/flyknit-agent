namespace Flyknit.Core.Skills;

/// <summary>
/// 公司技能库的使用范围在员工端的落实：IT 把某个技能限定给别的部门（或者下架、删掉）以后，
/// 这台电脑上之前从技能库装的那份要删掉，不能「装过就一直能用」。
///
/// 只动从技能库装的（SKILL.md 里 origin 是「公司技能库」），并且只在 org 目录和个人目录里：
/// 员工自己从别处装的、IT 拷到机器级目录（C:\ProgramData）的都不碰。
/// 判断依据是服务端这次下发给这台电脑的技能清单（服务端已经按范围过滤过）；拉清单失败时不调用这里。
/// </summary>
public static class LibrarySkillScope
{
    /// <summary>从技能库安装时写进 SKILL.md 的 origin。</summary>
    public const string Origin = "公司技能库";

    /// <summary>按包名找本机的技能：名称、规整后的名称、所在文件夹名，和员工端按包名匹配的规则一致。</summary>
    public static bool Matches(SkillInfo skill, string packageName)
    {
        var key = packageName.Trim();
        return skill.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
            || SkillPackage.Sanitize(skill.Name).Equals(key, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(skill.Directory.TrimEnd('\\', '/')).Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这台电脑上要删掉的技能：来自技能库、在可删的目录里、服务端这次没再下发。</summary>
    public static IReadOnlyList<SkillInfo> Stale(IEnumerable<SkillInfo> installed, IReadOnlyCollection<string> library, IEnumerable<string> removableRoots)
    {
        var roots = removableRoots
            .Where(r => r.Length > 0)
            .Select(r => Path.GetFullPath(r).TrimEnd('\\', '/') + Path.DirectorySeparatorChar)
            .ToList();
        return installed
            .Where(s => s.Origin == Origin)
            .Where(s => roots.Any(r => Path.GetFullPath(s.Directory).StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            .Where(s => !library.Any(name => Matches(s, name)))
            .ToList();
    }
}
