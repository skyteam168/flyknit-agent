using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Experts.Delegation;

/// <summary>
/// 专家的「交卷」工具。参数就是交回主助手的结构；用函数调用的格式约束字段，比让模型在正文里写 JSON 可靠。
///
/// 交卷成功后本次运行立刻结束（<see cref="ToolResult.EndRun"/>），不再多调一轮模型。
/// 只认第一份有效的卷：重复交卷不会覆盖先前的结果。格式不对的退回去让专家改，最多改几次。
/// </summary>
public sealed class SubmitResultTool : ITool
{
    public const string ToolName = "submit_result";

    /// <summary>格式不对最多退回这么多次，再不对就按失败处理。</summary>
    public const int MaxInvalidAttempts = 3;

    private const int MaxFindings = 30;
    private const int MaxListItems = 15;
    private const int MaxTextChars = 2000;

    private static readonly Regex EvidenceId = new(@"^E\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SubtaskState _state;
    private readonly object _gate = new();

    public SubmitResultTool(SubtaskState state)
    {
        _state = state;
    }

    /// <summary>第一份有效的卷。没交过是 null。</summary>
    public SubtaskSubmission? Submission { get; private set; }

    public int InvalidAttempts { get; private set; }

    public string Name => ToolName;

    public string Description => """
        交卷：把这个子任务的结果交回给主助手，调用后本次任务立即结束，不能再做别的。
        - status：completed 完成 / partial 只完成了一部分 / needs_input 缺关键信息没法继续（在 open_questions 里写清缺什么）/ failed 做不了。
        - findings：每条结论一句话，evidence 填支撑它的证据编号（工具结果开头的「证据 E3」）或产出文件的完整路径。
          只写你真正查到的；推测要在 note 里说明是推测。数据范围、时间范围、口径写进 scope。
        - 没查到的、没权限的、不确定的写进 open_questions，不要编。
        """;

    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["status"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(SubtaskStatuses.Submittable.Select(s => (JsonNode)s).ToArray()),
            },
            ["summary"] = new JsonObject { ["type"] = "string", ["description"] = "一句话结论" },
            ["findings"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["statement"] = new JsonObject { ["type"] = "string", ["description"] = "结论" },
                        ["evidence"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "证据编号（E3）或文件完整路径",
                        },
                        ["scope"] = new JsonObject { ["type"] = "string", ["description"] = "数据范围、时间范围、口径" },
                        ["note"] = new JsonObject { ["type"] = "string", ["description"] = "推测、证据不足、需要注意的地方" },
                    },
                    ["required"] = new JsonArray("statement", "evidence"),
                },
            },
            ["open_questions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["next_suggestions"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["artifacts"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "产出文件的完整路径",
            },
            ["confidence"] = new JsonObject { ["type"] = "number", ["description"] = "0~1，可不填" },
        },
        ["required"] = new JsonArray("status", "summary", "findings"),
    };

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto("交卷");

    public string Describe(JsonElement args) => "交回结果";

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Submission is not null)
            {
                return Task.FromResult(ToolResult.Success("已经交过卷了，以第一次为准。") with { EndRun = true });
            }
            if (!_state.IsActive)
            {
                // 已经取消或超时：不收卷，也别再接着跑
                return Task.FromResult(ToolResult.Success($"子任务已经结束（{_state.Status.Wire()}），这次交卷没有被接受。") with { EndRun = true });
            }
            var (submission, error) = Parse(args);
            if (submission is null)
            {
                InvalidAttempts++;
                var left = MaxInvalidAttempts - InvalidAttempts;
                return Task.FromResult(ToolResult.Fail(left > 0
                    ? $"交卷格式不对：{error}。请改好后重新调用 submit_result（还能改 {left} 次）。"
                    : $"交卷格式不对：{error}。已经改了 {MaxInvalidAttempts} 次，不再接受。"));
            }
            Submission = submission;
            return Task.FromResult(ToolResult.Success("已交卷。") with { EndRun = true });
        }
    }

    /// <summary>校验并整理参数。不合格时返回原因。</summary>
    public static (SubtaskSubmission? Submission, string Error) Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return (null, "参数必须是一个对象");
        }
        var status = args.Str("status").Trim().ToLowerInvariant();
        if (!SubtaskStatuses.Submittable.Contains(status))
        {
            return (null, $"status 只能是 {string.Join(" / ", SubtaskStatuses.Submittable)}");
        }
        var summary = Clip(args.Str("summary").Trim());
        if (summary.Length == 0)
        {
            return (null, "summary 不能为空");
        }

        var findings = new List<SubtaskFinding>();
        if (args.TryGetProperty("findings", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                return (null, "findings 必须是数组");
            }
            foreach (var item in list.EnumerateArray().Take(MaxFindings))
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return (null, "findings 的每一项必须是对象");
                }
                var statement = Clip(item.Str("statement").Trim());
                if (statement.Length == 0)
                {
                    return (null, "findings 里有一条 statement 是空的");
                }
                var evidence = new List<SubtaskEvidence>();
                if (item.TryGetProperty("evidence", out var refs) && refs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in refs.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Take(10))
                    {
                        var value = r.GetString()!.Trim();
                        if (value.Length == 0)
                        {
                            continue;
                        }
                        var isTool = EvidenceId.IsMatch(value);
                        evidence.Add(new SubtaskEvidence
                        {
                            Ref = isTool ? value.ToUpperInvariant() : FullPathOrRaw(value),
                            Kind = isTool ? "tool" : "file",
                        });
                    }
                }
                var scope = Clip(item.Str("scope").Trim());
                var note = Clip(item.Str("note").Trim());
                findings.Add(new SubtaskFinding
                {
                    Id = $"F{findings.Count + 1}",
                    Statement = statement,
                    Evidence = evidence,
                    Note = scope.Length > 0 ? (note.Length > 0 ? $"范围：{scope}\n{note}" : $"范围：{scope}") : note,
                });
            }
        }
        if (status == "completed" && findings.Count == 0)
        {
            return (null, "status 为 completed 时至少要有一条 findings");
        }

        double? confidence = null;
        if (args.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var cv))
        {
            confidence = Math.Clamp(cv, 0, 1);
        }

        return (new SubtaskSubmission
        {
            Status = status,
            Summary = summary,
            Findings = findings,
            OpenQuestions = Strings(args, "open_questions"),
            NextSuggestions = Strings(args, "next_suggestions"),
            Artifacts = Strings(args, "artifacts").Select(FullPathOrRaw).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Confidence = confidence,
        }, "");
    }

    private static List<string> Strings(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => Clip(x.GetString()!.Trim())).Where(x => x.Length > 0).Take(MaxListItems).ToList()
            : new List<string>();

    private static string Clip(string text) => text.Length > MaxTextChars ? text[..MaxTextChars] + "…" : text;

    private static string FullPathOrRaw(string path)
    {
        try
        {
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : path;
        }
        catch (Exception)
        {
            return path;
        }
    }
}
