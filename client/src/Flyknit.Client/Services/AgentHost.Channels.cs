using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Client.Bridge;
using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Storage;

namespace Flyknit.Client.Services;

public sealed partial class AgentHost
{
    /// <summary>正在执行钉钉消息的对话：这一轮不给提问工具（见 AskUserFor）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _remoteRuns = new();

    /// <summary>员工在设置里生成了绑定码、解了绑：钉钉中继马上重新看要不要开始等消息。</summary>
    /// <remarks>参数为 true 表示刚生成了绑定码：员工马上要去钉钉里绑定，先开始等消息。</remarks>
    public event Action<bool>? ChannelsChanged;

    public void NotifyChannelsChanged(bool expectBinding = false) => ChannelsChanged?.Invoke(expectBinding);

    /// <summary>
    /// 执行一条从钉钉 / 企业微信发来的消息。同一个账号的消息接着同一个对话聊（对话在侧栏里能看到、能接着在电脑上问），
    /// 那个对话被删了、正在跑别的任务时新开一个。
    ///
    /// 需要员工确认的操作一律不远程放行：人不在电脑前，钉钉里回一句「同意」也说不清看没看明白要做什么。
    /// 被拦下的操作写进回复里，让员工回到电脑上处理。
    /// </summary>
    public static string ChannelName(string channel) => channel == "wecom" ? "企业微信" : "钉钉";

    public async Task<InstructionOutcome> RunChannelMessageAsync(string text, string sender, string conversationId, CancellationToken ct, string channel = "dingtalk")
    {
        var conv = conversationId.Length > 0 ? Store.Get(conversationId) : null;
        if (conv is not null && (conv.DeletedAt is not null || conv.Mode != ConversationMode.Agent || IsRunning(conv.Id)))
        {
            conv = null;
        }
        if (conv is null)
        {
            var title = $"{ChannelName(channel)} · {(sender.Length > 0 ? sender : "远程")}";
            conv = Store.Create(ConversationMode.Agent, title);
            Store.Rename(conv.Id, title); // 固定标题，不让 AI 自动总结覆盖
        }
        var confirm = new RemoteDecline();
        _remoteRuns[conv.Id] = 0;
        try
        {
            return await RunRemoteAsync(conv, text, confirm, ct);
        }
        finally
        {
            _remoteRuns.TryRemove(conv.Id, out _);
        }
    }

    private async Task<InstructionOutcome> RunRemoteAsync(Conversation conv, string text, RemoteDecline confirm, CancellationToken ct)
    {

        // 界面还没就绪（刚开机、窗口还没加载）时静默执行，照样受策略约束、写审计
        if (Events is not { } events)
        {
            var headless = await RunInstructionHeadlessAsync(conv, text, confirm, ct);
            return WithDeclined(headless, confirm);
        }

        var tcs = new TaskCompletionSource<RunFinishedInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(RunFinishedInfo info)
        {
            if (info.ConversationId == conv.Id)
            {
                tcs.TrySetResult(info);
            }
        }
        RunFinished += OnFinished;
        using var reg = ct.Register(() => Stop(conv.Id));
        try
        {
            // 对话出现在侧栏里、过程实时可见，但不抢员工正在看的页面
            events.Post(new { type = "conversation.updated", conversation = ConversationDto.From(conv) });
            Send(conv.Id, text, Array.Empty<Attachment>(), null, UiLanguage, confirm, events);
            var info = await tcs.Task;
            Log.Info($"钉钉会话 {conv.Id} 结束：{info.StopReason}");
            return WithDeclined(ToOutcome(conv.Id, info.StopReason, info.Answer), confirm);
        }
        catch (Exception ex)
        {
            Log.Error($"执行钉钉消息失败（会话 {conv.Id}）", ex);
            return new InstructionOutcome(false, "", ex.Message, conv.Id);
        }
        finally
        {
            RunFinished -= OnFinished;
        }
    }

    private static InstructionOutcome WithDeclined(InstructionOutcome outcome, RemoteDecline confirm)
    {
        if (confirm.Declined.Count == 0)
        {
            return outcome;
        }
        var list = string.Join("\n", confirm.Declined.Distinct().Take(5).Select(d => $"- {d}"));
        var note = $"\n\n**以下操作需要在电脑上确认，远程没有执行：**\n{list}\n\n回到电脑上打开这个对话，让我再做一次即可。";
        return outcome with { Answer = outcome.Answer + note };
    }

    /// <summary>远程执行时的确认：一律拒绝，并记下被拒的是什么。</summary>
    private sealed class RemoteDecline : IConfirmationHandler
    {
        private readonly object _lock = new();
        public List<string> Declined { get; } = new();

        public Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            lock (_lock)
            {
                Declined.Add(request.Summary.Length > 80 ? request.Summary[..80] + "…" : request.Summary);
            }
            return Task.FromResult(ConfirmChoice.Reject);
        }
    }
}
