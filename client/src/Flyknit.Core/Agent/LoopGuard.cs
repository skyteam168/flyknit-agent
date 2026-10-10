using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flyknit.Core.Chat;

namespace Flyknit.Core.Agent;

public enum LoopVerdict
{
    /// <summary>正常。</summary>
    Ok,

    /// <summary>开始打转了：提醒模型换个办法。</summary>
    Nudge,

    /// <summary>提醒过了还在打转：停下来，别再浪费。</summary>
    Stop,
}

/// <summary>
/// 空转检测。模型偶尔会卡在同一个操作上反复试（同样的命令、同样的参数），每试一次都要把整段上下文再发一遍，
/// token 烧得很快却没有进展。这里盯着最近的工具调用：
/// - 同一个调用连结果都一模一样（原地打转）：出现 3 次提醒一次，提醒后再出现 2 次就停；
/// - 同一个调用结果在变（轮询等待，比如反复截图看画面变没变）：出现 4 次提醒改用一条带等待的命令，到 8 次就停。
/// 不同调用交替出现（A、B、A、B…）也算在内，因为按调用分别计数。
/// </summary>
public sealed class LoopGuard
{
    public int Window { get; init; } = 12;
    public int SpinNudgeAt { get; init; } = 3;
    public int SpinStopAt { get; init; } = 5;
    public int PollNudgeAt { get; init; } = 4;
    public int PollStopAt { get; init; } = 8;

    /// <summary>
    /// 不参与上面那套检测的工具（更新计划本来就会反复调用：每做完一步更新一次）。
    /// 但“只更新计划、不干活”另算：中间没有任何别的操作、连着更新计划 <see cref="PlanOnlyNudgeAt"/> 次提醒，
    /// <see cref="PlanOnlyStopAt"/> 次停下。
    /// </summary>
    public static readonly HashSet<string> Exempt = new(StringComparer.Ordinal) { "update_plan" };

    public int PlanOnlyNudgeAt { get; init; } = 3;
    public int PlanOnlyStopAt { get; init; } = 5;

    /// <summary>
    /// 反复重写同一个文件：每次内容都略有不同，按「参数完全相同」认不出来，但其实是在原地改来改去。
    /// 按路径计数：同一个文件写到第 <see cref="RewriteNudgeAt"/> 次提醒，第 <see cref="RewriteStopAt"/> 次停下。
    /// </summary>
    public int RewriteNudgeAt { get; init; } = 4;
    public int RewriteStopAt { get; init; } = 8;

    /// <summary>按目标（文件路径）计数的工具：参数里的内容每次都不同，只看写的是哪个文件。</summary>
    public static readonly HashSet<string> TargetTools = new(StringComparer.Ordinal) { "write_file", "delete_path" };

    /// <summary>
    /// 不影响「是不是同一个操作」的参数：同一条命令把超时从 60 改成 120 再跑一遍，仍然是在重复。
    /// </summary>
    private static readonly HashSet<string> VolatileArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "timeout", "timeout_seconds", "timeout_ms", "description", "reason", "explanation",
    };

    private int _planOnly;
    private readonly Dictionary<string, int> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _targetNudged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本轮所有不同操作的签名：用来判断一轮有没有「新的进展」（全是做过的操作就不算）。</summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>最近一次观察的调用是不是本轮第一次出现（之前没做过一模一样的操作）。</summary>
    public bool LastWasNew { get; private set; }

    /// <summary>重写同一个文件的提醒文字（和参数完全相同的打转分开说，给的建议不一样）。</summary>
    public bool LastWasRewrite { get; private set; }

    private readonly LinkedList<(string Call, string Result)> _recent = new();
    private readonly HashSet<string> _nudged = new(StringComparer.Ordinal);

    /// <summary>最近一次判定为打转的调用（工具名），用于提示文字。</summary>
    public string? LastTool { get; private set; }

    /// <summary>本轮提醒过几次。</summary>
    public int Nudges { get; private set; }

    public LoopVerdict Observe(ToolCall call, string result)
    {
        LastWasRewrite = false;
        var signature = Signature(call);
        LastWasNew = _seen.Add(signature);
        if (Exempt.Contains(call.Name))
        {
            _planOnly++;
            if (_planOnly < PlanOnlyNudgeAt)
            {
                return LoopVerdict.Ok;
            }
            LastTool = call.Name;
            if (_planOnly == PlanOnlyNudgeAt)
            {
                Nudges++;
                return LoopVerdict.Nudge;
            }
            return _planOnly >= PlanOnlyStopAt ? LoopVerdict.Stop : LoopVerdict.Ok;
        }
        _planOnly = 0;
        if (TargetTools.Contains(call.Name) && Target(call) is { } target)
        {
            var rewrite = ObserveTarget(call.Name, target);
            if (rewrite != LoopVerdict.Ok)
            {
                LastTool = call.Name;
                LastWasRewrite = true;
                return rewrite;
            }
        }
        var resultHash = Hash(result);
        _recent.AddLast((signature, resultHash));
        while (_recent.Count > Window)
        {
            _recent.RemoveFirst();
        }

        var same = _recent.Count(r => r.Call == signature);
        var identical = _recent.Count(r => r.Call == signature && r.Result == resultHash);
        var spinning = identical >= SpinNudgeAt;
        var polling = same >= PollNudgeAt;
        if (!spinning && !polling)
        {
            return LoopVerdict.Ok;
        }
        LastTool = call.Name;
        if (_nudged.Contains(signature))
        {
            return identical >= SpinStopAt || same >= PollStopAt ? LoopVerdict.Stop : LoopVerdict.Ok;
        }
        _nudged.Add(signature);
        Nudges++;
        return LoopVerdict.Nudge;
    }

    private LoopVerdict ObserveTarget(string tool, string target)
    {
        var key = tool + "\n" + target;
        var n = _targets[key] = _targets.GetValueOrDefault(key) + 1;
        if (n >= RewriteStopAt)
        {
            return LoopVerdict.Stop;
        }
        if (n >= RewriteNudgeAt && _targetNudged.Add(key))
        {
            Nudges++;
            return LoopVerdict.Nudge;
        }
        return LoopVerdict.Ok;
    }

    /// <summary>写的是哪个文件（规范化路径，大小写不敏感）。参数解析不了就当没有目标。</summary>
    private static string? Target(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                && path.GetString() is { Length: > 0 } p)
            {
                return p.Trim().Replace('/', '\\').TrimEnd('\\');
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public const string RewriteText = """


        【系统提醒】你已经多次重写同一个文件。先停下来想清楚要改成什么样、一次写对，不要反复小改；
        如果是在等外部条件（比如别的程序处理完），先确认条件，而不是一直重写。做完了就直接总结回答。
        """;

    /// <summary>附在工具结果后面给模型看的提醒。</summary>
    public string NudgeFor(string tool) => LastWasRewrite ? RewriteText : NudgeText(tool);

    /// <summary>附在工具结果后面给模型看的提醒。</summary>
    public static string NudgeText(string tool) => Exempt.Contains(tool) ? PlanOnlyText : $"""


        【系统提醒】你已经多次用相同的参数调用 {tool}，结果没有新进展。不要再原样重复：
        - 如果是在等某件事完成（文件出现、画面变化、服务启动），改用一条命令在里面循环等待并设超时（例如 PowerShell 的 while + Start-Sleep），一次拿到结果；
        - 如果这个办法行不通，换一种办法；
        - 如果缺少信息或权限，直接停下来向用户说明卡在哪里、需要什么。
        """;

    public const string PlanOnlyText = """


        【系统提醒】计划已经更新好几次了，中间没有做任何实际操作。计划已经够用，直接开始执行下一步；
        如果是不知道怎么做下去，停下来告诉用户卡在哪里。
        """;

    /// <summary>每隔一段步数附上的自查提醒。</summary>
    public static string CheckpointText(int steps) => $"""


        【系统提醒】这一轮已经执行了 {steps} 步。请先对照计划检查进度：目标已经达成就直接总结回答；
        方法不奏效就换一种；需要用户提供信息或确认就停下来问。确实还在推进就继续。
        """;

    private static string Signature(ToolCall call)
    {
        // 参数按 JSON 规范化，避免空格、字段顺序不同被当成不同的调用
        string args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = Canonical(doc.RootElement);
        }
        catch (JsonException)
        {
            args = call.ArgumentsJson.Trim();
        }
        return call.Name + "\n" + args;
    }

    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject()
            .Where(p => !VolatileArgs.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        // 多个空格、换行和单个空格算同一个：模型重试时经常只改了排版
        JsonValueKind.String => JsonSerializer.Serialize(Spaces.Replace(e.GetString()!.Trim(), " ")),
        _ => e.GetRawText(),
    };

    private static readonly System.Text.RegularExpressions.Regex Spaces = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.Trim())));
}
