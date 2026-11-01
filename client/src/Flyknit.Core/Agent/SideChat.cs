using System.Text;
using Flyknit.Core.Chat;

namespace Flyknit.Core.Agent;

/// <summary>
/// 侧聊（顺便问一句）：任务在跑或做完了，员工想问一句「刚才那个表是哪来的？」，又不想打断、也不想把问答写进任务对话。
/// 这里把任务对话整理成一段只读的记录交给模型，只回答，不调工具，答完不写回任务对话。
/// </summary>
public static class SideChat
{
    /// <summary>带多少条任务对话进去（太早的对任务现状没用，还占地方）。</summary>
    public const int MaxMessages = 40;

    public const int MaxCharsPerMessage = 1500;
    public const int MaxToolChars = 400;

    public const string SystemPrompt = """
        你是 FlyknitBuddy。员工正在一个任务对话里让你办事，现在在旁边「顺便问一句」。
        下面「任务对话记录」是那个任务到目前为止的过程（只读）。请根据记录回答员工的问题：
        - 只回答，不要说你要去执行什么操作，也不要假装已经做了什么；需要动手的事，请员工回到任务里说。
        - 记录里没有的信息就直说不知道，不要编。
        - 简洁，用员工提问的语言回答。
        - 这段问答不会写进任务对话，也不会影响正在进行的任务。
        """;

    /// <summary>组装发给模型的消息：系统说明 + 任务对话记录 + 之前几轮侧聊 + 这次的问题。</summary>
    public static List<ChatMessage> BuildMessages(IReadOnlyList<ChatMessage> conversation, IReadOnlyList<ChatMessage> sideHistory, string question)
    {
        var record = new StringBuilder("【任务对话记录】\n");
        var recent = conversation.Where(m => m.Role != ChatRole.System).TakeLast(MaxMessages).ToList();
        if (conversation.Count(m => m.Role != ChatRole.System) > recent.Count)
        {
            record.AppendLine("（更早的部分省略）");
        }
        foreach (var m in recent)
        {
            switch (m.Role)
            {
                case ChatRole.User:
                    record.Append("员工：").AppendLine(Clip(m.Content, MaxCharsPerMessage));
                    break;
                case ChatRole.Assistant:
                    if (m.Content.Trim().Length > 0)
                    {
                        record.Append("AI：").AppendLine(Clip(m.Content, MaxCharsPerMessage));
                    }
                    foreach (var call in m.ToolCalls)
                    {
                        record.Append("AI 调用工具 ").Append(call.Name).Append('：').AppendLine(Clip(call.ArgumentsJson, 300));
                    }
                    break;
                case ChatRole.Tool:
                    record.Append("工具结果（").Append(m.ToolName ?? "").Append("）：").AppendLine(Clip(m.Content, MaxToolChars));
                    break;
            }
        }
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt + "\n\n" + record),
        };
        foreach (var m in sideHistory.Where(m => m.Role is ChatRole.User or ChatRole.Assistant).TakeLast(10))
        {
            messages.Add(m.Role == ChatRole.User ? ChatMessage.User(Clip(m.Content, MaxCharsPerMessage)) : ChatMessage.Assistant(Clip(m.Content, MaxCharsPerMessage), Array.Empty<ToolCall>(), null));
        }
        messages.Add(ChatMessage.User(question.Trim()));
        return messages;
    }

    private static string Clip(string text, int max)
    {
        var t = text.Replace("\r", "").Trim();
        return t.Length <= max ? t : t[..max] + "…（后略）";
    }
}
