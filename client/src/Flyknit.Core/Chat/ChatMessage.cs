namespace Flyknit.Core.Chat;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>模型发起的一次工具调用。ArgumentsJson 为模型给出的原始 JSON 字符串。</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed class Attachment
{
    public string FileName { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public string Mime { get; init; } = "application/octet-stream";
    public long Size { get; init; }

    public bool IsImage => Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed class ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ChatRole Role { get; init; }
    public string Content { get; set; } = "";
    public string? Reasoning { get; set; }
    public List<ToolCall> ToolCalls { get; init; } = new();
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public List<Attachment> Attachments { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>用户对回答的评价：1 赞、-1 踩、null 未评价。不发送给模型。</summary>
    public int? Feedback { get; set; }

    /// <summary>助手消息：实际使用的模型与本次调用的 token 用量。不发送给模型。</summary>
    public string? ModelName { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }

    /// <summary>这条回答产出的文件（绝对路径）。界面据此给出打开 / 预览的卡片。</summary>
    public List<string> Outputs { get; set; } = new();

    public static ChatMessage System(string content) => new() { Role = ChatRole.System, Content = content };

    public static ChatMessage User(string content, IEnumerable<Attachment>? attachments = null) => new()
    {
        Role = ChatRole.User,
        Content = content,
        Attachments = attachments?.ToList() ?? new(),
    };

    public static ChatMessage Assistant(string content, IEnumerable<ToolCall>? toolCalls = null, string? reasoning = null) => new()
    {
        Role = ChatRole.Assistant,
        Content = content,
        ToolCalls = toolCalls?.ToList() ?? new(),
        Reasoning = reasoning,
    };

    public static ChatMessage ToolResult(ToolCall call, string content) => new()
    {
        Role = ChatRole.Tool,
        Content = content,
        ToolCallId = call.Id,
        ToolName = call.Name,
    };
}
