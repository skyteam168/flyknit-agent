using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Storage;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>输入框 /export：对话导出成 Markdown，只要员工看得到的内容。</summary>
public class ConversationExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flyknit-export").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ExportsTheVisibleConversationOnly()
    {
        var call = new ToolCall("c1", "read_file", "{\"path\":\"secret.txt\"}");
        var user = ChatMessage.User("汇总一下 10 月的日报");
        user.TurnContext = "<turn-context>当前时间、技能说明</turn-context>";
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("系统提示词：不要导出我"),
            user,
            new() { Role = ChatRole.Assistant, Content = "", ToolCalls = new() { call }, Reasoning = "思考过程" },
            ChatMessage.ToolResult(call, "文件全文：机密内容"),
            new() { Role = ChatRole.Assistant, Content = "", ToolCalls = new() { new ToolCall("c2", "read_file", "{}") } },
            ChatMessage.ToolResult(new ToolCall("c2", "read_file", "{}"), "又一个文件"),
            new() { Role = ChatRole.Assistant, Content = "汇总好了，共 31 天。", Outputs = new() { @"D:\日报\10月汇总.xlsx" } },
            ChatMessage.User(ContextManager.ContinueMarker),
        };

        var md = ConversationExport.ToMarkdown("10 月日报", messages, DateTimeOffset.Parse("2026-11-12T15:30:00+08:00"));

        Assert.StartsWith("# 10 月日报", md);
        Assert.Contains("## 我\n\n汇总一下 10 月的日报", md);
        Assert.Contains("## FlyknitBuddy\n\n汇总好了，共 31 天。", md);
        Assert.Contains("*用了：read_file ×2*", md);          // 连续的工具调用合成一行，只有名字
        Assert.Contains("产出文件：10月汇总.xlsx", md);
        // 不导出的东西
        Assert.DoesNotContain("系统提示词", md);
        Assert.DoesNotContain("turn-context", md);
        Assert.DoesNotContain("思考过程", md);
        Assert.DoesNotContain("机密内容", md);
        Assert.DoesNotContain("secret.txt", md);
        Assert.DoesNotContain(ContextManager.ContinueMarker, md);
    }

    [Theory]
    [InlineData("10 月日报", "对话导出-10 月日报-20261112-1530.md")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "对话导出-a_b_c_d_e_f_g_h_i_j-20261112-1530.md")]
    [InlineData("", "对话导出-对话-20261112-1530.md")]
    [InlineData("...", "对话导出-对话-20261112-1530.md")]
    public void FileNamesAreSafeForWindows(string title, string expected)
    {
        var at = new DateTimeOffset(2026, 11, 12, 15, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 12)));
        Assert.Equal(expected, ConversationExport.FileName(title, at));
    }

    [Fact]
    public void LongTitlesAreCut()
    {
        var name = ConversationExport.FileName(new string('长', 100), DateTimeOffset.Now);
        Assert.True(name.Length < 70);
    }

    [Fact]
    public void ExistingFilesAreNeverOverwritten()
    {
        var first = ConversationExport.UniquePath(_dir, "导出.md");
        File.WriteAllText(first, "旧的");
        var second = ConversationExport.UniquePath(_dir, "导出.md");
        Assert.Equal(Path.Combine(_dir, "导出 (2).md"), second);
        File.WriteAllText(second, "x");
        Assert.Equal(Path.Combine(_dir, "导出 (3).md"), ConversationExport.UniquePath(_dir, "导出.md"));
        Assert.Equal("旧的", File.ReadAllText(first));
    }
}
