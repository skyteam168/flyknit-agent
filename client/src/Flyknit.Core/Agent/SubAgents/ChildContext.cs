using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Agent.SubAgents;

/// <summary>
/// 专家、子代理（以及团队成员）这类子任务的上下文管理。
///
/// 以前子任务的循环不带上下文管理：读进来的文件、命令输出一直留在上下文里，每调一次模型都整段重发，
/// 用量按「每次的输入 + 输出」累计——上下文 6 万时第二步就算 12 万、第三步 18 万……
/// 一个读代码的专家分到 40 万，四五步就「用完了预算」，看起来像刚派出去就停了。
///
/// 现在按分到的额度给子任务定一个「工作窗口」：额度的六分之一（至少 2.4 万，最多是模型的上下文长度）。
/// 超过窗口的四分之一就开始把较早的工具输出换成摘要，快满时压缩成摘要，每一步发出去的上下文不再一直涨，
/// 同样的额度能走的步数多好几倍。
/// </summary>
public static class ChildContext
{
    public const int MinWindow = 24_000;

    /// <summary>工作窗口（token）：额度的 1/6，夹在 [2.4 万, 模型上下文长度] 之间。</summary>
    public static int WindowFor(long cap, int modelContextLength = 0)
    {
        var max = modelContextLength > 0 ? modelContextLength : new ContextOptions().DefaultContextLength;
        var window = cap <= 0 ? max : (long)(cap / 6);
        return (int)Math.Clamp(window, Math.Min(MinWindow, max), max);
    }

    public static ContextManager Create(IChatGateway gateway, string systemPrompt, long cap, int modelContextLength = 0) =>
        new(gateway, systemPrompt, null, WindowFor(cap, modelContextLength), new ContextOptions
        {
            // 子任务一般就是查一件事：最近 3 次工具结果留原文，更早的只留开头
            KeepRecentToolResults = 3,
        });

    /// <summary>
    /// fork 子代理带过去的主对话历史：太长时只留系统提示词和最近的一段（不超过工作窗口的一半），
    /// 从一条用户消息开始切，不把工具调用和它的结果拆开。第一步就发十几万 token 的事不再有。
    /// </summary>
    public static List<ChatMessage> TrimFork(IReadOnlyList<ChatMessage> history, long cap, int modelContextLength = 0)
    {
        var limit = WindowFor(cap, modelContextLength) / 2;
        var system = history.Where(m => m.Role == ChatRole.System).Take(1).ToList();
        var rest = history.Where(m => m.Role != ChatRole.System).ToList();
        if (TokenEstimator.Estimate(rest) <= limit)
        {
            return system.Concat(rest).ToList();
        }
        var start = rest.Count;
        long used = 0;
        for (var i = rest.Count - 1; i >= 0; i--)
        {
            used += TokenEstimator.Estimate(rest[i]);
            if (used > limit)
            {
                break;
            }
            start = i;
        }
        // 往后找到第一条用户消息再开始，保证工具调用和结果成对
        while (start < rest.Count && rest[start].Role != ChatRole.User)
        {
            start++;
        }
        return system.Concat(rest.Skip(start)).ToList();
    }
}
