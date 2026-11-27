using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>
/// 服务端下发的一个钩子的匹配条件（docs/hooks.md，/client/config 的 hooks）。只有「哪些事件、哪些工具」，
/// 地址和签名密钥只在服务端。员工端用它少问几次：不匹配的工具不用去问服务端。
/// </summary>
public sealed record HookSpec
{
    [JsonPropertyName("id")] public int Id { get; init; }
    /// <summary>pre_tool / post_tool / stop 的子集。</summary>
    [JsonPropertyName("events")] public List<string> Events { get; init; } = new();
    [JsonPropertyName("tools")] public string Tools { get; init; } = "*";
    [JsonPropertyName("match")] public string Match { get; init; } = "";
    /// <summary>问服务端都失败了（服务端连不上）时怎么办：ask / deny / allow。</summary>
    [JsonPropertyName("on_error")] public string OnError { get; init; } = "ask";
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; init; } = 3000;
}

/// <summary>钩子对一次工具调用的回答。只有三种：照旧、必须确认、拦下。</summary>
public enum HookDecision
{
    Allow,
    Ask,
    Deny,
}

public sealed record HookVerdict(HookDecision Decision, string Reason = "")
{
    public static readonly HookVerdict Allow = new(HookDecision.Allow);

    public static HookDecision Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "allow" => HookDecision.Allow,
        "deny" or "block" => HookDecision.Deny,
        _ => HookDecision.Ask, // 看不懂的一律按「要确认」，不会因为回答写错就放行
    };
}

/// <summary>一次工具调用要发给钩子的信息（参数已经打过码）。</summary>
public sealed record HookCall(string Tool, string Arguments, string ConversationId, string RunId, string TaskId, string GrantId);

/// <summary>问服务端的那一层（FlyknitServerClient 实现；单测里换成假的）。</summary>
public interface IHookClient
{
    /// <summary>pre_tool：服务端去问匹配的钩子，回答最严的那个。连不上时抛异常，由 <see cref="ToolHooks"/> 按 on_error 处理。</summary>
    Task<HookVerdict> CheckAsync(HookCall call, CancellationToken ct);

    /// <summary>post_tool / stop：只通知，不等结果；失败也不影响任务。</summary>
    Task NotifyAsync(string hookEvent, HookCall call, bool ok, string summary, string status, CancellationToken ct);
}

/// <summary>
/// 钩子（同 Claude Code 的 PreToolUse / PostToolUse / Stop hooks）：IT 在后台配的 HTTP 地址，AI 调用工具前、做完后、
/// 一轮结束时收到通知。调用前的回答<b>只能收紧</b>（见 <see cref="Tighten"/>）：照旧、改成必须确认（不能记成以后自动）、拦下。
/// 本来就被拦下的不再去问；本来就要确认的，回答 allow 也照样要确认。
/// </summary>
public sealed class ToolHooks
{
    public const string PreTool = "pre_tool";
    public const string PostTool = "post_tool";
    public const string Stop = "stop";

    /// <summary>发给钩子的参数、结果最多这么长（服务端还会再截）。</summary>
    public const int MaxArguments = 20000;
    public const int MaxSummary = 1000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private readonly IHookClient _client;
    private readonly List<Compiled> _hooks = new();

    private sealed record Compiled(HookSpec Spec, Regex[] Tools, Regex? Match);

    public ToolHooks(IEnumerable<HookSpec> specs, IHookClient client)
    {
        _client = client;
        foreach (var s in specs)
        {
            try
            {
                var tools = (string.IsNullOrWhiteSpace(s.Tools) ? "*" : s.Tools)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => new Regex("^" + Regex.Escape(t).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.None, RegexTimeout))
                    .ToArray();
                var match = s.Match.Trim().Length > 0 ? new Regex(s.Match, RegexOptions.IgnoreCase, RegexTimeout) : null;
                _hooks.Add(new Compiled(s, tools, match));
            }
            catch (ArgumentException)
            {
                // 正则写坏了：服务端保存时校验过，万一坏了这一条员工端就按匹配所有工具算（多问一次服务端，服务端自己再匹配）
                _hooks.Add(new Compiled(s, new[] { new Regex(".*") }, null));
            }
        }
    }

    public bool IsEmpty => _hooks.Count == 0;

    public bool Has(string hookEvent) => _hooks.Any(h => h.Spec.Events.Contains(hookEvent));

    /// <summary>这次调用有没有钩子要看（员工端先筛一遍，服务端还会按自己的配置再匹配）。</summary>
    public IReadOnlyList<HookSpec> Matching(string hookEvent, string tool, string argsJson) =>
        _hooks.Where(h => h.Spec.Events.Contains(hookEvent) && (hookEvent == Stop || (Matches(h.Tools, tool) && MatchesArgs(h.Match, argsJson))))
            .Select(h => h.Spec).ToList();

    private static bool Matches(Regex[] patterns, string tool)
    {
        try
        {
            return patterns.Length == 0 || patterns.Any(p => p.IsMatch(tool));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static bool MatchesArgs(Regex? match, string argsJson)
    {
        if (match is null)
        {
            return true;
        }
        try
        {
            return match.IsMatch(argsJson.Length > MaxArguments ? argsJson[..MaxArguments] : argsJson);
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // 判断不了就去问（服务端再判断一遍）
        }
    }

    /// <summary>
    /// 只收紧：deny → 拦下；ask → 至少要确认，而且「以后自动允许」「本次都允许」都跳不过；allow → 原样。
    /// 本来被拦下的永远是拦下，本来要确认的永远还要确认。
    /// </summary>
    public static PolicyDecision Tighten(PolicyDecision decision, HookVerdict verdict)
    {
        if (decision.Level == RiskLevel.Blocked || verdict.Decision == HookDecision.Allow)
        {
            return decision;
        }
        var why = verdict.Reason.Length > 0 ? verdict.Reason : "公司的安全检查要求" + (verdict.Decision == HookDecision.Deny ? "拦下这个操作" : "先确认");
        if (verdict.Decision == HookDecision.Deny)
        {
            return PolicyDecision.Blocked(why) with { Effect = decision.Effect };
        }
        var reason = decision.Level == RiskLevel.Confirm && decision.Reason.Length > 0 ? $"{why}；{decision.Reason}" : why;
        return PolicyDecision.Confirm(reason) with { Effect = decision.Effect, Rule = decision.Rule, AlwaysAsk = true };
    }

    /// <summary>参数打码（密码、令牌、Cookie）、截断后再发出去。</summary>
    public static string SafeArguments(string argsJson)
    {
        var text = SecretRedactor.Redact(argsJson ?? "");
        return text.Length > MaxArguments ? text[..MaxArguments] : text;
    }

    /// <summary>
    /// 调用前：有匹配的钩子就问服务端，按回答收紧判定。服务端连不上时按匹配的钩子里最严的 on_error 处理；
    /// 停止任务的取消照常往外抛。
    /// </summary>
    public async Task<PolicyDecision> BeforeAsync(HookCall call, PolicyDecision decision, CancellationToken ct)
    {
        if (decision.Level == RiskLevel.Blocked)
        {
            return decision;
        }
        var matching = Matching(PreTool, call.Tool, call.Arguments);
        if (matching.Count == 0)
        {
            return decision;
        }
        HookVerdict verdict;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 服务端会按各个钩子自己的超时等，这里多留一点余量，免得服务端卡住时任务一直不动
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(matching.Max(h => h.TimeoutMs), 500, 10000) + 5000));
        try
        {
            verdict = await _client.CheckAsync(call with { Arguments = SafeArguments(call.Arguments) }, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var onError = matching.Select(h => HookVerdict.Parse(h.OnError)).Max();
            verdict = new HookVerdict(onError, onError == HookDecision.Allow ? "" : "公司的安全检查暂时连不上");
        }
        return Tighten(decision, verdict);
    }

    /// <summary>工具做完：有匹配的 post_tool 钩子就通知（后台发，不等，失败不管）。</summary>
    public void After(HookCall call, bool ok, string output)
    {
        if (Matching(PostTool, call.Tool, call.Arguments).Count == 0)
        {
            return;
        }
        var summary = SecretRedactor.Redact(output.Length > MaxSummary * 4 ? output[..(MaxSummary * 4)] : output);
        Fire(PostTool, call with { Arguments = SafeArguments(call.Arguments) }, ok, summary.Length > MaxSummary ? summary[..MaxSummary] : summary, "");
    }

    /// <summary>一轮任务结束：有 stop 钩子就通知。status：succeeded / failed / stopped。</summary>
    public void Finished(string conversationId, string runId, string taskId, string grantId, string status, string summary)
    {
        if (!Has(Stop))
        {
            return;
        }
        var text = SecretRedactor.Redact(summary.Length > MaxSummary * 4 ? summary[..(MaxSummary * 4)] : summary);
        Fire(Stop, new HookCall("", "", conversationId, runId, taskId, grantId), status == "succeeded", text.Length > MaxSummary ? text[..MaxSummary] : text, status);
    }

    private void Fire(string hookEvent, HookCall call, bool ok, string summary, string status) =>
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _client.NotifyAsync(hookEvent, call, ok, summary, status, cts.Token);
            }
            catch (Exception)
            {
                // 通知失败不影响任务
            }
        });
}
