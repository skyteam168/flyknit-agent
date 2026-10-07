using System.Text.Json.Nodes;
using Flyknit.Core.Chat;

namespace Flyknit.Core.Gateway;

/// <summary>场景名，服务端据此路由到具体模型。</summary>
public static class Scenes
{
    public const string Chat = "chat";
    public const string Agent = "agent";
    public const string Translate = "translate";
    public const string Title = "title";
    public const string Vision = "vision";
}

public sealed class ChatRequest
{
    public string Scene { get; init; } = Scenes.Chat;
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public JsonArray? Tools { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public bool Stream { get; init; } = true;

    /// <summary>用户在输入框选择的模型（服务端模型 ID），为空时按场景路由。</summary>
    public int? ModelId { get; init; }

    /// <summary>会话 id。服务端据此把一轮一轮归到一次对话下，否则归档是散的。</summary>
    public string? ConversationId { get; init; }

    /// <summary>透传给模型的额外参数，例如 enable_thinking。</summary>
    public IReadOnlyDictionary<string, JsonNode?>? ExtraBody { get; init; }
}

public sealed class ChatTurn
{
    public string Content { get; init; } = "";
    public string Reasoning { get; init; } = "";
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = Array.Empty<ToolCall>();
    public string? FinishReason { get; init; }
    public string? ModelName { get; init; }

    /// <summary>本次调用的 token 用量（模型未返回时为 null）。</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>实际使用模型的上下文长度（服务端通过响应头告知，未知时为 0）。</summary>
    public int ContextLength { get; init; }
}

public sealed record TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int Total => PromptTokens + CompletionTokens;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.PromptTokens + b.PromptTokens, a.CompletionTokens + b.CompletionTokens);
}

/// <summary>流式输出回调。</summary>
public interface IStreamSink
{
    void OnContent(string delta);
    void OnReasoning(string delta);
}

public interface IChatGateway
{
    Task<ChatTurn> CompleteAsync(ChatRequest request, IStreamSink? sink, CancellationToken ct);
}

/// <summary>一次向量化的结果：用的哪个模型、每段文字的向量（顺序和输入一致）。</summary>
public sealed record EmbeddingResult(string Model, IReadOnlyList<float[]> Vectors);

/// <summary>
/// 文字转向量（服务端的 embedding 场景）。语义检索用；服务端没配向量模型时返回 503，调用方按“不可用”处理，退回字面匹配。
/// </summary>
public interface IEmbeddingGateway
{
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

public sealed class GatewayException : Exception
{
    public int? StatusCode { get; }

    public GatewayException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
