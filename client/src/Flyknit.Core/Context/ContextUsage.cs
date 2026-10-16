using Flyknit.Core.Chat;
using Flyknit.Core.Mcp;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Context;

/// <summary>上下文里的一块：多少 token，下面还可以再分（比如每个连接器、每个工具）。</summary>
public sealed record ContextPart(string Key, string Label, int Tokens, IReadOnlyList<ContextPart> Items)
{
    public static ContextPart Of(string key, string label, int tokens) => new(key, label, tokens, Array.Empty<ContextPart>());
}

/// <summary>
/// 一个对话的上下文用了多少、花在哪：给「会话信息」面板看。
/// 各部分是按字数估算的（和服务端计费会有出入），LastPromptTokens 是模型服务报回来的最近一次真实输入。
/// </summary>
public sealed class ContextUsageReport
{
    /// <summary>模型的上下文长度。</summary>
    public int ContextLength { get; init; }

    /// <summary>给模型回答预留的部分。</summary>
    public int ReserveOutput { get; init; }

    /// <summary>超过这个数就自动把较早的对话压成摘要。</summary>
    public int CompactAt { get; init; }

    public IReadOnlyList<ContextPart> Parts { get; init; } = Array.Empty<ContextPart>();

    /// <summary>估算的已用量（各部分相加）。</summary>
    public int Used => Parts.Sum(p => p.Tokens);

    /// <summary>模型服务报回来的最近一次输入 token；没有时为 null。</summary>
    public int? LastPromptTokens { get; init; }

    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.Now;

    /// <summary>对话部分换掉（压缩之后、又聊了几句），系统提示和工具不变。</summary>
    public ContextUsageReport WithConversation(IReadOnlyList<ChatMessage> conversation, int? lastPromptTokens = null) => new()
    {
        ContextLength = ContextLength,
        ReserveOutput = ReserveOutput,
        CompactAt = CompactAt,
        Parts = Parts.Where(p => p.Key is not (ContextUsage.Messages or ContextUsage.ToolOutputs))
            .Concat(ContextUsage.ConversationParts(conversation)).ToList(),
        LastPromptTokens = lastPromptTokens ?? LastPromptTokens,
    };
}

public static class ContextUsage
{
    public const string SystemPrompt = "system";
    public const string Tools = "tools";
    public const string Messages = "messages";
    public const string ToolOutputs = "tool_outputs";

    /// <summary>系统提示词各段的名字（键来自 PromptBuilder.Sections，summary / experts 由宿主加）。</summary>
    public static readonly IReadOnlyDictionary<string, string> SectionLabels = new Dictionary<string, string>
    {
        ["base"] = "基础规则",
        ["expert"] = "专家设定",
        ["guide"] = "图表与界面卡片说明",
        ["memory"] = "记忆",
        ["workspace"] = "工作区说明文件",
        ["episodes"] = "相似的历史任务",
        ["skills"] = "技能清单",
        ["mcp"] = "连接器说明",
        ["experts"] = "可派遣的专家",
        ["summary"] = "较早对话的摘要",
    };

    /// <param name="sections">系统提示词的各段（键、原文）；summary（压缩出的摘要和计划）等由宿主补进来。</param>
    /// <param name="tools">这次请求带上的工具。</param>
    /// <param name="conversation">发给模型的对话（不含系统提示）。</param>
    public static ContextUsageReport Measure(
        IEnumerable<(string Key, string Text)> sections,
        IEnumerable<ITool> tools,
        IReadOnlyList<ChatMessage> conversation,
        int contextLength,
        int compactAt,
        int reserveOutput,
        int? lastPromptTokens = null)
    {
        var parts = new List<ContextPart>();

        var system = sections
            .GroupBy(s => s.Key)
            .Select(g => ContextPart.Of(g.Key, SectionLabels.TryGetValue(g.Key, out var l) ? l : g.Key, g.Sum(s => TokenEstimator.Estimate(s.Text))))
            .Where(p => p.Tokens > 0)
            .OrderByDescending(p => p.Tokens)
            .ToList();
        parts.Add(new ContextPart(SystemPrompt, "系统提示", system.Sum(p => p.Tokens), system));

        // 工具定义每次请求都要整份带上：内置工具一组，每个连接器一组，组里列出每个工具
        var groups = tools
            .Select(t => (Tool: t, Tokens: TokenEstimator.Estimate(ToolRegistry.Definition(t).ToJsonString())))
            .GroupBy(x => x.Tool is McpTool mcp ? ("mcp:" + mcp.ServerId, mcp.ServerName) : ("builtin", "内置工具"))
            .Select(g => new ContextPart(g.Key.Item1, g.Key.Item2, g.Sum(x => x.Tokens),
                g.OrderByDescending(x => x.Tokens).Select(x => ContextPart.Of(x.Tool.Name, x.Tool is McpTool m ? m.Info.Name : x.Tool.Name, x.Tokens)).ToList()))
            .OrderBy(p => p.Key == "builtin" ? 0 : 1).ThenByDescending(p => p.Tokens)
            .ToList();
        parts.Add(new ContextPart(Tools, "工具定义", groups.Sum(p => p.Tokens), groups));

        parts.AddRange(ConversationParts(conversation));

        return new ContextUsageReport
        {
            ContextLength = contextLength,
            ReserveOutput = reserveOutput,
            CompactAt = compactAt,
            Parts = parts,
            LastPromptTokens = lastPromptTokens,
        };
    }

    internal static IEnumerable<ContextPart> ConversationParts(IReadOnlyList<ChatMessage> conversation)
    {
        var messages = conversation.Where(m => m.Role is ChatRole.User or ChatRole.Assistant).Sum(TokenEstimator.Estimate);
        var outputs = conversation.Where(m => m.Role == ChatRole.Tool).Sum(TokenEstimator.Estimate);
        yield return ContextPart.Of(Messages, "对话消息", messages);
        yield return ContextPart.Of(ToolOutputs, "工具结果", outputs);
    }
}
