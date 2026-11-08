using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Skills;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>技能声明依赖的 MCP 连接器：load_skill 如实报告连没连上、列出真实工具名；调了不存在的连接器工具说清楚原因。</summary>
public sealed class SkillMcpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-skill-mcp").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteSkill(string name, string frontmatter, string body = "按步骤用逆向工具分析目标程序，然后整理成报告。这里写足够长的说明文字。")
    {
        var dir = Path.Combine(_dir, "skills", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: 逆向分析桌面程序并整理成重建方案时使用\n{frontmatter}---\n\n{body}\n");
        return dir;
    }

    private static McpTool Tool(string server, string name) =>
        new(server, server, new McpToolInfo(name, "", "", new JsonObject { ["type"] = "object" }, true, false),
            (_, _, _) => Task.FromResult(new McpCallResult(false, "ok")));

    [Theory]
    [InlineData("mcp-servers: rea, craft\n")]
    [InlineData("mcp-servers: [rea, craft]\n")]
    [InlineData("mcp-servers:\n  - rea\n  - craft\n")]
    [InlineData("requires-mcp: rea、craft\n")]
    public void FrontmatterListsAreParsed(string frontmatter)
    {
        var dir = WriteSkill("re", frontmatter);
        var skill = SkillCatalog.TryParse(Path.Combine(dir, "SKILL.md"), SkillSource.Personal)!;
        Assert.Equal(new[] { "rea", "craft" }, skill.RequiredMcp);
    }

    private ToolContext Ctx(SkillCatalog catalog, ToolRegistry tools) => new()
    {
        Policy = CommandPolicy.Default(),
        ConversationId = "c1",
        Skills = catalog,
        ToolNames = () => tools.All.Select(t => t.Name).ToList(),
    };

    [Fact]
    public async Task LoadSkillReportsConnectedAndMissingConnectors()
    {
        WriteSkill("reverse", "mcp-servers: rea, craft\noptional-mcp-servers: playwright\n");
        var catalog = new SkillCatalog().AddRoot(Path.Combine(_dir, "skills"));
        catalog.Refresh(new HashSet<string>());
        var tools = ToolRegistry.CreateDefault();
        tools.Add(Tool("rea", "decompile"));
        tools.Add(Tool("rea", "list_functions"));

        var result = await new LoadSkillTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { name = "reverse" }), Ctx(catalog, tools), default);

        Assert.True(result.Ok);
        Assert.Contains("rea：已连接", result.Output);
        Assert.Contains("mcp__rea__decompile", result.Output);           // 真实工具名
        Assert.Contains("craft：没有连上（必需）", result.Output);
        Assert.Contains("playwright：没有连上（可选）", result.Output);
        Assert.Contains("不要编造 mcp__craft__", result.Output);
    }

    [Fact]
    public async Task SkillWithoutDependenciesIsUnchanged()
    {
        WriteSkill("plain", "");
        var catalog = new SkillCatalog().AddRoot(Path.Combine(_dir, "skills"));
        catalog.Refresh(new HashSet<string>());
        var result = await new LoadSkillTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { name = "plain" }), Ctx(catalog, ToolRegistry.CreateDefault()), default);
        Assert.True(result.Ok, result.Output);
        Assert.DoesNotContain("连接器", result.Output);
    }

    [Fact]
    public void CallingAToolOfAMissingConnectorExplainsWhy()
    {
        var tools = ToolRegistry.CreateDefault();
        tools.Add(Tool("rea", "decompile"));

        var missingServer = AgentLoop.MissingToolText("mcp__craft__build", tools);
        Assert.Contains("「craft」现在没有连上", missingServer);

        var wrongName = AgentLoop.MissingToolText("mcp__rea__decompile_function", tools);
        Assert.Contains("mcp__rea__decompile", wrongName);              // 列出真实的
        Assert.Contains("只能用这些名字", wrongName);

        Assert.Equal("错误：不存在名为 foo 的工具", AgentLoop.MissingToolText("foo", tools));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.2", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("v2.0.0", "2.0.1", -1)]
    [InlineData("", "1.0", 0)]
    public void VersionsCompareNumerically(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(SkillVersion.Compare(a, b)));

    [Fact]
    public void InspectWarnsAboutDowngradeAndConnectors()
    {
        var dir = WriteSkill("reverse", "version: 1.0.0\nmcp-servers: rea\n");
        var inspection = SkillPackage.Inspect(dir, null, _ => true, _ => "1.2.0");
        Assert.True(inspection.Ok, inspection.Error);
        Assert.Equal("1.2.0", inspection.InstalledVersion);
        Assert.Equal(new[] { "rea" }, inspection.RequiredMcp);
        Assert.Contains(inspection.Warnings, w => w.Contains("降级"));
        Assert.Contains(inspection.Warnings, w => w.Contains("rea"));
    }
}
