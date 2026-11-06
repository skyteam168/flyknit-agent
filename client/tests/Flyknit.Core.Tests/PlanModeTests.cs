using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>计划模式：只读调研、出方案；会改东西的工具都拿掉，权限压到仅可查看。</summary>
public sealed class PlanModeTests
{
    private static McpTool Mcp(string name, bool readOnly, bool destructive = false) => new("erp", "ERP",
        new McpToolInfo(name, "", "", new JsonObject { ["type"] = "object" }, readOnly, destructive),
        (_, _, _) => Task.FromResult(new McpCallResult(false, "")));

    [Fact]
    public void OnlyReadingToolsAreLeft()
    {
        var all = ToolRegistry.CreateDefault().Add(Mcp("query", readOnly: true)).Add(Mcp("update", readOnly: false));

        var names = PlanMode.Filter(all).All.Select(t => t.Name).ToHashSet();

        Assert.Contains("read_file", names);
        Assert.Contains("run_shell", names);       // 仍可执行，但仅可查看只放行查询命令
        Assert.Contains("update_plan", names);
        Assert.Contains("mcp__erp__query", names);
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("edit_file", names);
        Assert.DoesNotContain("delete_path", names);
        Assert.DoesNotContain("mcp__erp__update", names);
        Assert.Equal(PermissionMode.ReadOnly, PlanMode.Cap(PermissionMode.Full));
    }

    [Fact]
    public void ThePromptTellsTheAgentToPlanOnlyInPlanMode()
    {
        var on = new PromptBuilder(null, null).Build(new PromptContext { Mode = ConversationMode.Agent, PlanMode = true });
        var off = new PromptBuilder(null, null).Build(new PromptContext { Mode = ConversationMode.Agent });

        Assert.Contains("<计划模式>", on);
        Assert.DoesNotContain("<计划模式>", off);
    }
}
