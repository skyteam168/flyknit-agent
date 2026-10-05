using System.Text.Json.Nodes;

namespace Flyknit.Core.Chat;

/// <summary>把内部消息转换为 OpenAI Chat Completions 格式。</summary>
public static class OpenAiSerializer
{
    /// <summary>单张图片最大 10MB，超过则只以文字形式告知模型。</summary>
    public const long MaxInlineImageBytes = 10 * 1024 * 1024;

    public static JsonArray ToMessages(IEnumerable<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var m in messages)
        {
            array.Add(ToJson(m));
        }
        return array;
    }

    public static JsonObject ToJson(ChatMessage m)
    {
        switch (m.Role)
        {
            case ChatRole.System:
                return new JsonObject { ["role"] = "system", ["content"] = m.Content };

            case ChatRole.User:
                return UserMessage(m);

            case ChatRole.Assistant:
                var obj = new JsonObject { ["role"] = "assistant", ["content"] = m.Content };
                if (m.ToolCalls.Count > 0)
                {
                    var calls = new JsonArray();
                    foreach (var c in m.ToolCalls)
                    {
                        calls.Add(new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                        });
                    }
                    obj["tool_calls"] = calls;
                }
                return obj;

            case ChatRole.Tool:
                return new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = m.ToolCallId,
                    ["content"] = m.Content,
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(m), m.Role, null);
        }
    }

    private static JsonObject UserMessage(ChatMessage m)
    {
        var text = m.Content;
        var files = m.Attachments.Where(a => !a.IsImage || a.Size > MaxInlineImageBytes).ToList();
        if (files.Count > 0)
        {
            // 非图片附件：告诉模型文件位置，由 Agent 通过工具读取
            var lines = files.Select(a => $"- {a.FileName}（{FormatSize(a.Size)}）：{a.LocalPath}");
            text += "\n\n[用户附加的文件]\n" + string.Join("\n", lines);
        }

        var images = m.Attachments.Where(a => a.IsImage && a.Size <= MaxInlineImageBytes && File.Exists(a.LocalPath)).ToList();
        if (images.Count == 0)
        {
            return new JsonObject { ["role"] = "user", ["content"] = text };
        }

        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        foreach (var img in images)
        {
            var b64 = Convert.ToBase64String(File.ReadAllBytes(img.LocalPath));
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{img.Mime};base64,{b64}" },
            });
        }
        return new JsonObject { ["role"] = "user", ["content"] = parts };
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
