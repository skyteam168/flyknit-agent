using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Flyknit.Client.Bridge;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;

namespace Flyknit.Client.Services;

public sealed partial class AgentHost
{
    /// <summary>运行中又发的话：插话（下一步交给 AI）、排队（这一轮结束后再发）。AI 读到之前能改、能撤回。</summary>
    public RunInbox Inbox { get; } = new();

    /// <summary>每个对话上一次运行用的界面语言、确认方式和事件出口：排队的话接着发时沿用。</summary>
    private readonly ConcurrentDictionary<string, (string UiLanguage, IConfirmationHandler Confirm, IHostEvents Events)> _runContext = new();

    /// <summary>
    /// 对话正在运行时又发了一句。mode：steer 插话 / queue 排到本轮之后 / interrupt 停掉这一轮、优先处理这句。
    /// 没在运行返回 false，调用方照常发送。
    /// </summary>
    public bool Interject(string conversationId, string text, string mode, string? messageId)
    {
        if (!IsRunning(conversationId) || text.Trim().Length == 0)
        {
            return false;
        }
        var id = messageId is not null && SafeId.IsMatch(messageId) ? messageId : null;
        if (mode == "interrupt")
        {
            Inbox.Add(conversationId, text, RunInbox.Queue, id, front: true);
            Stop(conversationId); // 这一轮结束时（finally 里）接着发队首这句
        }
        else
        {
            Inbox.Add(conversationId, text, mode == RunInbox.Queue ? RunInbox.Queue : RunInbox.Steer, id);
        }
        // 刚好在加进来之前这一轮结束了：马上接着发，别让它一直挂着
        if (!IsRunning(conversationId))
        {
            ContinuePending(conversationId);
        }
        return true;
    }

    /// <summary>
    /// 从悬浮球插话时这一轮刚好结束了：作为新的一句接着发（用这个对话上一次的界面语言和确认方式）。
    /// 没运行过、对话没了返回 false。
    /// </summary>
    public bool SendAfterRun(string conversationId, string text)
    {
        if (!_runContext.ContainsKey(conversationId) || Store.Get(conversationId) is null || text.Trim().Length == 0)
        {
            return false;
        }
        Inbox.Add(conversationId, text, RunInbox.Queue);
        ContinuePending(conversationId);
        return true;
    }

    /// <summary>给 AgentLoop：每一步取走这个对话的插话，变成用户消息。</summary>
    private Func<IReadOnlyList<ChatMessage>> InterjectionsFor(string conversationId) => () =>
        Inbox.TakeSteer(conversationId).Select(p => NewUserMessage(p.Id, p.Text, Array.Empty<Attachment>())).ToList();

    /// <summary>一轮结束：没读到的插话、排队的话，按顺序接着发下一条（远程任务不在这里续，见 RunChannelMessageAsync）。</summary>
    private void ContinuePending(string conversationId)
    {
        if (IsRunning(conversationId) || !_runContext.TryGetValue(conversationId, out var rc) || Store.Get(conversationId) is null)
        {
            return;
        }
        var next = Inbox.TakeNext(conversationId);
        if (next is null)
        {
            return;
        }
        var message = NewUserMessage(next.Id, next.Text, Array.Empty<Attachment>());
        try
        {
            StartRun(conversationId, rc.UiLanguage, rc.Confirm, rc.Events, _ => message);
            rc.Events.Post(new { type = "chat.message", conversationId, message = MessageDto.From(message) });
        }
        catch (Exception ex)
        {
            // 正好又有一轮开始了之类：放回队首，等那一轮结束再发
            Log.Warn($"接着发排队的话失败：{ex.Message}");
            Inbox.Add(conversationId, next.Text, RunInbox.Queue, next.Id, front: true);
        }
    }

    /// <summary>界面上的待发列表：AI 还没读到的插话和排队的话。</summary>
    public static object PendingDto(IReadOnlyList<PendingInput> items) =>
        items.Select(p => new { id = p.Id, text = p.Text, mode = p.Mode, at = p.At }).ToList();
}
