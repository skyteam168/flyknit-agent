using Flyknit.Core.Security;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>IT 的工具规则：只收紧；拦截胜过一切；必须确认的跳不过记住的规则；结果打码、加提醒。</summary>
public sealed class ToolRulesTests
{
    private static ToolRules Rules(params ToolRule[] r) => new(r);

    [Fact]
    public void BlockAndConfirmOnlyTighten()
    {
        var rules = Rules(
            new ToolRule { Name = "ERP 只读", Tools = "mcp__erp__*", Match = "delete|update", Action = "block", Message = "ERP 只能查" },
            new ToolRule { Name = "命令要确认", Tools = "run_shell", Action = "confirm", Message = "公司规定命令都要确认" });

        var blocked = rules.Apply("mcp__erp__order", "{\"op\":\"UPDATE\"}", PolicyDecision.Auto());
        var readOk = rules.Apply("mcp__erp__order", "{\"op\":\"query\"}", PolicyDecision.Auto());
        var confirm = rules.Apply("run_shell", "{}", PolicyDecision.Auto() with { Effect = CommandEffect.Read });
        var stillBlocked = rules.Apply("run_shell", "{}", PolicyDecision.Blocked("黑名单"));
        var other = rules.Apply("read_file", "{}", PolicyDecision.Auto());

        Assert.Equal(RiskLevel.Blocked, blocked.Level);
        Assert.Equal("ERP 只能查", blocked.Reason);
        Assert.Equal(RiskLevel.Auto, readOk.Level);
        Assert.Equal(RiskLevel.Confirm, confirm.Level);
        Assert.True(confirm.AlwaysAsk);
        Assert.False(confirm.Rememberable);                 // 不能选「以后自动执行」
        Assert.Equal(CommandEffect.Read, confirm.Effect);
        Assert.Equal(RiskLevel.Blocked, stillBlocked.Level);  // 不会把被拦的放出来
        Assert.Equal("黑名单", stillBlocked.Reason);
        Assert.Equal(RiskLevel.Auto, other.Level);
    }

    [Fact]
    public void NotesAndRedactionApplyToResults()
    {
        var rules = Rules(
            new ToolRule { Name = "库存", Tools = "mcp__erp__*", Action = "note", Message = "改 ERP 前必须先查库存" },
            new ToolRule { Name = "身份证", Tools = "*", Action = "none", Redact = @"\d{17}[\dXx]" });

        var erp = rules.Annotate("mcp__erp__query", "{}", "员工 张三 440301199001011234");
        var plain = rules.Annotate("read_file", "{}", "身份证 44030119900101123X 已登记");

        Assert.StartsWith("【公司规定】改 ERP 前必须先查库存", erp);
        Assert.Contains("员工 张三 ***", erp);
        Assert.DoesNotContain("440301", erp);
        Assert.Equal("身份证 *** 已登记", plain);
    }

    [Fact]
    public void BrokenOrDisabledRulesAreIgnored()
    {
        var rules = Rules(
            new ToolRule { Name = "坏的", Tools = "*", Match = "(unclosed", Action = "block" },
            new ToolRule { Name = "停用", Tools = "*", Action = "block", Enabled = false });

        Assert.True(rules.IsEmpty);
        Assert.Equal(RiskLevel.Auto, rules.Apply("read_file", "{}", PolicyDecision.Auto()).Level);
        Assert.True(ToolRules.None.IsEmpty);
    }
}
