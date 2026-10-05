using System.IO.Compression;
using Flyknit.Core.Security;
using Flyknit.Core.Skills;
using Xunit;

namespace Flyknit.Core.Tests;

public class SkillPackageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-skillpkg").FullName;
    private static readonly CommandPolicy Policy = CommandPolicy.Default();

    public void Dispose() => Directory.Delete(_dir, true);

    private const string SkillMd = """
        ---
        name: excel-report
        description: >
          按公司模板生成 Excel 周报和月报。
          需要汇总质检或生产数据时使用。
        version: 1.2.0
        author: IT
        keywords:
          - excel
          - 报表
        ---

        # 生成周报

        1. 读取数据
        2. 按车间汇总
        """;

    private string Zip(string name, Dictionary<string, string> files)
    {
        var path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entry, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    private string Root(string name = "skills")
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void InstallsFromZipWithWrapperDirectory()
    {
        // GitHub 下载的包外面套一层仓库目录
        var zip = Zip("repo.zip", new()
        {
            ["skills-main/excel-report/SKILL.md"] = SkillMd,
            ["skills-main/excel-report/references/template.md"] = "模板",
            ["skills-main/README.md"] = "# repo",
        });
        var root = Root();

        var result = SkillPackage.Install(zip, root, Policy, origin: "https://github.com/acme/skills");

        Assert.True(result.Ok, result.Message);
        Assert.Equal("excel-report", result.Inspection.Name);
        Assert.Equal("1.2.0", result.Inspection.Version);
        Assert.True(File.Exists(Path.Combine(root, "excel-report", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(root, "excel-report", "references", "template.md")));

        // 安装后能被扫描到，来源写进了 frontmatter
        var skill = new SkillCatalog().AddRoot(root).Refresh().Single();
        Assert.Equal("excel-report", skill.Name);
        Assert.Equal("1.2.0", skill.Version);
        Assert.Equal("https://github.com/acme/skills", skill.Origin);
        Assert.Equal(new[] { "excel", "报表" }, skill.Keywords);
        Assert.Contains("按公司模板生成", skill.Description);
        Assert.StartsWith("# 生成周报", skill.LoadBody());
    }

    [Fact]
    public void ReinstallReplacesTheOldVersionAtomically()
    {
        var root = Root();
        SkillPackage.Install(Zip("v1.zip", new() { ["excel-report/SKILL.md"] = SkillMd, ["excel-report/old.md"] = "旧文件" }), root, Policy);

        var updated = SkillMd.Replace("version: 1.2.0", "version: 2.0.0");
        var result = SkillPackage.Install(Zip("v2.zip", new() { ["excel-report/SKILL.md"] = updated }), root, Policy);

        Assert.True(result.Ok);
        Assert.True(result.Inspection.Replaces);
        Assert.Equal("2.0.0", new SkillCatalog().AddRoot(root).Refresh().Single().Version);
        Assert.False(File.Exists(Path.Combine(root, "excel-report", "old.md"))); // 旧文件不残留
        Assert.Empty(Directory.GetDirectories(root, "*.old-*"));
    }

    [Fact]
    public void RejectsPathTraversal()
    {
        // 直接构造带 ../ 的条目（zip slip）
        var path = Path.Combine(_dir, "evil.zip");
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("a/SKILL.md").Open())) w.Write(SkillMd);
            using (var w = new StreamWriter(archive.CreateEntry("../../escaped.txt").Open())) w.Write("x");
        }
        var root = Root();

        var result = SkillPackage.Install(path, root, Policy);

        Assert.False(result.Ok);
        Assert.Contains("路径不安全", result.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public void RejectsMissingSkillFileDescriptionAndExecutables()
    {
        var root = Root();

        var noSkill = SkillPackage.Install(Zip("a.zip", new() { ["a/readme.md"] = "x" }), root, Policy);
        Assert.Contains("没有找到 SKILL.md", noSkill.Message);

        var noDesc = SkillPackage.Install(Zip("b.zip", new() { ["b/SKILL.md"] = "---\nname: demo-skill\n---\n正文" }), root, Policy);
        Assert.Contains("description", noDesc.Message);

        var exe = SkillPackage.Install(Zip("c.zip", new() { ["c/SKILL.md"] = SkillMd, ["c/setup.exe"] = "MZ" }), root, Policy);
        Assert.Contains("可执行程序", exe.Message);

        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public void RejectsSkillWhoseScriptContainsDangerousCommand()
    {
        var root = Root();
        var result = SkillPackage.Install(
            Zip("d.zip", new() { ["d/SKILL.md"] = SkillMd, ["d/scripts/clean.ps1"] = "Write-Host hi\nRemove-Item -Recurse -Force C:\\Windows" }),
            root, Policy);

        Assert.False(result.Ok);
        Assert.Contains("安全检查", result.Message);
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public void WarnsAboutScriptsAndShortDescription()
    {
        var root = Root();
        var md = "---\nname: tiny-skill\ndescription: 做事\n---\n正文很短";
        var result = SkillPackage.Install(Zip("e.zip", new() { ["e/SKILL.md"] = md, ["e/scripts/run.py"] = "print(1)" }), root, Policy);

        Assert.True(result.Ok, result.Message);
        Assert.Contains(result.Inspection.Warnings, w => w.Contains("脚本"));
        Assert.Contains(result.Inspection.Warnings, w => w.Contains("description"));
        Assert.Equal(new[] { "scripts/run.py" }, result.Inspection.Scripts);
    }

    [Fact]
    public void InstallsEverySkillInARepositoryAndFromAFolder()
    {
        var staging = Path.Combine(_dir, "unpacked");
        Directory.CreateDirectory(Path.Combine(staging, "skills", "a"));
        Directory.CreateDirectory(Path.Combine(staging, "skills", "b"));
        File.WriteAllText(Path.Combine(staging, "skills", "a", "SKILL.md"), SkillMd.Replace("excel-report", "skill-a"));
        File.WriteAllText(Path.Combine(staging, "skills", "b", "SKILL.md"), SkillMd.Replace("excel-report", "skill-b"));

        var roots = SkillPackage.FindAllSkillRoots(staging);
        Assert.Equal(2, roots.Count);

        var root = Root();
        foreach (var r in roots)
        {
            Assert.True(SkillPackage.Install(r, root, Policy, origin: "共享盘").Ok);
        }
        Assert.Equal(new[] { "skill-a", "skill-b" }, new SkillCatalog().AddRoot(root).Refresh().Select(s => s.Name));
    }

    [Fact]
    public void DisabledSkillsAreHiddenFromPromptAndLookup()
    {
        var root = Root();
        SkillPackage.Install(Zip("f.zip", new() { ["f/SKILL.md"] = SkillMd }), root, Policy);
        var catalog = new SkillCatalog().AddRoot(root);

        catalog.SetState(new HashSet<string> { "excel-report" });
        var skill = catalog.Skills.Single();
        Assert.False(skill.Enabled);
        Assert.Null(catalog.Find("excel-report"));
        Assert.NotNull(catalog.FindAny("excel-report"));
        Assert.Equal("", catalog.BuildPromptSection());

        catalog.SetState(new HashSet<string>());
        Assert.Contains("excel-report", catalog.BuildPromptSection());
    }

    [Fact]
    public void RequiredSkillsCannotBeDisabled()
    {
        var root = Root();
        SkillPackage.Install(Zip("g.zip", new() { ["g/SKILL.md"] = SkillMd }), root, Policy);
        var catalog = new SkillCatalog().AddRoot(root, SkillSource.Organization);

        catalog.SetState(new HashSet<string> { "excel-report" }, new HashSet<string> { "excel-report" });

        var skill = catalog.Skills.Single();
        Assert.True(skill.Required);
        Assert.True(skill.Enabled);
    }

    [Fact]
    public void PromptListsOnlyRelevantSkillsWhenThereAreMany()
    {
        var root = Root();
        for (var i = 0; i < 20; i++)
        {
            var md = $"---\nname: skill-{i}\ndescription: 处理第 {i} 号设备的保养记录\n---\n正文正文正文正文正文正文";
            SkillPackage.Install(Zip($"s{i}.zip", new() { [$"s{i}/SKILL.md"] = md }), root, Policy);
        }
        SkillPackage.Install(Zip("x.zip", new() { ["x/SKILL.md"] = SkillMd }), root, Policy);
        var catalog = new SkillCatalog().AddRoot(root);
        catalog.Refresh();

        var all = catalog.BuildPromptSection();
        Assert.DoesNotContain("最相关", all); // 没有 query 时全部列出

        var focused = catalog.BuildPromptSection("帮我生成这周的 Excel 周报");
        Assert.Contains("excel-report", focused);
        Assert.Contains("最相关", focused);
        Assert.True(focused.Split('\n').Count(l => l.StartsWith("- ")) <= SkillCatalog.PromptListLimit);

        Assert.Equal("excel-report", catalog.Search("Excel 周报").First().Skill.Name);
    }

    [Fact]
    public void GitHubPageUrlsBecomeZipLinks()
    {
        Assert.Equal("https://codeload.github.com/acme/skills/zip/HEAD",
            Client.SkillUrl("https://github.com/acme/skills"));
        Assert.Equal("https://codeload.github.com/acme/skills/zip/main",
            Client.SkillUrl("https://github.com/acme/skills/tree/main/excel-report"));
        Assert.Equal("https://example.com/pack.zip", Client.SkillUrl("https://example.com/pack.zip"));
    }

    /// <summary>与客户端 SkillService.GitHubZipUrl 相同的逻辑（核心库测试里不引用 WPF 项目）。</summary>
    private static class Client
    {
        public static string SkillUrl(string url)
        {
            var uri = new Uri(url);
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return uri.ToString();
            }
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2)
            {
                return uri.ToString();
            }
            var branch = parts.Length >= 4 && (parts[2] == "tree" || parts[2] == "blob") ? parts[3] : "HEAD";
            return $"https://codeload.github.com/{parts[0]}/{parts[1]}/zip/{branch}";
        }
    }
}
