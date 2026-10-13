using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Security;

namespace Flyknit.Core.Tools;

public sealed record AskOption(string Label, string Description);

public sealed record AskQuestion(string Header, string Question, IReadOnlyList<AskOption> Options, bool MultiSelect);

/// <summary>员工对一个问题的回答：点选的选项，和「其他」里自己写的话。</summary>
public sealed record AskAnswer(IReadOnlyList<string> Selected, string Other);

public sealed class AskRequest
{
    public required string ConversationId { get; init; }
    public required string CallId { get; init; }
    public required IReadOnlyList<AskQuestion> Questions { get; init; }
}

/// <summary>把问题交给员工（界面上的选项卡片）。返回 null = 员工没有回答（跳过、超时、界面还没准备好）。</summary>
public interface IQuestionHandler
{
    Task<IReadOnlyList<AskAnswer>?> AskAsync(AskRequest request, CancellationToken ct);
}

/// <summary>
/// 信息不够时用选项向员工提问：员工点选就行，不用打字；回答作为工具结果交回，任务在同一轮里接着做。
/// 只给有人看着的对话用——定时任务和派出去的专家没有这个工具（没人回答）。
/// </summary>
public sealed class AskUserTool : ITool, ILongRunningTool
{
    public const string ToolName = "ask_user";
    public const int MaxQuestions = 4;
    public const int MinOptions = 2;
    public const int MaxOptions = 4;

    /// <summary>一轮任务最多问几次：问多了员工会烦，也说明该自己判断了。</summary>
    public const int MaxPerRun = 1;

    private readonly IQuestionHandler _handler;
    private readonly TimeSpan _wait;
    private readonly ConcurrentDictionary<string, int> _asked = new();

    /// <param name="wait">等员工回答多久，过了按「没有回答」处理。</param>
    public AskUserTool(IQuestionHandler handler, TimeSpan? wait = null)
    {
        _handler = handler;
        _wait = wait ?? TimeSpan.FromMinutes(30);
    }

    public string Name => ToolName;

    /// <summary>比自己的等待时间多一点，让工具先按「没有回答」收尾，而不是被外层当成超时。</summary>
    public TimeSpan Timeout => _wait + TimeSpan.FromMinutes(1);

    public string Description => $"""
        信息不够、做不下去时，用选项向员工提问：员工点一下就能回答，不用打字。回答会作为这个工具的结果返回，拿到后接着把任务做完。
        只在真的需要员工决定或提供信息时用：能从对话、文件、记忆里查到的先自己查，能按常理判断的直接做。
        一轮最多问 {MaxPerRun} 次，一次 1～{MaxQuestions} 个问题，每个问题 {MinOptions}～{MaxOptions} 个简短选项。
        界面会自动加「其他（自己输入）」，不要自己加「其他」选项；建议的选项放第一个，并在后面写「（推荐）」。
        要不要执行有风险的操作不用这个工具问，系统会自动弹确认。
        """;

    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["questions"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = MaxQuestions,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["question"] = new JsonObject { ["type"] = "string", ["description"] = "完整的问题，以问号结尾" },
                        ["header"] = new JsonObject { ["type"] = "string", ["description"] = "很短的标签（12 个字以内），如「统计口径」" },
                        ["options"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["minItems"] = MinOptions,
                            ["maxItems"] = MaxOptions,
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["label"] = new JsonObject { ["type"] = "string", ["description"] = "选项，1～5 个词" },
                                    ["description"] = new JsonObject { ["type"] = "string", ["description"] = "选了会怎样（可选）" },
                                },
                                ["required"] = new JsonArray("label"),
                            },
                        },
                        ["multi_select"] = new JsonObject { ["type"] = "boolean", ["description"] = "能不能选多个，默认只能选一个" },
                    },
                    ["required"] = new JsonArray("question", "options"),
                },
            },
        },
        ["required"] = new JsonArray("questions"),
    };

    public PolicyDecision Assess(JsonElement args, ToolContext ctx) => PolicyDecision.Auto("向员工提问");

    public string Describe(JsonElement args)
    {
        var (questions, _) = Parse(args);
        return questions.Count switch
        {
            0 => "向员工提问",
            1 => questions[0].Question,
            _ => $"{questions[0].Question}（共 {questions.Count} 个问题）",
        };
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var callId = ctx.CurrentCallId;
        var (questions, error) = Parse(args);
        if (error is not null)
        {
            return ToolResult.Fail(error + "。请改好后重新调用。");
        }
        var run = ctx.RunId.Length > 0 ? ctx.RunId : ctx.ConversationId;
        if (_asked.Count > 1000)
        {
            _asked.Clear();
        }
        if (_asked.AddOrUpdate(run, 1, (_, n) => n + 1) > MaxPerRun)
        {
            return ToolResult.Fail("这一轮已经问过员工了，不要再问。按已有的信息继续，把你的假设写在回答里。");
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(_wait);
        IReadOnlyList<AskAnswer>? answers;
        try
        {
            answers = await _handler.AskAsync(new AskRequest { ConversationId = ctx.ConversationId, CallId = callId, Questions = questions }, wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            answers = null; // 等太久了
        }
        if (answers is null)
        {
            return new ToolResult(true, """
                员工没有回答（可能暂时不在电脑前，或者选择了跳过）。不要再问：按最稳妥的理解继续，
                并在回答里写明你做了哪些假设；必须由员工决定的事放到最后说明。
                """);
        }
        return new ToolResult(true, Format(questions, answers));
    }

    internal static string Format(IReadOnlyList<AskQuestion> questions, IReadOnlyList<AskAnswer> answers)
    {
        var sb = new StringBuilder("员工的回答：\n");
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var a = i < answers.Count ? answers[i] : new AskAnswer(Array.Empty<string>(), "");
            // 只认卡片上真有的选项，防止界面传来别的东西
            var picked = a.Selected.Where(s => q.Options.Any(o => o.Label == s)).Distinct().ToList();
            var other = a.Other.Trim();
            if (other.Length > 1000)
            {
                other = other[..1000] + "…";
            }
            var parts = new List<string>();
            if (picked.Count > 0)
            {
                parts.Add(string.Join("、", picked));
            }
            if (other.Length > 0)
            {
                parts.Add($"员工补充：{other}");
            }
            sb.AppendLine($"{i + 1}. {q.Question}");
            sb.AppendLine($"   → {(parts.Count > 0 ? string.Join("；", parts) : "（没有回答这一题，按最稳妥的理解处理）")}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>检查模型给的问题。不合格就说清楚哪里不对，让它改了再问。</summary>
    internal static (IReadOnlyList<AskQuestion> Questions, string? Error) Parse(JsonElement args)
    {
        var list = new List<AskQuestion>();
        if (!args.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array || qs.GetArrayLength() == 0)
        {
            return (list, "至少要有一个问题（questions）");
        }
        if (qs.GetArrayLength() > MaxQuestions)
        {
            return (list, $"一次最多问 {MaxQuestions} 个问题");
        }
        var n = 0;
        foreach (var q in qs.EnumerateArray())
        {
            n++;
            if (q.ValueKind != JsonValueKind.Object)
            {
                return (list, $"第 {n} 个问题格式不对");
            }
            var question = Clip(q.Str("question"), 300);
            if (question.Length == 0)
            {
                return (list, $"第 {n} 个问题没有写问题内容");
            }
            if (!q.TryGetProperty("options", out var os) || os.ValueKind != JsonValueKind.Array)
            {
                return (list, $"第 {n} 个问题没有选项");
            }
            var options = new List<AskOption>();
            foreach (var o in os.EnumerateArray())
            {
                var label = Clip(o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : o.Str("label"), 60);
                if (label.Length == 0 || IsOther(label))
                {
                    continue; // 「其他」界面会自动加
                }
                if (options.Any(x => x.Label == label))
                {
                    return (list, $"第 {n} 个问题有重复的选项「{label}」");
                }
                options.Add(new AskOption(label, o.ValueKind == JsonValueKind.Object ? Clip(o.Str("description"), 200) : ""));
            }
            if (options.Count < MinOptions || options.Count > MaxOptions)
            {
                return (list, $"第 {n} 个问题要有 {MinOptions}～{MaxOptions} 个选项（不算「其他」），现在有 {options.Count} 个");
            }
            var multi = q.TryGetProperty("multi_select", out var m) && m.ValueKind == JsonValueKind.True;
            list.Add(new AskQuestion(Clip(q.Str("header"), 12), question, options, multi));
        }
        return (list, null);
    }

    private static bool IsOther(string label) =>
        label is "其他" or "其它" or "其他（自己输入）" or "Other" or "other" or "Khác";

    private static string Clip(string text, int max)
    {
        var t = text.Trim().ReplaceLineEndings(" ");
        return t.Length > max ? t[..max] : t;
    }
}
