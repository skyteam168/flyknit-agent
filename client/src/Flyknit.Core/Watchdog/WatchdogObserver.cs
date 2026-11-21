using Flyknit.Core.Agent;
using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Watchdog;

/// <summary>
/// 把一轮的事件顺带记成进展信号，其余原样转给宿主自己的观察者。不改变任何行为。
/// </summary>
public sealed class WatchdogObserver(IAgentObserver inner, ProgressSignals signals) : IAgentObserver
{
    public void OnContent(string delta) => inner.OnContent(delta);
    public void OnReasoning(string delta) => inner.OnReasoning(delta);
    public void OnSources(IReadOnlyList<WebSource> sources) => inner.OnSources(sources);

    public void OnAssistantMessage(ChatMessage message)
    {
        // 回答员工（有正文、不调工具）算进展；只调工具的中间步骤不算
        if (message.ToolCalls.Count == 0 && message.Content.Trim().Length > 0)
        {
            signals.Progressed();
        }
        inner.OnAssistantMessage(message);
    }

    public void OnToolStarted(ToolCall call, string summary, PolicyDecision decision)
    {
        signals.ToolStarted();
        inner.OnToolStarted(call, summary, decision);
    }

    public void OnToolFinished(ToolCall call, ToolResult result, string decision)
    {
        signals.ToolFinished(call, result);
        inner.OnToolFinished(call, result, decision);
    }

    public void OnToolMessage(ChatMessage message) => inner.OnToolMessage(message);

    public void OnPlanUpdated(IReadOnlyList<PlanItem> plan)
    {
        signals.PlanUpdated(plan);
        inner.OnPlanUpdated(plan);
    }

    public void OnContextCompacted(Context.CompactionInfo info) => inner.OnContextCompacted(info);
    public void OnInterjection(ChatMessage message) => inner.OnInterjection(message);

    public void OnOutputsProduced(IReadOnlyList<OutputFile> files)
    {
        if (files.Count > 0)
        {
            signals.Progressed();
        }
        inner.OnOutputsProduced(files);
    }

    public void OnNotice(string text) => inner.OnNotice(text);
}
