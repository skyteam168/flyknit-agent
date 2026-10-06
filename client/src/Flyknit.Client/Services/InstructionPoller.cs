using System;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Client.Bridge;
using Flyknit.Core.Gateway;

namespace Flyknit.Client.Services;

/// <summary>
/// 轮询后台下发的指令，逐条交给本机的 AI agent 执行，再把结果回报。
///
/// 一次只跑一条（顺序执行），避免同时多条把电脑拖垮。开始执行时给员工弹一条桌面提示；
/// 执行本身静默进行，受安全策略约束、每步工具调用写入审计。
/// </summary>
public sealed class InstructionPoller : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly AgentHost _host;
    private readonly Action<string, string> _notify;
    private readonly CancellationTokenSource _cts = new();

    /// <param name="notify">弹桌面提示（标题，正文）；由宿主切到 UI 线程执行。</param>
    public InstructionPoller(AgentHost host, Action<string, string> notify)
    {
        _host = host;
        _notify = notify;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 没连上服务端（未注册、被停用、网络不通）时不拉，等下一轮
                if (_host.Connected && _host.Server.IsRegistered)
                {
                    var list = await _host.Server.PollInstructionsAsync(token);
                    foreach (var ins in list)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }
                        await HandleAsync(ins, token);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("拉取下发指令失败", ex);
            }
            try
            {
                await Task.Delay(Interval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task HandleAsync(ClientInstruction ins, CancellationToken token)
    {
        try
        {
            if (!await _host.Server.StartInstructionAsync(ins.RunId, token))
            {
                Log.Info($"下发指令 run={ins.RunId} 已被后台取消，跳过");
                return;
            }
            var what = ins.Title.Length > 0 ? ins.Title : "一项维护任务";
            _notify("IT 正在维护这台电脑", $"IT 正在为你的电脑执行：{what}。过程自动完成，请勿关机。");

            var outcome = await _host.RunInstructionAsync(ins.Prompt, ins.Title, token);
            await _host.Server.FinishInstructionAsync(ins.RunId, outcome.Ok, outcome.Answer, outcome.Error, outcome.ConversationId, token);
            Log.Info($"下发指令 run={ins.RunId} 回报完成 ok={outcome.Ok}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw; // 程序退出，下次开机重新领
        }
        catch (Exception ex)
        {
            Log.Error($"执行下发指令 run={ins.RunId} 失败", ex);
            try
            {
                await _host.Server.FinishInstructionAsync(ins.RunId, false, "", ex.Message, "", token);
            }
            catch (Exception)
            {
                // 回报也失败，服务端 7 天后自动过期
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
