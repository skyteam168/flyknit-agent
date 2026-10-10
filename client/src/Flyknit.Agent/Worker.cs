using Microsoft.Extensions.Hosting;

namespace Flyknit.Agent;

/// <summary>
/// 服务主循环：确保已注册 -> 每隔一段时间拉任务 -> 逐个执行并回报。
/// 任务一个一个跑（不并发），避免同时清理又修复把电脑拖垮。
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly AgentConfig _config;
    private ServerApi _api;
    private TaskRunner _runner;

    public Worker()
    {
        _config = AgentConfig.Load();
        _api = new ServerApi(_config);
        _runner = new TaskRunner(_api);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentLog.Info($"运维代理启动 v{ServerApi.Version}");
        AgentPaths.EnsureDirectories();
        AgentPaths.RestrictConfig();

        if (string.IsNullOrEmpty(_config.ServerUrl) || (string.IsNullOrEmpty(_config.EnrollmentKey) && string.IsNullOrEmpty(_config.Ticket)))
        {
            AgentLog.Error("配置缺少 server_url，或注册密钥和安装凭证都没有。请用安装程序（或安装脚本）重新安装。");
            return;
        }

        // 员工端由安装程序装在 Program Files 时，它的升级由这里负责（员工账号写不了那个目录）
        var clientUpdater = new ClientUpdater(_config, () => _api);
        _ = Task.Run(() => clientUpdater.RunAsync(stoppingToken), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.IsRegistered && !await EnsureRegistered(stoppingToken))
                {
                    await Delay(60, stoppingToken);
                    continue;
                }

                var outcome = await _api.PollAsync(stoppingToken);
                if (outcome is null)
                {
                    // 令牌失效，清掉身份重新注册
                    AgentLog.Warn("服务端不认当前令牌，准备重新注册");
                    _config.AgentId = 0;
                    _config.Token = "";
                    _config.Save();
                    RebuildApi();
                    continue;
                }

                foreach (var run in outcome.Runs)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await ProcessRun(run, stoppingToken);
                }

                await Delay(outcome.Runs.Count > 0 ? 2 : outcome.PollAfterSeconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                // 最常见的是安装包里的服务器地址填错了（localhost、开发端口），把地址写进日志方便 IT 一眼看出来
                AgentLog.Warn($"连不上服务器 {_config.ServerUrl}（{(_config.IsRegistered ? "已注册" : "还没注册上")}），一分钟后重试：{ex.Message}");
                await Delay(60, stoppingToken);
            }
            catch (Exception ex)
            {
                AgentLog.Warn("轮询出错，稍后重试", ex);
                await Delay(60, stoppingToken);
            }
        }
        AgentLog.Info("运维代理停止");
    }

    private async Task ProcessRun(AgentRun run, CancellationToken ct)
    {
        try
        {
            if (!await _api.StartAsync(run.RunId, ct))
            {
                AgentLog.Info($"run={run.RunId} 已被后台取消，跳过");
                return;
            }
            var result = await _runner.ExecuteAsync(run, ct);
            await _api.FinishAsync(run.RunId, result, ct);
            AgentLog.Info($"run={run.RunId} 回报完成 success={result.Succeeded} exit={result.ExitCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 服务正在停，别回报，下次开机重新领
        }
        catch (Exception ex)
        {
            AgentLog.Error($"处理 run={run.RunId} 失败", ex);
            try { await _api.FinishAsync(run.RunId, RunResult.Fail($"代理执行异常：{ex.Message}"), ct); }
            catch (Exception) { /* 回报也失败，服务端 7 天后自动过期 */ }
        }
    }

    private async Task<bool> EnsureRegistered(CancellationToken ct)
    {
        var ok = await _api.RegisterAsync(ct);
        if (ok)
        {
            RebuildApi();
        }
        return ok;
    }

    private void RebuildApi()
    {
        _api = new ServerApi(_config);
        _runner = new TaskRunner(_api);
    }

    private static async Task Delay(int seconds, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
        catch (OperationCanceledException) { }
    }
}
