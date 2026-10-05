using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Agent;

/// <summary>根据首轮问答生成会话标题。</summary>
public sealed class TitleGenerator
{
    private readonly IChatGateway _gateway;

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
                Messages = new[]
                {
                    ChatMessage.System(PromptBuilder.TitlePrompt(uiLanguage)),
                    ChatMessage.User(excerpt),
                },
            }, null, ct);
            return Clean(turn.Content);
        }
        catch (GatewayException)
        {
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
