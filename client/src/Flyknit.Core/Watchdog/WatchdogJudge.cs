using System.Text;
using System.Text.Json;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;

namespace Flyknit.Core.Watchdog;

/// <summary>模型的判断。</summary>
public sealed record WatchJudgement(WatchVerdict Verdict, string Reason, string Advice);

/// <summary>
/// 请便宜模型看一眼（docs/c3-watchdog.md 第 3.2 节）：任务目标、计划、最近 20 步的摘要。
/// 只在程序判「可疑」时才调；走 title 场景，用途记成 watchdog。
/// 给模型看的东西都先过 <see cref="SecretRedactor"/>，工具输出只取开头一小段，不带附件、图片、记忆。
/// 回答解析不了按「在推进」处理（宁可漏判，不误伤）；调用失败返回 null。
/// </summary>
public static class WatchdogJudge
{
    public const int GoalChars = 300;

    private const string SystemPrompt = """
        你是任务看门狗。下面是一个 AI 助手正在员工电脑上执行的任务：目标、计划、最近的操作。
        判断它最近这段时间是否在实质推进目标：
        - progressing：在推进（读资料、试方案、产出东西都算，只要是朝着目标）；
        - stalled：卡住了，换着花样重复、来回试同一类办法却没有新结果；
        - drifting：一直在做事，但做的事情和目标关系不大。
        拿不准时选 progressing。只输出一行 JSON，不要别的文字：
        {"verdict":"progressing|stalled|drifting","reason":"一句话说明（中文）","advice":"给助手的一句建议（中文）"}
        """;

    /// <summary>一步操作的关键参数：路径、命令、搜索词，最多 80 字。</summary>
    public static string BriefOf(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "command", "path", "pattern", "query", "url", "name", "task" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    {
                        return Head(s, 80);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return "";
    }

    /// <summary>开头一段，先脱敏、压成一行。</summary>
    public static string Head(string text, int chars)
    {
        var one = string.Join(' ', SecretRedactor.Redact(text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= chars ? one : one[..chars] + "…";
    }

    /// <summary>拼给模型看的内容。</summary>
    public static string Describe(ProgressSignals s, long tokensUsed, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("【任务目标】").Append(Head(s.Goal, GoalChars)).Append('\n');
        if (s.Plan.Count > 0)
        {
            sb.Append("【计划】\n");
            foreach (var p in s.Plan)
            {
                sb.Append(p.Status switch { "completed" => "[x] ", "in_progress" => "[>] ", _ => "[ ] " }).Append(Head(p.Step, 80)).Append('\n');
            }
        }
        else
        {
            sb.Append("【计划】没有列计划\n");
        }
        var minutes = (int)Math.Max(0, (now - s.StartedAt).TotalMinutes);
        sb.Append($"【已经】{s.TotalSteps} 步、{minutes} 分钟、约 {tokensUsed} tokens；最近 {s.StepsSinceProgress} 步、{(int)s.IdleFor.TotalMinutes} 分钟没有新的进展信号\n");
        sb.Append("【最近的操作】\n");
        var i = 0;
        foreach (var n in s.Recent)
        {
            sb.Append(++i).Append(". ").Append(n.Tool);
            if (n.Brief.Length > 0)
            {
                sb.Append(" ").Append(n.Brief);
            }
            sb.Append(n.Ok ? " → " : " → 失败：").Append(n.ResultHead).Append('\n');
        }
        return sb.ToString();
    }

    public static ChatRequest Request(string description) => new()
    {
        Scene = Scenes.Title,
        Purpose = Purposes.Watchdog,
        Stream = false,
        MaxTokens = 200,
        Temperature = 0,
        ExtraBody = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["enable_thinking"] = false },
        Messages = new[] { ChatMessage.System(SystemPrompt), ChatMessage.User(description) },
    };

    /// <summary>解析模型的回答；认不出就当在推进。</summary>
    public static WatchJudgement Parse(string content)
    {
        var text = content ?? "";
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                var root = doc.RootElement;
                string Str(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
                var verdict = Str("verdict").ToLowerInvariant() switch
                {
                    "stalled" => WatchVerdict.Stalled,
                    "drifting" => WatchVerdict.Drifting,
                    _ => WatchVerdict.Progressing,
                };
                return new WatchJudgement(verdict, Head(Str("reason"), 120), Head(Str("advice"), 200));
            }
            catch (JsonException)
            {
            }
        }
        return new WatchJudgement(WatchVerdict.Progressing, "", "");
    }

    /// <summary>请模型判断。失败（离线、配额用完、超时）返回 null。</summary>
    public static async Task<WatchJudgement?> JudgeAsync(IChatGateway gateway, ProgressSignals s, long tokensUsed, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var turn = await gateway.CompleteAsync(Request(Describe(s, tokensUsed, DateTimeOffset.Now)), null, cts.Token);
            return Parse(turn.Content);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>附在下一个工具结果后面给 AI 看的提醒。</summary>
    public static string ReminderText(WatchJudgement? j)
    {
        var why = j is { Reason.Length: > 0 } ? j.Reason : "最近一段时间没有新的进展";
        var what = j?.Verdict == WatchVerdict.Drifting ? "最近做的事情和目标关系不大" : "最近一段时间没有实质进展";
        var advice = j is { Advice.Length: > 0 } ? $"\n建议：{j.Advice}" : "";
        return $"""


            【系统提醒（看门狗）】{what}（{why}）。{advice}
            先对照目标和计划想清楚：这条路行不通就换办法，或者用 update_plan 调整计划；缺信息或权限就停下来向用户说明卡在哪里。
            如果目标已经达成，直接总结回答。
            """;
    }
}
