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
    public void MessagesAndToolResultsShowWhatTakesTheMostRoom()
    {
        var read = new ToolCall("c1", "read_file", "{}");
        var shell1 = new ToolCall("c2", "run_shell", "{}");
        var shell2 = new ToolCall("c3", "run_shell", "{}");
        var conversation = new List<ChatMessage>
        {
            ChatMessage.User("把 D 盘日报汇总一下"),
            new() { Role = ChatRole.Assistant, Content = "", ToolCalls = { read, shell1, shell2 } },
            ChatMessage.ToolResult(read, "日报第一行\n" + new string('字', 4000)),
            ChatMessage.ToolResult(shell1, "退出码：0\n" + new string('x', 300)),
            ChatMessage.ToolResult(shell2, "退出码：0"),
            // 老消息里没存工具名的：按 assistant 的 tool call 认出来
            new() { Role = ChatRole.Tool, ToolCallId = "c2", Content = "旧格式" },
            ChatMessage.Assistant("汇总好了。"),
        };

        var report = ContextUsage.Measure(Array.Empty<(string, string)>(), Array.Empty<ITool>(), conversation, 131072, 92000, 8192);

        var messages = report.Parts.Single(p => p.Key == ContextUsage.Messages);
        Assert.Equal(new[] { "user", "assistant" }.OrderBy(x => x), messages.Items.Select(i => i.Key).OrderBy(x => x));
        Assert.Equal(messages.Items.Sum(i => i.Tokens), messages.Tokens);

        var outputs = report.Parts.Single(p => p.Key == ContextUsage.ToolOutputs);
        Assert.Equal(new[] { "read_file", "run_shell" }, outputs.Items.Select(i => i.Key)); // 大的在前
        Assert.Equal(outputs.Items.Sum(i => i.Tokens), outputs.Tokens);
        var shell = outputs.Items.Single(i => i.Key == "run_shell");
        Assert.Equal(3, shell.Items.Count);
        Assert.StartsWith("退出码：0", shell.Items[0].Label);                    // 最大的一次在前，开头一句认得出
        Assert.EndsWith("…", outputs.Items[0].Items[0].Label);
        Assert.True(shell.Items[0].Tokens >= shell.Items[1].Tokens);
    }

    [Fact]
    public void OnlyTheLargestFewResultsOfEachToolAreListed()
    {
        var conversation = Enumerable.Range(0, 9)
            .Select(i => ChatMessage.ToolResult(new ToolCall("c" + i, "read_file", "{}"), new string('字', 100 * (i + 1))))
            .ToList();

        var outputs = ContextUsage.Measure(Array.Empty<(string, string)>(), Array.Empty<ITool>(), conversation, 131072, 92000, 8192)
            .Parts.Single(p => p.Key == ContextUsage.ToolOutputs);

        var group = Assert.Single(outputs.Items);
        Assert.Equal(ContextUsage.LargestOutputs, group.Items.Count);
        Assert.Equal("c8", group.Items[0].Key);
        Assert.Equal(conversation.Sum(TokenEstimator.Estimate), group.Tokens); // 组的合计算全部，不只是列出来的几次
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
