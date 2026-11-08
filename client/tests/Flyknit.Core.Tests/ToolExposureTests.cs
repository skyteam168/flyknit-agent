using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>外部服务工具多时按需加载：只发已加载的完整定义，load_tools 列清单、加载后下一步带上。</summary>
public sealed class ToolExposureTests
{
    private static McpTool Mcp(string server, string name, string desc = "说明") => new(server, server.ToUpperInvariant(),
        new McpToolInfo(name, "", desc, new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string" } } }, false, false),
        (_, _, _) => Task.FromResult(new McpCallResult(true, "ok")));

    private static ToolRegistry Registry(int mcpCount)
    {
        var r = new ToolRegistry().Add(new ReadFileTool());
        for (var i = 0; i < mcpCount; i++)
        {
            r.Add(Mcp("mes", $"t{i}", i == 3 ? "查询车间缺勤" : "其他功能"));
        }
        r.Add(new LoadToolsTool(r));
        return r;
    }

    private static ToolContext Ctx() => new() { Policy = CommandPolicy.Default(), ConversationId = "c1" };

    private static JsonElement Args(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();

    [Fact]
    public void FewConnectorToolsAreAllSentAndTheLoaderIsHidden()
    {
        var visible = ToolExposure.Visible(Registry(ToolExposure.DeferAbove).All, new HashSet<string>());

        Assert.Equal(ToolExposure.DeferAbove + 1, visible.Count);
        Assert.DoesNotContain(visible, t => t.Name == LoadToolsTool.ToolName);
    }

    [Fact]
    public void ManyConnectorToolsAreOnlySentOnceLoadedOrUsed()
    {
        var reg = Registry(30);
        var loaded = new HashSet<string> { "mcp__mes__t5" };
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.Assistant, ToolCalls = { new ToolCall("x", "mcp__mes__t7", "{}") } },
        };

        var names = ToolExposure.Visible(reg.All, loaded, history).Select(t => t.Name).ToHashSet();

        Assert.Equal(new HashSet<string> { "read_file", "load_tools", "mcp__mes__t5", "mcp__mes__t7" }, names);
    }

    [Fact]
    public async Task LoadToolsListsTheCatalogAndMarksToolsLoaded()
    {
        var reg = Registry(30);
        var tool = (LoadToolsTool)reg.Get(LoadToolsTool.ToolName)!;
        Assert.Contains("mcp__mes__t3：[MES] 查询车间缺勤", tool.Description);
        var ctx = Ctx();

        var byName = await tool.ExecuteAsync(Args(new { names = new[] { "mcp__mes__t1", "nope" } }), ctx, default);
        var byQuery = await tool.ExecuteAsync(Args(new { query = "缺勤" }), ctx, default);
        var none = await tool.ExecuteAsync(Args(new { query = "完全不相关" }), ctx, default);

        Assert.True(byName.Ok);
        Assert.Contains("参数：", byName.Output);           // 结果里直接带上参数说明
        Assert.Contains("nope", byName.Output);
        Assert.True(byQuery.Ok);
        Assert.Contains("mcp__mes__t3", byQuery.Output);
        Assert.False(none.Ok);
        Assert.Equal(new HashSet<string> { "mcp__mes__t1", "mcp__mes__t3" }, ctx.LoadedTools.ToHashSet());
        Assert.Equal(RiskLevel.Auto, tool.Assess(Args(new { }), ctx).Level);
    }

    [Fact]
    public void ThePromptMentionsLoadingOnlyWhenThereAreManyTools()
    {
        var many = PromptBuilder.BuildMcpSection(new[] { new McpPromptInfo("mes", "MES", 30, "") });
        var few = PromptBuilder.BuildMcpSection(new[] { new McpPromptInfo("mes", "MES", 3, "") });

        Assert.Contains("load_tools", many);
        Assert.DoesNotContain("load_tools", few);
    }
}

/// <summary>太长的工具结果存成文件：模型拿到开头、结尾和路径，要看中间的按行读。</summary>
public sealed class ToolOutputSpillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flyknit-spill-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private static string Big() => string.Join("\n", Enumerable.Range(1, 3000).Select(i => $"第 {i} 行：数据"));

    [Fact]
    public void LongResultsAreSavedWithHeadTailAndPath()
    {
        var full = Big();
        var r = ToolOutputSpill.Apply(ToolResult.Success(full), 2000, _dir, "mcp__mes__query", "call_ab-1");

        Assert.True(r.Ok);
        Assert.StartsWith("第 1 行", r.Output);
        Assert.EndsWith("第 3000 行：数据", r.Output);
        Assert.Contains("read_file", r.Output);
        Assert.Contains("3000 行", r.Output);
        Assert.True(r.Output.Length < 2600);
        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal(full, File.ReadAllText(file));                 // 一个字不少
        Assert.Contains(file, r.Output);
        Assert.Contains("mcp__mes__query-callab1", Path.GetFileName(file));
    }

    [Fact]
    public void ShortResultsReadFileAndNoFolderKeepTheOldBehaviour()
    {
        var ok = ToolResult.Success("短结果");
        Assert.Same(ok, ToolOutputSpill.Apply(ok, 2000, _dir, "run_shell", "c1"));

        var read = ToolOutputSpill.Apply(ToolResult.Success(Big()), 2000, _dir, "read_file", "c2");
        var noDir = ToolOutputSpill.Apply(ToolResult.Success(Big()), 2000, null, "run_shell", "c3");

        Assert.Contains("已省略", read.Output);
        Assert.Contains("已省略", noDir.Output);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void SpilledResultsKeepLessInContext()
    {
        // 完整内容落了盘、随时能读：交给模型的不超过 SpillChars，哪怕调用方给的上限更大
        var big = new string('x', 11000);
        var spilled = ToolOutputSpill.Apply(ToolResult.Success(big), 12000, _dir, "run_shell", "c1");
        Assert.True(spilled.Output.Length < ToolOutputSpill.SpillChars + 400, $"留了 {spilled.Output.Length} 字");
        Assert.Contains("已保存到", spilled.Output);
        var file = Directory.GetFiles(_dir, "*.txt", SearchOption.AllDirectories).Single();
        Assert.Equal(big, File.ReadAllText(file));

        // read_file 本身就是按行从文件读的，不另存，照旧按调用方的上限
        var read = ToolOutputSpill.Apply(ToolResult.Success(big), 12000, _dir, "read_file", "c2");
        Assert.Equal(big, read.Output);
    }

    [Fact]
    public void OldSavedResultsArePruned()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "conv"));
        var old = Path.Combine(_dir, "conv", "old.txt");
        var fresh = Path.Combine(_dir, "conv", "new.txt");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "y");
        File.SetLastWriteTime(old, DateTime.Now - ToolOutputSpill.KeepFor - TimeSpan.FromHours(1));

        ToolOutputSpill.Prune(_dir);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        ToolOutputSpill.Prune(Path.Combine(_dir, "missing")); // 不存在也不报错
    }
}
