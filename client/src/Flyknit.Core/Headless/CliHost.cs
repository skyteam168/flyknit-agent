using Flyknit.Core.Agent;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Skills;

namespace Flyknit.Core.Headless;

/// <summary>命令行的退出码。脚本和 CI 靠它判断，不要改已有的数字。</summary>
public static class ExitCodes
{
    /// <summary>任务做完了。</summary>
    public const int Ok = 0;

    /// <summary>任务没做完（模型服务出错、空转、预算用完、步数到上限、失败太多）。</summary>
    public const int NotCompleted = 1;

    /// <summary>命令行参数不对。</summary>
    public const int Usage = 2;

    /// <summary>这台电脑没登录员工端，或者设备令牌失效 / 设备被停用。</summary>
    public const int NotSignedIn = 3;

    /// <summary>IT 没开「命令行 / IDE 接入」。</summary>
    public const int AccessDisabled = 4;

    /// <summary>今天的额度用完了（服务端 429）。</summary>
    public const int QuotaExceeded = 5;

    /// <summary>连不上公司服务器。</summary>
    public const int Unreachable = 6;

    /// <summary>超过 --timeout 设的时间。</summary>
    public const int Timeout = 124;

    /// <summary>被 Ctrl+C 中断。</summary>
    public const int Interrupted = 130;

    /// <summary>模型服务的错误对应哪个退出码。</summary>
    public static int ForGatewayError(GatewayException ex) =>
        ex.IsNetworkError ? Unreachable
        : ex.StatusCode is 401 or 403 ? NotSignedIn
        : ex.StatusCode == 429 ? QuotaExceeded
        : NotCompleted;
}

/// <summary>连接结果：成功时带上运行环境；失败时是退出码和给人看的原因。</summary>
public sealed record CliConnection(HeadlessEnvironment? Environment, RecordingGateway? Gateway, BufferedAudit? Audit, int ExitCode, string Error)
{
    public bool Ok => Environment is not null;

    /// <summary>连上的服务端（报审计用）。</summary>
    public FlyknitServerClient? Server { get; init; }
}

/// <summary>审计先攒着，一轮结束（或程序退出前）一次报给服务端。报不上去写到 stderr，不影响任务结果。</summary>
public sealed class BufferedAudit : IAuditSink
{
    private readonly List<AuditEntry> _pending = new();
    private readonly object _gate = new();

    public void Record(AuditEntry entry)
    {
        lock (_gate)
        {
            _pending.Add(entry);
        }
    }

    public IReadOnlyList<AuditEntry> Take()
    {
        lock (_gate)
        {
            var items = _pending.ToList();
            _pending.Clear();
            return items;
        }
    }

    public async Task FlushAsync(FlyknitServerClient server, Action<string>? warn)
    {
        var items = Take();
        if (items.Count == 0)
        {
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await server.ReportAuditAsync(items, cts.Token);
        }
        catch (Exception ex) when (ex is GatewayException or HttpRequestException or OperationCanceledException)
        {
            warn?.Invoke($"审计记录上报失败（{items.Count} 条）：{ex.Message}");
        }
    }
}

/// <summary>
/// 命令行连公司服务器：用员工端登录后的设备令牌拉一次配置（安全中心、命令策略、IT 工具规则），检查总开关，
/// 拼出 <see cref="HeadlessEnvironment"/>。和员工端拉的是同一份配置，所以安全设置一样。
/// </summary>
public static class CliHost
{
    /// <param name="think">让模型深度思考（命令行 --think）。不跟员工端的「深度思考」开关走，见 CliOptions.Think。</param>
    public static async Task<CliConnection> ConnectAsync(LocalProfile profile, string actor, HttpMessageHandler? handler, Action<string>? warn, CancellationToken ct, bool think = false)
    {
        if (!profile.IsRegistered)
        {
            return Fail(ExitCodes.NotSignedIn,
                "这台电脑（当前 Windows 账号）还没登录 FlyknitBuddy。先打开 FlyknitBuddy 登录一次，命令行用的是同一个登录");
        }
        handler ??= HttpProxy.CreateHandler(profile.ProxyMode, profile.ProxyUrl, profile.ProxyUser, profile.ProxyPassword, warn);
        var server = new FlyknitServerClient(new HttpClient(handler), profile.ServerUrl, profile.DeviceToken);
        ClientConfig config;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            config = await server.GetConfigAsync(cts.Token);
        }
        catch (GatewayException ex)
        {
            var code = ExitCodes.ForGatewayError(ex);
            return Fail(code == ExitCodes.NotCompleted ? ExitCodes.Unreachable : code, code switch
            {
                ExitCodes.NotSignedIn => $"服务器不认这台电脑的登录（{ex.Message}）。请打开 FlyknitBuddy 重新登录",
                _ => $"连不上公司服务器 {profile.ServerUrl}：{ex.Message}",
            });
        }
        catch (HttpRequestException ex)
        {
            return Fail(ExitCodes.Unreachable, $"连不上公司服务器 {profile.ServerUrl}：{ex.Message}");
        }

        var security = new SecuritySettings();
        security.MergeFromServer(config.Security);
        security.ApplyLocal(profile.SecurityChoices);
        if (HeadlessAccess.Problem(security) is { } problem)
        {
            return Fail(ExitCodes.AccessDisabled, problem);
        }

        var gateway = new RecordingGateway(server);
        var audit = new BufferedAudit();
        var agentScene = config.Scenes.FirstOrDefault(s => s.Scene == Scenes.Agent);
        var env = new HeadlessEnvironment
        {
            Gateway = gateway,
            Security = security,
            Policy = config.Policy is not null ? new CommandPolicy(config.Policy) : CommandPolicy.Default(),
            ToolRules = new ToolRules(config.ToolRules),
            Approvals = new ApprovalStore(profile.ApprovalRulesFile),
            Audit = audit,
            Actor = actor,
            UiLanguage = profile.UiLanguage,
            DeepThinking = think,
            Skills = LoadSkills(profile),
            ContextLength = agentScene?.ContextLength ?? 0,
            ToolOutputDir = Path.Combine(Path.GetTempPath(), "Flyknit", "tool-outputs"),
        };
        return new CliConnection(env, gateway, audit, ExitCodes.Ok, "") { Server = server };

        static CliConnection Fail(int code, string error) => new(null, null, null, code, error);
    }

    /// <summary>员工端装好的技能（只读）：机器级、企业、学到的、个人，和员工端同样的顺序。</summary>
    private static SkillCatalog? LoadSkills(LocalProfile profile)
    {
        try
        {
            var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FlyknitBuddy", "skills");
            var skills = Path.Combine(profile.Root, "skills");
            var catalog = new SkillCatalog()
                .AddRoot(machine, SkillSource.Organization)
                .AddRoot(Path.Combine(skills, "org"), SkillSource.Organization)
                .AddRoot(Path.Combine(skills, "learned"), SkillSource.Learned)
                .AddRoot(skills);
            catalog.Refresh(profile.DisabledSkills);
            return catalog;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
