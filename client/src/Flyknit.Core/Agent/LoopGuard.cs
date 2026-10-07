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

    /// <summary>不参与检测的工具（更新计划这类本来就会反复调用、也不贵）。</summary>
    public static readonly HashSet<string> Exempt = new(StringComparer.Ordinal) { "update_plan" };

    private readonly LinkedList<(string Call, string Result)> _recent = new();
    private readonly HashSet<string> _nudged = new(StringComparer.Ordinal);

    /// <summary>最近一次判定为打转的调用（工具名），用于提示文字。</summary>
    public string? LastTool { get; private set; }

    /// <summary>本轮提醒过几次。</summary>
    public int Nudges { get; private set; }

    public LoopVerdict Observe(ToolCall call, string result)
    {
        if (Exempt.Contains(call.Name))
        {
            return LoopVerdict.Ok;
        }
        var signature = Signature(call);
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

    /// <summary>附在工具结果后面给模型看的提醒。</summary>
    public static string NudgeText(string tool) => $"""


        【系统提醒】你已经多次用相同的参数调用 {tool}，结果没有新进展。不要再原样重复：
        - 如果是在等某件事完成（文件出现、画面变化、服务启动），改用一条命令在里面循环等待并设超时（例如 PowerShell 的 while + Start-Sleep），一次拿到结果；
        - 如果这个办法行不通，换一种办法；
        - 如果缺少信息或权限，直接停下来向用户说明卡在哪里、需要什么。
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
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()!.Trim()),
        _ => e.GetRawText(),
    };

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.Trim())));
}
