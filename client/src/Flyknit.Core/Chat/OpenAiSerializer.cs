using System.Text.Json.Nodes;

namespace Flyknit.Core.Chat;

/// <summary>把内部消息转换为 OpenAI Chat Completions 格式。</summary>
public static class OpenAiSerializer
{
    /// <summary>单张图片最大 10MB，超过则只以文字形式告知模型。</summary>
    public const long MaxInlineImageBytes = 10 * 1024 * 1024;

    /// <summary>
    /// 转换整段对话。思考内容（reasoning_content）怎么回传不按厂商写死，只看数据：
    /// <list type="bullet">
    /// <item>模型从没返回过思考内容（大多数非推理模型）：一个字段都不加，请求和以前完全一样。</item>
    /// <item>返回过：带工具调用的 assistant 消息都带上 reasoning_content，这一条没有思考的填空字符串。
    /// 推理模型在多步工具调用里要接着上一步的思路往下想，有的厂商（如 DeepSeek 思考模式）缺了这个字段直接报 400；
    /// 不需要的厂商会忽略它。最终回答（不带工具调用）的思考不回传：下一轮用不上，白占上下文。</item>
    /// </list>
    /// 个别不接受这个字段的上游，由管理员在后台把模型的「思考内容回传」设为不回传，网关转发前去掉——客户端不需要知道是哪家。
    /// </summary>
    /// <param name="toolImages">
    /// 模型支持识图时为 true：工具返回的图片（截图等）跟在那一串工具结果后面，作为一条用户消息发过去——
    /// 大多数接口的 tool 消息只收文字。只发员工最后一句话之后的、最近 <see cref="MaxToolImages"/> 张：更早的图片模型已经看过、说过了，
    /// 每次都带会很费 token。为 false 时只有文字（里面写着图片存在哪）。
    /// </param>
    public static JsonArray ToMessages(IEnumerable<ChatMessage> messages, bool toolImages = false)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var echo = list.Any(m => m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Reasoning));
        var images = toolImages ? RecentToolImages(list) : new HashSet<Attachment>();
        var array = new JsonArray();
        var pending = new List<Attachment>();
        for (var i = 0; i < list.Count; i++)
        {
            var m = list[i];
            var json = ToJson(m);
            if (echo && m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0)
            {
                json["reasoning_content"] = m.Reasoning ?? "";
            }
            array.Add(json);
            if (m.Role == ChatRole.Tool)
            {
                pending.AddRange(m.Attachments.Where(images.Contains));
                // 一串工具结果结束了（下一条不是工具结果）才插：工具结果中间插别的消息，接口会报错
                if (pending.Count > 0 && (i + 1 == list.Count || list[i + 1].Role != ChatRole.Tool))
                {
                    array.Add(ToolImagesMessage(pending));
                    pending.Clear();
                }
            }
        }
        return array;
    }

    /// <summary>一次请求最多带几张工具返回的图片。</summary>
    public const int MaxToolImages = 4;

    public const string ToolImagesNote = "（以下是上面工具返回的图片，不是员工发的）";

    private static HashSet<Attachment> RecentToolImages(IReadOnlyList<ChatMessage> list)
    {
        var lastUser = -1;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.User)
            {
                lastUser = i;
                break;
            }
        }
        return list.Skip(lastUser + 1)
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Attachments)
            .Where(a => a.IsImage && a.Size <= MaxInlineImageBytes && File.Exists(a.LocalPath))
            .TakeLast(MaxToolImages)
            .ToHashSet();
    }

    private static JsonObject ToolImagesMessage(List<Attachment> images)
    {
        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = ToolImagesNote } };
        foreach (var img in images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{img.Mime};base64,{Convert.ToBase64String(File.ReadAllBytes(img.LocalPath))}" },
            });
        }
        return new JsonObject { ["role"] = "user", ["content"] = parts };
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
        // 粘贴的长文本放在员工打的字前面（员工常说「以上是……」）；文件读不到就当普通附件
        var pasted = m.Attachments.Where(a => a.Pasted).Select(a => (a, block: PastedText.Block(a))).ToList();
        var blocks = pasted.Where(p => p.block is not null).Select(p => p.block!).ToList();
        var text = blocks.Count > 0 ? string.Join("\n\n", blocks) + (m.Content.Length > 0 ? "\n\n" + m.Content : "") : m.Content;
        var inlined = pasted.Where(p => p.block is not null).Select(p => p.a).ToHashSet();
        var files = m.Attachments.Where(a => !inlined.Contains(a) && (!a.IsImage || a.Size > MaxInlineImageBytes)).ToList();
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
