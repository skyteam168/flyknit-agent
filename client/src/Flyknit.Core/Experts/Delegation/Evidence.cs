using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Experts.Delegation;

/// <summary>专家的一次工具调用，编号 E1、E2……，交卷时结论只能引用这些编号。</summary>
public sealed record EvidenceEntry(string Id, string ToolName, string Summary, bool Ok, string Output, IReadOnlyList<string> Outputs);

/// <summary>
/// 证据台账：专家每次工具调用都由系统记一笔、编一个号，附在工具结果开头给专家看。
/// 编号是系统生成、存在系统这边的，专家交卷时写个不存在的编号、或者引用一次失败的调用，核验时都对不上。
/// </summary>
public sealed class EvidenceLedger
{
    /// <summary>每条证据最多留这么多字用来核验，够放查询结果的关键部分。</summary>
    public const int KeepChars = 8000;

    private readonly List<EvidenceEntry> _entries = new();
    private readonly object _gate = new();

    public IReadOnlyList<EvidenceEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public EvidenceEntry Add(string toolName, string summary, ToolResult result)
    {
        lock (_gate)
        {
            var output = result.Output.Length > KeepChars ? result.Output[..KeepChars] : result.Output;
            var entry = new EvidenceEntry($"E{_entries.Count + 1}", toolName, summary, result.Ok, output, result.Outputs);
            _entries.Add(entry);
            return entry;
        }
    }

    public EvidenceEntry? Get(string id)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(e => e.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>专家这次产出的文件。</summary>
    public IReadOnlyList<string> ProducedFiles
    {
        get
        {
            lock (_gate)
            {
                return _entries.Where(e => e.Ok).SelectMany(e => e.Outputs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    public List<ToolCallStat> Stats() =>
        Entries.GroupBy(e => e.ToolName)
            .Select(g => new ToolCallStat { Name = g.Key, Count = g.Count(), Failed = g.Count(e => !e.Ok) })
            .OrderByDescending(s => s.Count)
            .ToList();
}

/// <summary>
/// 专家这次运行还能不能动手。每次工具调用真正执行前都问一遍——不只是在开始时筛一次工具：
/// 员工停了、到时限了、管理员关了派遣开关，之后的调用（包括刚刚等确认回来的）一律不执行。
/// </summary>
public sealed class ExpertRunGuard
{
    /// <summary>专家永远不能用的工具：再派遣专家（只许一层）、写长期记忆。</summary>
    public static readonly IReadOnlySet<string> Banned = new HashSet<string>(StringComparer.Ordinal)
    {
        AskExpertTool.ToolName,
        "memory_write",
    };

    private readonly Func<bool> _enabled;
    private readonly Func<PolicyDecision, bool> _remembered;

    /// <param name="remembered">员工设置过「总是允许」的操作（只读专家只有这种需要确认的操作能做）。</param>
    public ExpertRunGuard(SubtaskState state, Expert expert, Func<bool> enabled, Func<PolicyDecision, bool>? remembered = null)
    {
        State = state;
        Expert = expert;
        _enabled = enabled;
        _remembered = remembered ?? (_ => false);
    }

    public SubtaskState State { get; }
    public Expert Expert { get; }

    public bool Allows(ITool tool) => !Banned.Contains(tool.Name) && ExpertRuntime.Allows(Expert, tool);

    /// <summary>
    /// 只读专家不弹确认（和 Claude Code 的只读子代理一样，派出去就自己跑完）：要确认的操作说明不是纯查看，
    /// 直接拦下，让它写进 open_questions 交回主助手。员工以前设过「总是允许」的照常放行。
    /// </summary>
    public PolicyDecision Review(PolicyDecision decision, string toolName)
    {
        if (Expert.ReadOnly && decision.Level == RiskLevel.Confirm && !_remembered(decision))
        {
            return PolicyDecision.Blocked(
                $"「{Expert.Name}」是只读专家，不弹确认；{toolName} 需要员工确认（{decision.Reason}），没有执行。" +
                "需要这一步的话，把它写进 open_questions 交回主助手");
        }
        return decision;
    }

    /// <summary>不能动手时返回原因，能动手返回 null。</summary>
    public string? Refusal()
    {
        if (!State.IsActive)
        {
            return $"子任务已经结束（{State.Status.Wire()}），不能再执行操作";
        }
        if (!_enabled())
        {
            return "管理员已关闭「派遣专家」，不能再执行操作";
        }
        return null;
    }
}

/// <summary>
/// 套在专家能用的每个工具外面：执行前过一遍 <see cref="ExpertRunGuard"/>，执行后记进证据台账，
/// 并在结果开头标上证据编号。工具本身的风险判定、确认、命令策略原样保留。
/// </summary>
public sealed class GuardedExpertTool : ITool
{
    private readonly ITool _inner;
    private readonly ExpertRunGuard _guard;
    private readonly EvidenceLedger _ledger;

    public GuardedExpertTool(ITool inner, ExpertRunGuard guard, EvidenceLedger ledger)
    {
        _inner = inner;
        _guard = guard;
        _ledger = ledger;
    }

    public ITool Inner => _inner;
    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public JsonObject Parameters => _inner.Parameters;

    public PolicyDecision Assess(JsonElement args, ToolContext ctx)
    {
        if (!_guard.Allows(_inner))
        {
            return PolicyDecision.Blocked($"「{_guard.Expert.Name}」不能使用 {_inner.Name}");
        }
        if (_guard.Refusal() is { } reason)
        {
            return PolicyDecision.Blocked(reason);
        }
        return _guard.Review(_inner.Assess(args, ctx), _inner.Name);
    }

    public string Describe(JsonElement args) => _inner.Describe(args);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        // 判定和执行之间可能隔着一次员工确认：确认回来时任务也许已经停了，这里再看一次
        if (!_guard.Allows(_inner))
        {
            return ToolResult.Fail($"「{_guard.Expert.Name}」不能使用 {_inner.Name}");
        }
        if (_guard.Refusal() is { } reason)
        {
            return ToolResult.Fail(reason);
        }
        var result = await _inner.ExecuteAsync(args, ctx, ct);
        var entry = _ledger.Add(_inner.Name, SafeDescribe(args), result);
        return result with { Output = $"[证据 {entry.Id}] {result.Output}" };
    }

    private string SafeDescribe(JsonElement args)
    {
        try
        {
            return _inner.Describe(args);
        }
        catch (Exception)
        {
            return _inner.Name;
        }
    }
}

/// <summary>
/// 核验专家交回的结论。分两步，不混为一谈：
/// 1. 来源是不是真的——引用的证据编号存在、那次调用成功、文件确实在；
/// 2. 证据能不能支持这句话——结论里的数字、编号在引用的结果里找得到。
/// 第二步只能做到「找得到」这个程度；比对不了的（结论里没有可比的数字或编号）只标来源已核实，不冒充「有依据」。
/// </summary>
public static class EvidenceVerifier
{
    private static readonly Regex Checkable = new(@"[A-Za-z]+[-_]?\d[\w-]*|\d[\d,]*(?:\.\d+)?%?", RegexOptions.Compiled);

    public static void Verify(IList<SubtaskFinding> findings, EvidenceLedger ledger, IReadOnlyCollection<string> allowedFiles)
    {
        foreach (var finding in findings)
        {
            var outputs = new List<string>();
            var bad = 0;
            foreach (var ev in finding.Evidence)
            {
                if (ev.Kind == "tool")
                {
                    var entry = ledger.Get(ev.Ref);
                    ev.Check = entry is null ? "missing" : entry.Ok ? "ok" : "failed_call";
                    if (entry is not null)
                    {
                        ev.Label = ev.Label.Length > 0 ? ev.Label : $"{entry.ToolName}：{entry.Summary}";
                        if (entry.Ok)
                        {
                            outputs.Add(entry.Output);
                        }
                    }
                }
                else
                {
                    var ok = File.Exists(ev.Ref) && (ledger.ProducedFiles.Contains(ev.Ref, StringComparer.OrdinalIgnoreCase)
                        || allowedFiles.Contains(ev.Ref, StringComparer.OrdinalIgnoreCase));
                    ev.Check = ok ? "ok" : "missing";
                    ev.Label = ev.Label.Length > 0 ? ev.Label : Path.GetFileName(ev.Ref);
                }
                if (ev.Check != "ok")
                {
                    bad++;
                }
            }

            if (finding.Evidence.Count == 0)
            {
                finding.Verification = "unverified";
                AppendNote(finding, "没有给出来源");
            }
            else if (bad > 0)
            {
                finding.Verification = "verification_failed";
                AppendNote(finding, $"{bad} 处引用的来源不存在或那次调用失败了");
            }
            else if (outputs.Count == 0)
            {
                // 只引用了文件：文件在，但内容没有比对
                finding.Verification = "source_verified";
                AppendNote(finding, "来源已核实，内容没有自动比对");
            }
            else
            {
                var (found, total) = Match(finding.Statement, outputs);
                if (total == 0)
                {
                    finding.Verification = "source_verified";
                    AppendNote(finding, "来源已核实；结论里没有可以自动比对的数字或编号");
                }
                else if (found * 10 >= total * 6)
                {
                    finding.Verification = "supported";
                }
                else
                {
                    finding.Verification = "unverified";
                    AppendNote(finding, $"结论里的 {total - found}/{total} 个数字或编号在引用的结果里找不到");
                }
            }
        }
    }

    /// <summary>结论里可比对的数字和编号，有几个能在证据里找到。</summary>
    public static (int Found, int Total) Match(string statement, IReadOnlyList<string> outputs)
    {
        var haystack = Normalize(string.Join("\n", outputs));
        var tokens = Checkable.Matches(statement)
            .Select(m => Normalize(m.Value).TrimEnd('%'))
            .Where(t => t.Length > 0 && !IsTrivial(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var found = tokens.Count(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        return (found, tokens.Count);
    }

    // 「1」「2」这种序号、年份之外的个位数太容易碰巧出现，不拿来比
    private static bool IsTrivial(string token) => token.Length == 1 && char.IsDigit(token[0]);

    private static string Normalize(string text) => text.Replace(",", "").Replace("，", "");

    private static void AppendNote(SubtaskFinding finding, string note) =>
        finding.Note = finding.Note.Length == 0 ? $"【系统核验】{note}" : $"{finding.Note}\n【系统核验】{note}";
}
