using Flyknit.Agent.Tasks;

namespace Flyknit.Agent;

/// <summary>把一个任务分派到对应的执行器，并给员工弹开始提示。</summary>
public sealed class TaskRunner
{
    private readonly ServerApi _api;

    public TaskRunner(ServerApi api) => _api = api;

    public async Task<RunResult> ExecuteAsync(AgentRun run, CancellationToken ct)
    {
        AgentLog.Info($"开始执行 run={run.RunId} kind={run.Kind}「{run.Title}」");
        NotifyEmployee(run);
        try
        {
            return run.Kind switch
            {
                "collect_info" => await InfoCollector.RunAsync(ct),
                "clean" => await Cleaner.RunAsync(run.Params, ct),
                "optimize" => await Optimizer.RunAsync(run.Params, ct),
                "install" => await Installer.RunAsync(run.Params, _api, ct),
                "repair" => await Repairer.RunAsync(run.Params, ct),
                "restart" => await Restarter.RunAsync(run.Params, ct),
                _ => RunResult.Fail($"代理不认识的任务类型：{run.Kind}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"执行 run={run.RunId} 出错", ex);
            return RunResult.Fail($"执行出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 静默执行但让员工知情：开工时弹一条右下角提示。采集信息很快且无感，不打扰。
    /// 重启任务由 <see cref="Restarter"/> 自己发更贴切的提示，这里跳过。
    /// </summary>
    private static void NotifyEmployee(AgentRun run)
    {
        if (run.Kind is "collect_info" or "restart")
        {
            return;
        }
        var what = run.Title.Length > 0 ? run.Title : run.Kind;
        Notice.Post("IT 正在维护这台电脑", $"IT 正在为你的电脑执行：{what}。过程自动完成，请勿关机。");
    }
}
