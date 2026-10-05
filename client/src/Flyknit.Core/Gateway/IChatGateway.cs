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
}

public sealed class ChatTurn
{
    public string Content { get; init; } = "";
    public string Reasoning { get; init; } = "";
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = Array.Empty<ToolCall>();
    public string? FinishReason { get; init; }
    public string? ModelName { get; init; }
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

public sealed class GatewayException : Exception
{
    public int? StatusCode { get; }

    public GatewayException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
