using Flyknit.Core.Chat;

namespace Flyknit.Core.Agent;

/// <summary>
/// 修补保存下来的对话历史。任务每一步都会立刻存库，程序在执行某个工具的半路被关（退出、崩溃、电脑重启）时，
/// 库里会留下「发起了调用、却没有结果」的消息——原样发给模型服务会被拒绝，这里给它们补上说明。
/// </summary>
public static class HistoryRepair
{
    public const string InterruptedResult =
        "未执行完：程序在执行这一步时被关闭了，这一步做没做成不确定。请先检查它的结果（比如文件有没有生成、内容对不对），再决定是否重做。";

    /// <summary>就地补上缺的工具结果（放在那一组工具结果的末尾），返回补了几条。</summary>
    public static int CloseDanglingToolCalls(List<ChatMessage> messages)
    {
        var added = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role != ChatRole.Assistant || m.ToolCalls.Count == 0)
            {
                continue;
            }
            var j = i + 1;
            var answered = new HashSet<string>();
            while (j < messages.Count && messages[j].Role == ChatRole.Tool)
            {
                if (messages[j].ToolCallId is { } id)
                {
                    answered.Add(id);
                }
                j++;
            }
            foreach (var call in m.ToolCalls.Where(c => !answered.Contains(c.Id)))
            {
                messages.Insert(j++, ChatMessage.ToolResult(call, InterruptedResult));
                added++;
            }
            i = j - 1;
        }
        return added;
    }
}
