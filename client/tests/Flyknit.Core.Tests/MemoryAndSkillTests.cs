using Flyknit.Core.Agent;
using Flyknit.Core.Memory;
using Flyknit.Core.Skills;
using Xunit;

namespace Flyknit.Core.Tests;

public class MemoryAndSkillTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-mem").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void MemoryCreatesDefaultsAndDeduplicates()
    {
        var store = new MemoryStore(Path.Combine(_dir, "memory"));
        store.EnsureDefaults();
        Assert.Contains("Flyknit", store.Read(MemoryStore.AgentFile));

        store.Remember("日报放在 D:\\日报");
        store.Remember("日报放在 D:\\日报");
        var text = store.Read(MemoryStore.MemoryFile);
        Assert.Equal(1, text.Split("日报放在").Length - 1);

        var prompt = store.BuildPromptSection();
        Assert.Contains("<长期记忆>", prompt);
        Assert.Contains("D:\\日报", prompt);
    }

    [Fact]
    public void ParsesFrontmatterIncludingFoldedDescription()
    {
        var (meta, body) = SkillCatalog.SplitFrontmatter("---\nname: excel-report\ndescription: >\n  Build weekly Excel\n  reports.\nlicense: \"MIT\"\n---\n# Body\nstep 1\n");
        Assert.Equal("excel-report", meta["name"]);
        Assert.Equal("Build weekly Excel reports.", meta["description"]);
        Assert.Equal("MIT", meta["license"]);
        Assert.StartsWith("# Body", body);
    }

    [Fact]
    public void CatalogPrefersOrganizationSkillsAndHonorsDisabled()
    {
        var personal = Path.Combine(_dir, "skills");
        var org = Path.Combine(personal, "org");
        WriteSkill(Path.Combine(personal, "office", "translate-terms"), "translate-terms", "个人版术语表");
        WriteSkill(Path.Combine(org, "translate-terms"), "translate-terms", "企业版术语表");
        WriteSkill(Path.Combine(personal, "meeting-notes"), "meeting-notes", "整理会议纪要");
        Directory.CreateDirectory(Path.Combine(personal, "no-description"));
        File.WriteAllText(Path.Combine(personal, "no-description", "SKILL.md"), "---\nname: x\n---\nbody");

        var catalog = new SkillCatalog().AddRoot(org, isOrganization: true).AddRoot(personal);
        var skills = catalog.Refresh(new HashSet<string> { "meeting-notes" });

        Assert.Equal(2, skills.Count);
        var terms = skills.Single(s => s.Name == "translate-terms");
        Assert.True(terms.IsOrganization);
        Assert.Equal("企业版术语表", terms.Description);
        Assert.Null(catalog.Find("meeting-notes")); // 已停用
        Assert.Contains("translate-terms", catalog.BuildPromptSection());
        Assert.DoesNotContain("meeting-notes", catalog.BuildPromptSection());
        Assert.Equal("步骤说明", terms.LoadBody());
    }

    [Fact]
    public void TranslatePromptNamesTargetLanguage()
    {
        var prompt = new PromptBuilder(null, null).Build(new PromptContext { Mode = ConversationMode.Translate, TranslateTo = "km" });
        Assert.Contains("Khmer", prompt);
    }

    [Theory]
    [InlineData("\"越南语邮件翻译\"", "越南语邮件翻译")]
    [InlineData("<think>hmm</think>\n标题：Excel 数据分析。", "Excel 数据分析")]
    [InlineData("  ", null)]
    public void CleansGeneratedTitles(string raw, string? expected)
    {
        Assert.Equal(expected, TitleGenerator.Clean(raw));
    }

    private static void WriteSkill(string dir, string name, string description)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n步骤说明\n");
    }
}
