using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Agent;

/// <summary>根据首轮问答生成会话标题。</summary>
public sealed class TitleGenerator
{
    private readonly IChatGateway _gateway;

    /// <summary>最近一次生成失败的原因（写日志用）。</summary>
    public string? LastError { get; private set; }

    public TitleGenerator(IChatGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<string?> GenerateAsync(string userText, string assistantText, string uiLanguage, CancellationToken ct)
    {
        var excerpt = $"User: {Clip(userText, 800)}\nAssistant: {Clip(assistantText, 800)}";
        try
        {
            var turn = await _gateway.CompleteAsync(new ChatRequest
            {
                Scene = Scenes.Title,
                Stream = false,
                MaxTokens = 40,
                Temperature = 0.3,
                // Qwen3 等推理模型在非流式请求中必须关闭思考，否则会报错
                ExtraBody = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["enable_thinking"] = false },
                Messages = new[]
                {
                    ChatMessage.System(PromptBuilder.TitlePrompt(uiLanguage)),
                    ChatMessage.User(excerpt),
                },
            }, null, ct);
            return Clean(turn.Content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            return null;
        }
    }

    internal static string? Clean(string raw)
    {
        // 去掉推理模型可能输出的 <think> 段
        var text = raw;
        var thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
        {
            text = text[(thinkEnd + 8)..];
        }
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = line.Trim().Trim('"', '“', '”', '「', '」', '《', '》', '\'', '*', '#').Trim();
        if (line.StartsWith("标题：") || line.StartsWith("标题:"))
        {
            line = line[3..].Trim();
        }
        line = line.TrimEnd('。', '.', '!', '！', '?', '？');
        if (line.Length == 0)
        {
            return null;
        }
        return line.Length > 40 ? line[..40] : line;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
