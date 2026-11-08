using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Agent;

/// <summary>
/// 一轮任务做到哪了：当前阶段（计划里进行中的那一步）、计划、步数、工具调用、失败、确认、最后一个工具调用的状态。
///
/// Agent 循环每执行一个工具、改一次计划、请员工确认一次就更新一份，宿主存进本机数据库（和消息一样每步落盘，
/// 不需要 Redis 之类的外部服务）。程序被关、电脑重启后，「继续」时据此告诉 AI 精确的断点：
/// 计划里哪几步做完了、最后那个调用有没有执行完——没执行完的可能做了一半，要先核对再继续。
/// </summary>
public sealed class RunCheckpoint
{
    private readonly object _gate = new();

    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("steps")] public int Steps { get; set; }
    [JsonPropertyName("toolCalls")] public int ToolCalls { get; set; }
    [JsonPropertyName("failures")] public int Failures { get; set; }
    [JsonPropertyName("approvalsAsked")] public int ApprovalsAsked { get; set; }
    [JsonPropertyName("approvalsRejected")] public int ApprovalsRejected { get; set; }
    [JsonPropertyName("plan")] public List<PlanItem> Plan { get; set; } = new();
    [JsonPropertyName("lastTool")] public CheckpointTool? LastTool { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>当前阶段：计划里进行中的那一步；没有就是第一个没完成的；都完成了为空。</summary>
    [JsonIgnore]
    public string Stage =>
        Plan.FirstOrDefault(p => p.Status == "in_progress")?.Step
        ?? Plan.FirstOrDefault(p => p.Status != "completed")?.Step
        ?? "";

    public void ToolStarted(string callId, string name, string summary)
    {
        lock (_gate)
        {
            ToolCalls++;
            LastTool = new CheckpointTool(callId, name, Clip(summary, 200), "started");
        }
    }

    public void ToolFinished(string callId, bool ok)
    {
        lock (_gate)
        {
            if (!ok)
            {
                Failures++;
            }
            if (LastTool is { } last && last.CallId == callId)
            {
                LastTool = last with { State = ok ? "ok" : "failed" };
            }
        }
    }

    public void Asked(bool rejected)
    {
        lock (_gate)
        {
            ApprovalsAsked++;
            if (rejected)
            {
                ApprovalsRejected++;
            }
        }
    }

    public void PlanUpdated(IReadOnlyList<PlanItem> plan)
    {
        lock (_gate)
        {
            Plan = plan.ToList();
        }
    }

    public string ToJson()
    {
        lock (_gate)
        {
            UpdatedAt = DateTimeOffset.Now;
            return JsonSerializer.Serialize(this, Json);
        }
    }

    public static RunCheckpoint? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RunCheckpoint>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>「继续」时发给 AI 的断点说明。没有断点（老版本留下的中断）时给通用说法。</summary>
    public static string ResumeNote(RunCheckpoint? cp)
    {
        var sb = new StringBuilder("继续上次没做完的任务（程序上次在执行中被关闭了）。");
        if (cp is null)
        {
            sb.Append("先看看已经完成了哪些步骤、产出的文件在不在、对不对，最后那一步如果没执行完要先确认结果，再接着把任务做完。");
            return sb.ToString();
        }
        sb.AppendLine();
        sb.AppendLine($"断点记录（{cp.UpdatedAt:MM-dd HH:mm}）：已走 {cp.Steps} 步、调用工具 {cp.ToolCalls} 次（失败 {cp.Failures} 次），请员工确认 {cp.ApprovalsAsked} 次（拒绝 {cp.ApprovalsRejected} 次）。");
        if (cp.Plan.Count > 0)
        {
            sb.AppendLine("计划进度：");
            foreach (var p in cp.Plan)
            {
                var mark = p.Status switch { "completed" => "✓", "in_progress" => "→", _ => "·" };
                sb.AppendLine($"{mark} {p.Step}");
            }
        }
        if (cp.LastTool is { } last)
        {
            sb.AppendLine(last.State == "started"
                ? $"最后一个操作 {last.Name}（{last.Summary}）开始后程序就关了，不知道有没有执行完、可能只做了一半：先核对它的结果（文件在不在、内容对不对），不要直接重做。"
                : $"最后一个操作 {last.Name}（{last.Summary}）已{(last.State == "ok" ? "完成" : "失败")}。");
        }
        sb.Append(cp.Stage.Length > 0 ? $"从「{cp.Stage}」接着做，" : "");
        sb.Append("已经完成的步骤不要重做；做完后照常总结。");
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>最后一个工具调用：started 开始了还没结果 / ok / failed。</summary>
public sealed record CheckpointTool(
    [property: JsonPropertyName("callId")] string CallId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("state")] string State);
