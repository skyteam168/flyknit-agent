using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Flyknit.Client.Bridge;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Storage;

namespace Flyknit.Client.Services;

/// <summary>
/// 长任务：每一步立刻存库、记下哪些任务正在跑（程序被关后知道哪些没做完、能接着做）、长任务模式自动续跑。
/// </summary>
public sealed partial class AgentHost
{
    /// <summary>长任务模式下每一轮最多走的步数（本机设置更大时用本机的）。</summary>
    public const int LongModeSteps = 300;

    /// <summary>长任务模式下，一轮走到步数上限后最多自动接着做几次（之后停下等员工）。</summary>
    public const int MaxAutoContinues = 5;

    /// <summary>这一串任务已经自动续跑了几次。员工自己发话时清零。</summary>
    private readonly ConcurrentDictionary<string, int> _autoContinues = new();

    /// <summary>这个对话开着长任务模式，且 IT 没关掉这个功能（只对办事对话有效）。</summary>
    private bool LongModeOn(Conversation? conv) =>
        conv is { LongMode: true, Mode: ConversationMode.Agent }
        && Security.On(Flyknit.Core.Security.SecuritySettings.LongTasks, fallback: true);

    /// <summary>员工在对话里开 / 关长任务模式。IT 关掉了这个功能时打不开（返回 false）。</summary>
    public bool SetLongMode(string conversationId, bool on)
    {
        if (on && !Security.On(Flyknit.Core.Security.SecuritySettings.LongTasks, fallback: true))
        {
            return false;
        }
        Store.SetLongMode(conversationId, on);
        return true;
    }

    /// <summary>给 AgentLoop：每产生一条消息就存一次（同一条后面还会再存最终版本）。</summary>
    private Action<ChatMessage> PersistFor(string conversationId) => m => Store.AddMessages(conversationId, new[] { m });

    private void MarkRunStarted(string conversationId, string origin)
    {
        try
        {
            Store.MarkRunStarted(conversationId, origin);
        }
        catch (Exception ex)
        {
            Log.Warn("记录任务开始失败", ex);
        }
    }

    private void MarkRunEnded(string conversationId)
    {
        try
        {
            Store.MarkRunEnded(conversationId);
        }
        catch (Exception ex)
        {
            Log.Warn("记录任务结束失败", ex);
        }
    }

    /// <summary>
    /// 长任务模式：这一轮走到步数上限停下了，自动排一句「继续」，这一轮收尾后接着做（最多 <see cref="MaxAutoContinues"/> 次）。
    /// 被停止、出错、预算用完、原地打转都不续——那些要人来看。钉钉、手机发来的任务不续（结果要先回给对方）。
    /// </summary>
    private void QueueAutoContinue(Conversation conv, AgentStopReason reason)
    {
        if (reason != AgentStopReason.MaxSteps || !LongModeOn(Store.Get(conv.Id)) || _remoteRuns.ContainsKey(conv.Id))
        {
            return;
        }
        var n = _autoContinues.AddOrUpdate(conv.Id, 1, (_, x) => x + 1);
        if (n > MaxAutoContinues)
        {
            Log.Info($"对话 {conv.Id} 已自动续跑 {MaxAutoContinues} 次，停下等员工");
            return;
        }
        Log.Info($"对话 {conv.Id} 长任务模式：走到步数上限，自动接着做（第 {n}/{MaxAutoContinues} 次）");
        Inbox.Add(conv.Id, $"继续（长任务模式自动接着做，第 {n}/{MaxAutoContinues} 次）。先对照计划看看已经完成了哪些、还差什么，再接着做。", RunInbox.Queue, front: true);
    }

    // ---------- 中断的任务 ----------

    /// <summary>上次没跑完就被关掉的任务（现在正在跑的不算）。</summary>
    public IReadOnlyList<InterruptedRun> InterruptedRuns()
    {
        try
        {
            return Store.InterruptedRuns().Where(r => !IsRunning(r.ConversationId)).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("读取中断的任务失败", ex);
            return Array.Empty<InterruptedRun>();
        }
    }

    /// <summary>
    /// 接着做上次没做完的任务：发一句「继续」，AI 先核对已经做到哪一步（没执行完的那一步会被标出来）再接着做。
    /// </summary>
    public void ResumeInterrupted(string conversationId, string uiLanguage, IConfirmationHandler confirm, IHostEvents events)
    {
        var message = NewUserMessage(null,
            "继续上次没做完的任务（程序上次在执行中被关闭了）。先看看已经完成了哪些步骤、产出的文件在不在、对不对，最后那一步如果没执行完要先确认结果，再接着把任务做完。",
            Array.Empty<Attachment>());
        _autoContinues.TryRemove(conversationId, out _);
        StartRun(conversationId, uiLanguage, confirm, events, _ => message);
        events.Post(new { type = "chat.message", conversationId, message = MessageDto.From(message) });
    }

    /// <summary>员工选了「不用了」：不再提示。</summary>
    public void DismissInterrupted(string conversationId) => MarkRunEnded(conversationId);
}
