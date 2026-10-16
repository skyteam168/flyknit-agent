using System.Text.Json.Nodes;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

/// <summary>「会话信息」面板：上下文用量的拆分和手动整理对话。</summary>
public class ContextUsageTests
{
    private sealed class SummaryGateway : IChatGateway
    {
        public int Calls;
        public Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ChatTurn { Content = "摘要：员工在核对 6# 区域的考勤", Usage = new TokenUsage(100, 20) });
        }
    }

    [Fact]
    public void PromptSectionsAddUpToTheWholePrompt()
    {
        var builder = new PromptBuilder(null, null);
        var prompt = builder.Build(new PromptContext
        {
            Mode = ConversationMode.Agent,
            Workspace = Path.GetTempPath(),
            McpServers = new List<McpPromptInfo> { new("mes", "MES", 3, "查询生产数据") },
        });

        Assert.Equal(prompt, string.Concat(builder.Sections.Select(s => s.Text)).TrimEnd());
        var keys = builder.Sections.Select(s => s.Key).ToList();
        Assert.Equal("base", keys[0]);
        Assert.Contains("guide", keys);
        Assert.Contains("mcp", keys);
        Assert.Contains("<已连接的外部服务>", builder.Sections.Single(s => s.Key == "mcp").Text);

        var translate = new PromptBuilder(null, null);
        var t = translate.Build(new PromptContext { Mode = ConversationMode.Translate, TranslateTo = "vi" });
        Assert.Equal(t, Assert.Single(translate.Sections).Text);
    }

    [Fact]
    public void ToolsAreGroupedIntoBuiltInAndOneGroupPerConnector()
    {
        McpTool Mcp(string server, string name, int words) => new(server, server.ToUpperInvariant(),
            new McpToolInfo(name, "", string.Join(" ", Enumerable.Repeat("说明", words)), new JsonObject { ["type"] = "object" }, false, false),
            (_, _, _) => Task.FromResult(new McpCallResult(true, "")));
        var tools = new ToolRegistry().Add(new ReadFileTool()).Add(new UpdatePlanTool())
            .Add(Mcp("mes", "query", 200)).Add(Mcp("mes", "export", 10)).Add(Mcp("erp", "order", 50));
        var conversation = new List<ChatMessage>
        {
            ChatMessage.User("6# 区域今天缺勤几人？"),
            ChatMessage.ToolResult(new ToolCall("c1", "read_file", "{}"), new string('字', 3000)),
            ChatMessage.Assistant("4 人。"),
        };

        var report = ContextUsage.Measure(new[] { ("base", "你是助手"), ("memory", new string('记', 600)) }, tools.All, conversation,
            contextLength: 131072, compactAt: 92000, reserveOutput: 8192, lastPromptTokens: 5400);

        var toolPart = report.Parts.Single(p => p.Key == ContextUsage.Tools);
        Assert.Equal(new[] { "builtin", "mcp:mes", "mcp:erp" }, toolPart.Items.Select(i => i.Key));
        var mes = toolPart.Items.Single(i => i.Key == "mcp:mes");
        Assert.Equal("MES", mes.Label);
        Assert.Equal(new[] { "query", "export" }, mes.Items.Select(i => i.Label)); // 按占用从大到小
        Assert.Equal(mes.Items.Sum(i => i.Tokens), mes.Tokens);
        Assert.Equal(toolPart.Items.Sum(i => i.Tokens), toolPart.Tokens);

        var system = report.Parts.Single(p => p.Key == ContextUsage.SystemPrompt);
        Assert.Equal("记忆", system.Items[0].Label); // 大的在前
        Assert.True(report.Parts.Single(p => p.Key == ContextUsage.ToolOutputs).Tokens > report.Parts.Single(p => p.Key == ContextUsage.Messages).Tokens);
        Assert.Equal(report.Parts.Sum(p => p.Tokens), report.Used);
        Assert.Equal(5400, report.LastPromptTokens);

        // 压缩之后只换对话部分
        var after = report.WithConversation(new[] { ChatMessage.User("继续") });
        Assert.Equal(toolPart.Tokens, after.Parts.Single(p => p.Key == ContextUsage.Tools).Tokens);
        Assert.Equal(0, after.Parts.Single(p => p.Key == ContextUsage.ToolOutputs).Tokens);
        Assert.Equal(5400, after.LastPromptTokens);
    }

    [Fact]
    public async Task CompactingNowKeepsTheLatestExchangeAndSummarizesTheRest()
    {
        var gateway = new SummaryGateway();
        var context = new ContextManager(gateway, "系统提示", enableSessionMemory: false);
        CompactionInfo? compacted = null;
        context.Compacted += i => compacted = i;
        var history = new List<ChatMessage>
        {
            ChatMessage.System(context.SystemPrompt),
            ChatMessage.User("第一个问题"),
            ChatMessage.Assistant("第一个回答"),
            ChatMessage.User("第二个问题"),
            ChatMessage.Assistant("第二个回答"),
        };
        var keep = history[3].Id;

        var info = await context.CompactNowAsync(history, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Same(info, compacted);
        Assert.Equal(1, gateway.Calls);
        Assert.Equal(keep, history[1].Id); // 留下的是最近一问一答
        Assert.Contains("摘要：员工在核对", history[0].Content);
        Assert.Equal(3, history.Count); // 系统提示 + 最近一问一答
    }

    [Fact]
    public async Task CompactingNowDoesNothingWhenThereIsOnlyOneExchange()
    {
        var gateway = new SummaryGateway();
        var context = new ContextManager(gateway, "系统提示", enableSessionMemory: false);
        var history = new List<ChatMessage> { ChatMessage.System("系统提示"), ChatMessage.User("问题"), ChatMessage.Assistant("回答") };

        Assert.Null(await context.CompactNowAsync(history, CancellationToken.None));
        Assert.Equal(0, gateway.Calls);
        Assert.Equal(3, history.Count);
    }
}
