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
    public static string ChannelName(string channel) => channel switch { "wecom" => "企业微信", "mobile" => "手机", _ => "钉钉" };

    /// <param name="messageId">服务端的消息 ID。手机发来的任务用它回报进度、请求在手机上确认。</param>
    public async Task<InstructionOutcome> RunChannelMessageAsync(string text, string sender, string conversationId, CancellationToken ct, string channel = "dingtalk", int messageId = 0)
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
        // 钉钉、企业微信：需要确认的一律拒绝。手机：交给发起的那部手机由本人批准（后台允许时），否则同样拒绝
        var confirm = new RemoteDecline(mobile ? Server : null, messageId);
        var progress = mobile ? new RemoteProgress(Server, messageId) : null;
        _remoteRuns[conv.Id] = 0;
        try
        {
            return await RunRemoteAsync(conv, text, confirm, ct, progress);
        }
        finally
        {
            _remoteRuns.TryRemove(conv.Id, out _);
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
    /// 远程执行时的确认。钉钉、企业微信：一律拒绝。手机：问服务端能不能交给发起的手机批准，
    /// 能就等手机上的回答（超时当拒绝），不能就拒绝。被拒的都记下来写进回复里。
    /// 批准只放行这一次（不建规则）；被安全策略「阻止」的命令根本不会走到这里。
    /// </summary>
    private sealed class RemoteDecline : IConfirmationHandler
    {
        private const int Wait = 25;
        private readonly object _lock = new();
        private readonly Flyknit.Core.Gateway.FlyknitServerClient? _server;
        private readonly int _messageId;

        public RemoteDecline(Flyknit.Core.Gateway.FlyknitServerClient? server = null, int messageId = 0)
        {
            _server = server;
            _messageId = messageId;
        }

        public List<string> Declined { get; } = new();

        public async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            if (_server is not null && await AskPhoneAsync(request, ct))
            {
                return ConfirmChoice.AllowOnce;
            }
            lock (_lock)
            {
                Declined.Add(request.Summary.Length > 80 ? request.Summary[..80] + "…" : request.Summary);
            }
            return ConfirmChoice.Reject;
        }

        private async Task<bool> AskPhoneAsync(ConfirmRequest request, CancellationToken ct)
        {
            try
            {
                var detail = request.Decision.Reason.Length > 0 ? request.Decision.Reason : request.Rationale;
                var ticket = await _server!.RequestChannelApprovalAsync(_messageId, request.Summary, detail, "confirm", ct);
                if (!ticket.Enabled)
                {
                    return false;
                }
                var until = DateTime.UtcNow.AddSeconds(Math.Max(60, ticket.TimeoutSec) + 30);
                while (DateTime.UtcNow < until)
                {
                    var status = await _server.WaitChannelApprovalAsync(ticket.Id, Wait, ct);
                    if (status != "pending")
                    {
                        Log.Info($"手机上{(status == "approved" ? "批准" : "没有批准")}了：{request.Summary}（{status}）");
                        return status == "approved";
                    }
                }
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("请求手机确认失败，按拒绝处理", ex);
                return false;
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
