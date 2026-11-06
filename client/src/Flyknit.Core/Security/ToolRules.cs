using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flyknit.Core.Security;

/// <summary>IT 在后台写的一条工具规则（服务端 routers/tool_rules.py）。</summary>
public sealed record ToolRule
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    /// <summary>工具名，逗号分开，支持 * 通配；空或 * 表示所有工具。</summary>
    [JsonPropertyName("tools")] public string Tools { get; init; } = "*";
    /// <summary>参数（JSON 原文）要匹配的正则；空表示不看参数。</summary>
    [JsonPropertyName("match")] public string Match { get; init; } = "";
    /// <summary>block / confirm / note / none。</summary>
    [JsonPropertyName("action")] public string Action { get; init; } = "note";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    /// <summary>结果里要打码的内容（正则）。</summary>
    [JsonPropertyName("redact")] public string Redact { get; init; } = "";
}

/// <summary>
/// 工具规则（同 Claude Code 的 hooks、DeepSeek Harness 的 tools/pre-execute、post-execute）：AI 调用工具前、拿到结果后，按 IT 的规则
/// 拦下、要求确认、加一句公司规定、给结果打码。只能收紧：不会把本来要确认或被拦的操作放行。
/// 规则写坏了（正则不合法）那一条跳过，不影响别的；正则匹配有超时，防止写得很慢的正则卡住任务。
/// </summary>
public sealed class ToolRules
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private readonly List<Compiled> _rules = new();

    private sealed record Compiled(ToolRule Rule, Regex[] Tools, Regex? Match, Regex? Redact);

    public static readonly ToolRules None = new(Array.Empty<ToolRule>());

    public ToolRules(IEnumerable<ToolRule> rules)
    {
        foreach (var r in rules.Where(r => r.Enabled))
        {
            try
            {
                var tools = (string.IsNullOrWhiteSpace(r.Tools) ? "*" : r.Tools)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Glob)
                    .ToArray();
                var match = r.Match.Trim().Length > 0 ? new Regex(r.Match, RegexOptions.IgnoreCase, RegexTimeout) : null;
                var redact = r.Redact.Trim().Length > 0 ? new Regex(r.Redact, RegexOptions.None, RegexTimeout) : null;
                _rules.Add(new Compiled(r, tools, match, redact));
            }
            catch (ArgumentException)
            {
                // 正则写坏了：这一条不用
            }
        }
    }

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>
    /// 调用前：按规则收紧判定。block 胜过一切；confirm 把自动执行提到要确认，而且不能被「以后自动允许」「本次都允许」跳过。
    /// </summary>
    public PolicyDecision Apply(string tool, string argsJson, PolicyDecision decision)
    {
        foreach (var r in Matching(tool, argsJson))
        {
            var why = r.Rule.Message.Length > 0 ? r.Rule.Message : $"公司规则「{r.Rule.Name}」";
            if (r.Rule.Action == "block")
            {
                return PolicyDecision.Blocked(why) with { Effect = decision.Effect };
            }
            if (r.Rule.Action == "confirm" && decision.Level != RiskLevel.Blocked)
            {
                var reason = decision.Level == RiskLevel.Confirm && decision.Reason.Length > 0 ? $"{why}；{decision.Reason}" : why;
                decision = PolicyDecision.Confirm(reason) with { Effect = decision.Effect, AlwaysAsk = true };
            }
        }
        return decision;
    }

    /// <summary>拿到结果后：note 规则的话加在最前面，redact 规则把匹配的内容替换成 ***。</summary>
    public string Annotate(string tool, string argsJson, string output)
    {
        var notes = new StringBuilder();
        foreach (var r in Matching(tool, argsJson))
        {
            if (r.Redact is not null)
            {
                try
                {
                    output = r.Redact.Replace(output, "***");
                }
                catch (RegexMatchTimeoutException)
                {
                    // 太慢：这一条这次不打码，但也不能把原文漏出去——整段换掉
                    output = $"（结果包含按公司规则「{r.Rule.Name}」需要打码的内容，打码超时，已隐藏）";
                }
            }
            if (r.Rule.Action == "note" && r.Rule.Message.Length > 0)
            {
                notes.Append("【公司规定】").AppendLine(r.Rule.Message);
            }
        }
        return notes.Length > 0 ? notes + output : output;
    }

    private IEnumerable<Compiled> Matching(string tool, string argsJson) => _rules.Where(r =>
    {
        if (!r.Tools.Any(t => t.IsMatch(tool)))
        {
            return false;
        }
        if (r.Match is null)
        {
            return true;
        }
        try
        {
            return r.Match.IsMatch(argsJson);
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // 判断不了就当匹配：规则只收紧，宁可多拦
        }
    });

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase, RegexTimeout);
}
