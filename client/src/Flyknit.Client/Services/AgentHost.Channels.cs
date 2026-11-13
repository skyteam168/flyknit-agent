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
    public static string ChannelName(string channel) => channel switch { "wecom" => "企业微信", "mobile" => "手机", "event" => "事件触发", _ => "钉钉" };

    /// <param name="messageId">服务端的消息 ID。手机发来的任务用它回报进度、请求在手机上确认。</param>
    /// <param name="permission">事件触发：服务端给的权限上限（空 = 不另外封顶）。</param>
    /// <param name="rehearsal">事件触发：只预演。</param>
    public async Task<InstructionOutcome> RunChannelMessageAsync(string text, string sender, string conversationId, CancellationToken ct, string channel = "dingtalk", int messageId = 0,
        string permission = "", bool rehearsal = false)
    {
        var mobile = channel == "mobile" && messageId > 0;
        using var source = Flyknit.Core.Gateway.UsageSource.Use(mobile ? Flyknit.Core.Gateway.UsageSource.Mobile : Flyknit.Core.Gateway.UsageSource.Channel);
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
        // 钉钉、企业微信：需要确认的一律拒绝。
        // 手机：优先交给发起的手机批准；同时若电脑界面在，也弹出确认（谁先答用谁），避免双方都不弹、一直转圈。
        var progress = mobile ? new RemoteProgress(Server, messageId) : null;
        var confirm = new RemoteDecline(mobile ? Server : null, messageId, mobile ? Confirm : null, mobile ? Events : null, progress);
        // 授权记录：谁让做、做到哪。服务端核对不通过（这条消息不是发给这台电脑的）就不执行
        var origin = channel is "mobile" or "wecom" or "event" ? channel : "dingtalk";
        var reference = messageId > 0 ? messageId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        var (grant, refused) = await IssueGrantAsync(conv, origin, reference, sender, text, ct,
            permission.Length > 0 ? Flyknit.Core.Security.PermissionModes.Parse(permission) : null);
        if (refused is not null)
        {
            return new InstructionOutcome(false, "", refused, conv.Id);
        }
        // 任务账本：一条远程消息是一件新的事
        ExpectTask(origin, conv, Store.Get(conv.Id)?.Title ?? conv.Title, reference,
            $"{ChannelName(channel)}：{(sender.Length > 0 ? sender : "远程")}", grant, sender);
        if (rehearsal)
        {
            MarkRehearsal(conv.Id); // 只读调研、列出会做的修改（见 PlanMode.RehearsalGuide）
        }
        _remoteRuns[conv.Id] = 0;
        // 手机发来的：执行期间顺便收手机上补充的话（插话）和打断
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inputs = mobile ? Task.Run(() => RelayPhoneInputsAsync(conv.Id, messageId, listen.Token)) : Task.CompletedTask;
        try
        {
            var outcome = await RunRemoteAsync(conv, text, confirm, ct, progress);
            // 结束前一刻才到、AI 没读到的补充：写进结果里，手机上照着再说一遍就行
            var missed = Inbox.TakeSteer(conv.Id);
            if (missed.Count > 0)
            {
                var list = string.Join("\n", missed.Select(m => $"- {m.Text}"));
                outcome = outcome with { Answer = outcome.Answer + $"\n\n**这几句补充到的时候这件事已经办完了，没有处理：**\n{list}" };
            }
            return outcome;
        }
        finally
        {
            listen.Cancel();
            try
            {
                await inputs;
            }
            catch (OperationCanceledException)
            {
            }
            // 员工在电脑上给这个对话排了话（排到本轮之后）：用电脑界面的确认方式接着发，不再走远程的那一套。
            // 这一轮的 RunAsync 可能还没收尾：那就由它的 finally 接着发（那时这里已经不算远程了）
            if (Events is { } ui && Confirm is { } uiConfirm)
            {
                _runContext[conv.Id] = (UiLanguage, uiConfirm, ui);
            }
            _remoteRuns.TryRemove(conv.Id, out _);
            EndGrant(conv.Id, grant);
            ContinuePending(conv.Id);
        }
    }

    /// <summary>
    /// 长轮询服务端：手机上补充的话放进这个对话的插话箱（AI 下一步就看到；还没开始跑也没关系，开始后第一步就取走），
    /// 手机要求打断就停掉这一轮（那句优先的话服务端已经作为下一件事排上）。
    /// </summary>
    private async Task RelayPhoneInputsAsync(string conversationId, int messageId, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var got = await Server.PollChannelInputsAsync(messageId, 25, ct);
                failures = 0;
                foreach (var t in got.Inputs.Where(t => t.Trim().Length > 0))
                {
                    Inbox.Add(conversationId, t, RunInbox.Steer);
                }
                if (got.Stop)
                {
                    Log.Info($"手机要求打断任务 #{messageId}（会话 {conversationId}）");
                    Stop(conversationId);
                    return;
                }
                if (got.Done)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 服务端旧版本没有这个接口（404）之类：别刷日志，退避后再试，最多 5 次
                if (++failures >= 5)
                {
                    Log.Warn($"收取手机补充的话失败，不再收取：{ex.Message}");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(3 * failures), ct);
            }
        }
    }

    private async Task<InstructionOutcome> RunRemoteAsync(Conversation conv, string text, RemoteDecline confirm, CancellationToken ct, RemoteProgress? progress = null)
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
            IHostEvents sink = progress is null ? events : new ProgressTee(events, conv.Id, progress);
            Send(conv.Id, text, Array.Empty<Attachment>(), null, UiLanguage, confirm, sink);
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

    /// <summary>
    /// 远程执行时的确认。钉钉、企业微信：一律拒绝。
    /// 手机：后台允许时推到发起的手机；电脑界面若在，同时弹出本地确认（先答先生效）。
    /// 手机审批未开启或请求失败时，退回电脑确认；两边都没有才拒绝并记入回复。
    /// 批准只放行这一次（不建规则）；被安全策略「阻止」的命令根本不会走到这里。
    /// </summary>
    private sealed class RemoteDecline : IConfirmationHandler
    {
        private const int Wait = 25;
        private readonly object _lock = new();
        private readonly Flyknit.Core.Gateway.FlyknitServerClient? _server;
        private readonly int _messageId;
        private readonly IConfirmationHandler? _local;
        private readonly IHostEvents? _events;
        private readonly RemoteProgress? _progress;

        public RemoteDecline(
            Flyknit.Core.Gateway.FlyknitServerClient? server = null,
            int messageId = 0,
            IConfirmationHandler? local = null,
            IHostEvents? events = null,
            RemoteProgress? progress = null)
        {
            _server = server;
            _messageId = messageId;
            _local = local;
            _events = events;
            _progress = progress;
        }

        public List<string> Declined { get; } = new();

        public async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            if (_server is not null && _messageId > 0)
            {
                try
                {
                    var detail = request.Decision.Reason.Length > 0 ? request.Decision.Reason : request.Rationale;
                    var ticket = await _server.RequestChannelApprovalAsync(_messageId, request.Summary, detail, "confirm", ct);
                    if (ticket.Enabled)
                    {
                        var tip = request.Summary.Length > 80 ? request.Summary[..80] + "…" : request.Summary;
                        _progress?.Report("text", "等待手机确认：" + tip);
                        // 切到这个对话，否则确认条只绑「当前对话」，员工看着别的页/首页时本机永远不弹
                        _events?.Post(new { type = "app.openConversation", conversationId = request.ConversationId });
                        _events?.Post(new { type = "chat.notice", conversationId = request.ConversationId, text = "请在手机或本机确认：" + tip });

                        using var phoneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var phoneTask = WaitPhoneStatusAsync(ticket, tip, phoneCts.Token);
                        if (_local is not null)
                        {
                            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var localTask = _local.ConfirmAsync(request, localCts.Token);
                            var done = await Task.WhenAny(phoneTask, localTask);
                            if (done == phoneTask)
                            {
                                var status = await phoneTask;
                                if (status == "approved")
                                {
                                    localCts.Cancel();
                                    return ConfirmChoice.AllowOnce;
                                }
                                if (status == "rejected")
                                {
                                    // 手机上明确点了拒绝
                                    localCts.Cancel();
                                    return Decline(request.Summary);
                                }
                                // expired / 取消：本机确认条继续等，不能静默当拒绝
                                Log.Info($"手机确认超时或未送达，改等本机确认：{tip}（{status}）");
                                var localAfter = await localTask;
                                return localAfter != ConfirmChoice.Reject ? localAfter : Decline(request.Summary);
                            }

                            var localChoice = await localTask;
                            phoneCts.Cancel();
                            // 本机先答：关掉手机上还挂着的审批卡，避免员工随后在手机上点到过期确认
                            await CancelPhoneApprovalAsync(ticket.Id);
                            if (localChoice != ConfirmChoice.Reject)
                            {
                                return localChoice;
                            }
                            return Decline(request.Summary);
                        }

                        var phoneOnly = await WaitPhoneStatusAsync(ticket, tip, ct);
                        if (phoneOnly == "approved")
                        {
                            return ConfirmChoice.AllowOnce;
                        }
                        return Decline(request.Summary);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn("请求手机确认失败，改走电脑确认", ex);
                }
            }

            if (_local is not null)
            {
                _events?.Post(new { type = "app.openConversation", conversationId = request.ConversationId });
                return await _local.ConfirmAsync(request, ct);
            }
            return Decline(request.Summary);
        }

        private ConfirmChoice Decline(string summary)
        {
            lock (_lock)
            {
                Declined.Add(summary.Length > 80 ? summary[..80] + "…" : summary);
            }
            return ConfirmChoice.Reject;
        }

        /// <returns>approved / rejected / expired / cancelled</returns>
        private async Task<string> WaitPhoneStatusAsync(Flyknit.Core.Gateway.ChannelApprovalTicket ticket, string tip, CancellationToken ct)
        {
            try
            {
                var until = DateTime.UtcNow.AddSeconds(Math.Max(60, ticket.TimeoutSec) + 30);
                while (DateTime.UtcNow < until)
                {
                    ct.ThrowIfCancellationRequested();
                    var status = await _server!.WaitChannelApprovalAsync(ticket.Id, Wait, ct);
                    if (status != "pending")
                    {
                        Log.Info($"手机确认结果：{tip}（{status}）");
                        return status.Length > 0 ? status : "expired";
                    }
                }
                return "expired";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return "cancelled";
            }
        }

        private async Task CancelPhoneApprovalAsync(string approvalId)
        {
            if (_server is null || approvalId.Length == 0)
            {
                return;
            }
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _server.CancelChannelApprovalAsync(approvalId, cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warn($"取消手机审批失败（{approvalId}）", ex);
            }
        }
    }

    /// <summary>
    /// 手机发来的任务：执行到哪一步实时报给服务端，手机 App 上显示。
    /// 按顺序在后台发，发不出去就算了（不影响任务本身）。
    /// </summary>
    private sealed class RemoteProgress
    {
        private readonly Flyknit.Core.Gateway.FlyknitServerClient _server;
        private readonly int _messageId;
        private Task _tail = Task.CompletedTask;
        private readonly object _lock = new();

        public RemoteProgress(Flyknit.Core.Gateway.FlyknitServerClient server, int messageId)
        {
            _server = server;
            _messageId = messageId;
        }

        public void Report(string kind, string text)
        {
            lock (_lock)
            {
                _tail = _tail.ContinueWith(async _ =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await _server.ReportChannelEventAsync(_messageId, kind, text, cts.Token);
                    }
                    catch (Exception)
                    {
                        // 进度丢一条无所谓
                    }
                }, TaskScheduler.Default).Unwrap();
            }
        }
    }

    /// <summary>界面事件照常发给界面，同时把「调用了什么工具」这类进度转给手机。</summary>
    private sealed class ProgressTee : IHostEvents
    {
        private readonly IHostEvents _inner;
        private readonly string _conversationId;
        private readonly RemoteProgress _progress;

        public ProgressTee(IHostEvents inner, string conversationId, RemoteProgress progress)
        {
            _inner = inner;
            _conversationId = conversationId;
            _progress = progress;
        }

        public void Post(object evt)
        {
            _inner.Post(evt);
            var type = Prop(evt, "type");
            if (type is not ("tool.started" or "chat.notice") || Prop(evt, "conversationId") != _conversationId)
            {
                return;
            }
            if (type == "chat.notice")
            {
                _progress.Report("text", Prop(evt, "text") ?? "");
                return;
            }
            var name = Prop(evt, "name") ?? "";
            if (ToolLabel(name) is { } label)
            {
                var summary = Prop(evt, "summary") ?? "";
                _progress.Report("tool", summary.Length > 0 ? $"{label}：{(summary.Length > 120 ? summary[..120] + "…" : summary)}" : label);
            }
        }

        private static string? Prop(object evt, string name) => evt.GetType().GetProperty(name)?.GetValue(evt) as string;

        /// <summary>手机上显示的步骤名。记忆、计划这类内部动作不显示。</summary>
        private static string? ToolLabel(string name) => name switch
        {
            "run_shell" => "运行命令",
            "read_file" => "读取文件",
            "write_file" => "写入文件",
            "edit_file" => "修改文件",
            "list_dir" => "查看文件夹",
            "search_files" => "搜索文件",
            "delete_path" => "删除",
            "open_app" => "打开程序",
            "load_skill" => "使用技能",
            "ask_expert" => "请专家协助",
            _ when Flyknit.Core.Mcp.McpNames.IsMcp(name) => "调用连接器",
            _ => null,
        };
    }
}
