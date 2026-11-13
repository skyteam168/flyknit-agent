using System;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Gateway;

namespace Flyknit.Client.Services;

/// <summary>
/// 钉钉 / 企业微信 / 手机 App 通道：员工在钉钉或企业微信里私聊机器人发的话，服务端排进队列，这里长轮询领回来交给本机的 AI agent，
/// 办完把结果回报，服务端再发回钉钉。
///
/// 后台没开钉钉通道、或者这台电脑没有人绑定时，只是每 5 分钟问一次状态，不常驻长连接。
/// 一次只办一条，按到达顺序来。需要员工确认的操作不会远程放行（见 AgentHost.RunChannelMessageAsync）。
/// </summary>
public sealed class ChannelRelay : IDisposable
{
    private const int Wait = 25;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

    private readonly AgentHost _host;
    private readonly Action<string, string> _notify;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _recheck = new(0);
    /// <summary>刚生成过绑定码：这段时间里即使还没有绑定也先等着，绑上后第一条消息不用等。</summary>
    private DateTime _bindWindowUntil = DateTime.MinValue;

    public ChannelRelay(AgentHost host, Action<string, string> notify)
    {
        _host = host;
        _notify = notify;
        _host.ChannelsChanged += Recheck;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>员工刚生成绑定码或解绑：马上重新看一下要不要开始等消息。</summary>
    public void Recheck(bool expectBinding)
    {
        if (expectBinding)
        {
            _bindWindowUntil = DateTime.UtcNow.AddMinutes(10);
        }
        _recheck.Release();
    }

    private async Task LoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_host.Connected && _host.Server.IsRegistered)
                {
                    var state = await _host.Server.GetChannelsAsync(token);
                    if ((state.DingTalk || state.WeCom || state.Mobile) && (state.Bindings.Count > 0 || DateTime.UtcNow < _bindWindowUntil))
                    {
                        await ServeAsync(token);
                        continue; // 服务端那边状态变了（关掉、解绑）才会出来，马上再看一次
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("查询钉钉通道状态失败", ex);
            }
            try
            {
                await _recheck.WaitAsync(Idle, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>一直长轮询，直到出错或者每 10 分钟出来重新确认一次状态。</summary>
    private async Task ServeAsync(CancellationToken token)
    {
        var until = DateTime.UtcNow.AddMinutes(10);
        var failures = 0;
        while (!token.IsCancellationRequested && DateTime.UtcNow < until)
        {
            try
            {
                using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
                call.CancelAfter(TimeSpan.FromSeconds(Wait + 20));
                var list = await _host.Server.PollChannelAsync(Wait, call.Token);
                failures = 0;
                foreach (var task in list)
                {
                    if (!_host.RouterOn)
                    {
                        await HandleAsync(task, task.ConversationId, token); // 调度关着：和以前一样，一条一条办
                        continue;
                    }
                    await RouteAsync(task, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 网络抖动：退避一下再来，连续失败太多就回外层重新确认状态
                Log.Warn("领取钉钉 / 企业微信消息失败", ex);
                if (++failures >= 5)
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(5 * failures), token);
            }
        }
    }

    /// <summary>
    /// 调度开着（docs/c2-router.md 3.1）：收消息和执行分开。追问、问进度、重复事件马上回复，不排队；
    /// 新的事和要在原对话里接着做的，交给执行队列——仍然一次一件、按到达顺序。
    /// </summary>
    private async Task RouteAsync(ChannelTask task, CancellationToken token)
    {
        StartExecutor(token);
        AgentHost.ChannelRoute route;
        try
        {
            route = await _host.RouteChannelMessageAsync(task, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"调度钉钉消息 {task.Id} 失败，按新任务办", ex);
            route = new AgentHost.ChannelRoute(false, "", task.ConversationId);
        }
        if (!route.Done)
        {
            _queue.Writer.TryWrite((task, route.ConversationId));
            return;
        }
        try
        {
            await _host.Server.FinishChannelAsync(task.Id, true, route.Answer, "", route.ConversationId, token);
            Log.Info($"钉钉消息 {task.Id} 由调度直接回复");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"回报钉钉消息 {task.Id} 失败", ex);
        }
    }

    private readonly System.Threading.Channels.Channel<(ChannelTask Task, string ConversationId)> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<(ChannelTask, string)>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private int _executorStarted;

    /// <summary>执行队列：一次一件、按到达顺序（和调度关着时一样）。</summary>
    private void StartExecutor(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _executorStarted, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var (task, conversationId) in _queue.Reader.ReadAllAsync(token))
                {
                    await HandleAsync(task, conversationId, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("钉钉消息执行队列出错", ex);
                Interlocked.Exchange(ref _executorStarted, 0);
            }
        });
    }

    private async Task HandleAsync(ChannelTask task, string conversationId, CancellationToken token)
    {
        try
        {
            var who = task.Sender.Length > 0 ? task.Sender : "你";
            var where = AgentHost.ChannelName(task.Channel);
            _notify($"{where}消息", $"{who} 在{where}里让 FlyknitBuddy 办事：{Short(task.Text)}");
            var outcome = await _host.RunChannelMessageAsync(task.Text, task.Sender, conversationId, token, task.Channel, task.Id,
                task.Permission, task.Rehearsal);
            await _host.Server.FinishChannelAsync(task.Id, outcome.Ok, outcome.Answer, outcome.Error, outcome.ConversationId, token);
            Log.Info($"钉钉消息 {task.Id} 回报完成 ok={outcome.Ok}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw; // 程序退出：服务端 2 小时后会把它记成失败
        }
        catch (Exception ex)
        {
            Log.Error($"处理钉钉消息 {task.Id} 失败", ex);
            try
            {
                await _host.Server.FinishChannelAsync(task.Id, false, "", ex.Message, "", token);
            }
            catch (Exception)
            {
                // 回报也失败：服务端超时后自动记成失败
            }
        }
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";

    public void Dispose()
    {
        _host.ChannelsChanged -= Recheck;
        _cts.Cancel();
        _cts.Dispose();
        _recheck.Dispose();
    }
}
