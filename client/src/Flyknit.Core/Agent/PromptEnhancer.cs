using System.Text.RegularExpressions;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Agent;

/// <summary>增强提示词时知道的上下文：在哪种模式、是不是专家对话。</summary>
public sealed record PromptEnhanceInput(string Text, ConversationMode Mode, string UiLanguage, string? ExpertName = null, string? ExpertDescription = null);

/// <summary>
/// 输入框里的「增强提示词」：把员工随手写的一句话改写得更清楚、更好执行。
/// 只改写，不回答；保持原来的语言和意思，不编造员工没说的事实（不知道的写成让 AI 先确认）。
/// 原文由界面保留，随时可以恢复。
/// </summary>
public sealed class PromptEnhancer
{
    /// <summary>太长的不增强：已经写得很详细了，改写反而容易丢东西。</summary>
    public const int MaxInputChars = 4000;

    private readonly IChatGateway _gateway;

    public PromptEnhancer(IChatGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<string> EnhanceAsync(PromptEnhanceInput input, CancellationToken ct)
    {
        var text = input.Text.Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("没有要增强的内容");
        }
        if (text.Length > MaxInputChars)
        {
            throw new ArgumentException("内容已经很长了，不需要增强");
        }
        var turn = await _gateway.CompleteAsync(new ChatRequest
        {
            Scene = Scenes.Chat,
            Stream = false,
            MaxTokens = 1200,
            Temperature = 0.3,
            // Qwen3 等推理模型在非流式请求中必须关闭思考，否则会报错
            ExtraBody = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["enable_thinking"] = false },
            Messages = new[]
            {
                ChatMessage.System(SystemPrompt(input)),
                ChatMessage.User($"<原文>\n{text}\n</原文>"),
            },
        }, null, ct);
        var result = Clean(turn.Content);
        if (result.Length == 0)
        {
            throw new InvalidOperationException("AI 没有给出改写结果，请再试一次");
        }
        return result;
    }

    internal static string SystemPrompt(PromptEnhanceInput input)
    {
        var mode = input.Mode switch
        {
            ConversationMode.Agent => """
                这句话是交给能操作电脑的 AI 助手去「办事」的（读写文件、处理 Excel/Word/PPT、执行命令、调用公司系统）。
                改写时补齐办事需要的要素：要处理的对象（文件、数据、系统）、要做的步骤或要求、结果的形式（生成什么文件、什么格式、放在哪里）、限制条件（不要改动什么、截止范围）。
                """,
            ConversationMode.Translate => "这句话是要翻译的原文，只修正明显的错别字和断句，不要改变意思、不要加内容。",
            _ => """
                这句话是问 AI 助手的问题或要它写的东西。
                改写时讲清楚背景、要回答什么、希望的形式（要点 / 表格 / 一段话）、篇幅和语气。
                """,
        };
        var expert = string.IsNullOrWhiteSpace(input.ExpertName)
            ? ""
            : $"\n这句话是发给「{input.ExpertName}」的。{(string.IsNullOrWhiteSpace(input.ExpertDescription) ? "" : "这位专家的职责：\n" + Clip(input.ExpertDescription!, 600) + "\n")}按这位专家的业务场景来补充要素。\n";
        return $"""
            你是提示词优化助手。员工在输入框里写了一句话，你把它改写得更清楚、更具体、更容易被 AI 准确执行。
            {mode}{expert}
            规则：
            1. 只输出改写后的内容本身，不要解释、不要加「改写后：」之类的前缀，不要用引号或代码块包起来。
            2. 用原文的语言写（原文是越南语就写越南语，中文就写中文）。
            3. 保持原意，不要回答问题，不要替员工做决定。
            4. 不要编造原文没有的具体事实（文件名、数字、日期、人名）。缺少的关键信息写成「如果不确定 X，请先问我」。
            5. 原文已经很清楚时只做小幅润色，不要硬加内容。篇幅一般不超过原文的三倍。
            6. 内容较多时用简短的分条，便于阅读。
            """;
    }

    private static readonly Regex Prefix = new(@"^(改写后|优化后|增强后|提示词|Prompt|Câu lệnh)\s*[:：]\s*", RegexOptions.IgnoreCase);

    internal static string Clean(string raw)
    {
        var text = raw;
        var thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
        {
            text = text[(thinkEnd + 8)..];
        }
        text = text.Trim();
        // 整段包在代码块里
        if (text.StartsWith("```"))
        {
            var firstLine = text.IndexOf('\n');
            text = firstLine < 0 ? "" : text[(firstLine + 1)..];
            if (text.TrimEnd().EndsWith("```"))
            {
                text = text.TrimEnd()[..^3];
            }
            text = text.Trim();
        }
        text = text.Replace("<原文>", "").Replace("</原文>", "").Trim();
        text = Prefix.Replace(text, "").Trim();
        // 整段包在引号里
        if (text.Length >= 2 && ((text[0] == '“' && text[^1] == '”') || (text[0] == '"' && text[^1] == '"') || (text[0] == '「' && text[^1] == '」')))
        {
            text = text[1..^1].Trim();
        }
        return text;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
