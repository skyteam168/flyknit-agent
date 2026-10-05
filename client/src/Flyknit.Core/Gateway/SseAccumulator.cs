using System.Text;
using System.Text.Json;
using Flyknit.Core.Chat;

namespace Flyknit.Core.Gateway;

/// <summary>
/// 累积 OpenAI 流式输出（SSE 的 data 行），拼出完整的文本、推理内容和工具调用。
/// 兼容 Qwen / vLLM 的 reasoning_content 字段。
/// </summary>
public sealed class SseAccumulator
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly SortedDictionary<int, PartialCall> _calls = new();
    private readonly IStreamSink? _sink;

    public string? FinishReason { get; private set; }
    public bool Done { get; private set; }

    public SseAccumulator(IStreamSink? sink = null)
    {
        _sink = sink;
    }

    /// <summary>处理一行 SSE 文本。非 data 行会被忽略。</summary>
    public void FeedLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }
        var data = line.AsSpan(5).Trim().ToString();
        if (data.Length == 0)
        {
            return;
        }
        if (data == "[DONE]")
        {
            Done = true;
            return;
        }
        FeedChunk(data);
    }

    public void FeedChunk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
            throw new GatewayException(msg ?? "模型返回错误");
        }
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                FinishReason = fr.GetString();
            }
            // 非流式响应使用 message，流式使用 delta
            if (!choice.TryGetProperty("delta", out var delta) && !choice.TryGetProperty("message", out delta))
            {
                continue;
            }
            if (delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (TryString(delta, "content", out var content) && content.Length > 0)
            {
                _content.Append(content);
                _sink?.OnContent(content);
            }
            if ((TryString(delta, "reasoning_content", out var reasoning) || TryString(delta, "reasoning", out reasoning))
                && reasoning.Length > 0)
            {
                _reasoning.Append(reasoning);
                _sink?.OnReasoning(reasoning);
            }
            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                var position = 0;
                foreach (var call in calls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
                        ? idx.GetInt32()
                        : position;
                    position++;
                    if (!_calls.TryGetValue(index, out var partial))
                    {
                        partial = new PartialCall();
                        _calls[index] = partial;
                    }
                    if (TryString(call, "id", out var id) && id.Length > 0)
                    {
                        partial.Id = id;
                    }
                    if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (TryString(fn, "name", out var name) && name.Length > 0)
                        {
                            partial.Name += name;
                        }
                        if (TryString(fn, "arguments", out var args))
                        {
                            partial.Arguments.Append(args);
                        }
                    }
                }
            }
        }
    }

    public ChatTurn Build(string? modelName = null)
    {
        var calls = _calls.Values
            .Where(c => c.Name.Length > 0)
            .Select((c, i) => new ToolCall(
                string.IsNullOrEmpty(c.Id) ? $"call_{i}_{Guid.NewGuid():N}"[..20] : c.Id,
                c.Name,
                c.Arguments.Length == 0 ? "{}" : c.Arguments.ToString()))
            .ToList();
        return new ChatTurn
        {
            Content = _content.ToString(),
            Reasoning = _reasoning.ToString(),
            ToolCalls = calls,
            FinishReason = FinishReason,
            ModelName = modelName,
        };
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private sealed class PartialCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }
}
