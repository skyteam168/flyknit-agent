using Flyknit.Core.Chat;

namespace Flyknit.Core.Context;

/// <summary>
/// 一次模型调用的输入都花在哪了：系统提示（含记忆、技能清单、摘要）、工具定义、对话（用户和模型说的话、模型发起的调用）、工具输出。
/// 按估算的比例把真实输入 token 拆开，在执行链路里给人看：一眼看出是哪一块把每次请求撑到几万 token。
/// </summary>
public sealed record PromptBreakdown(int System, int Tools, int Conversation, int ToolOutputs)
{
    public int Total => System + Tools + Conversation + ToolOutputs;

    public static PromptBreakdown Of(IReadOnlyList<ChatMessage> history, int toolTokens)
    {
        int system = 0, conversation = 0, outputs = 0;
        foreach (var m in history)
        {
            var n = TokenEstimator.Estimate(m);
            switch (m.Role)
            {
                case ChatRole.System:
                    system += n;
                    break;
                case ChatRole.Tool:
                    outputs += n;
                    break;
                default:
                    conversation += n;
                    break;
            }
        }
        return new PromptBreakdown(system, toolTokens, conversation, outputs);
    }

    /// <summary>「输入 77,034：系统提示 18% · 工具定义 40% · 对话 12% · 工具输出 30%」。actual 为 0 时用估算总数。</summary>
    public string Describe(int actual)
    {
        var total = Math.Max(1, Total);
        var shown = actual > 0 ? actual : Total;
        string Part(string name, int v) => $"{name} {Math.Round(100.0 * v / total)}%（≈{(long)shown * v / total:N0}）";
        return $"输入 {shown:N0}{(actual > 0 ? "" : "（估算）")}：" + string.Join(" · ", new[]
        {
            Part("系统提示", System),
            Part("工具定义", Tools),
            Part("对话", Conversation),
            Part("工具输出", ToolOutputs),
        });
    }
}
