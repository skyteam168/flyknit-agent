using Flyknit.Core.Chat;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Agent;

/// <summary>
/// 模型服务的内容审核拦截。
///
/// 国内的模型服务（百炼 / DashScope 等）在请求前后各过一道内容审核，命中就直接返回错误，
/// 原文是英文的 "Input/Output data may contain inappropriate content."。
/// 这在工厂场景里很容易误伤：读进来的质检记录、客诉邮件、设备故障描述，
/// 都可能带上审核模型不喜欢的词，而文件内容本身完全正常。
///
/// 以前这种错误会一路抛到最外层，整轮任务报废、已经做完的步骤也不保存，
/// 用户只看到一句英文报错，重试还是同样的结果——因为触发词还在上下文里。
///
/// 现在的做法：把最近那段大的工具输出换成占位符重试一次。
/// 真正要紧的是任务继续走下去，而不是让模型逐字复述文件内容。
/// </summary>
public static class ContentFilter
{
    /// <summary>被审核拦截后，留在上下文里替换原文的占位符。</summary>
    public const string Placeholder =
        "（这段内容被模型服务的内容审核拦截了，已省略。文件本身没问题，只是其中某些词触发了服务商的审核规则。"
        + "请不要逐字复述这段内容；如果任务需要它，请改用概括的方式说明，或者告诉用户换一个处理方式。）";

    /// <summary>重试前给界面的提示。</summary>
    public const string RetryNotice = "上一段内容被模型服务的内容审核拦截，已省略后重试";

    /// <summary>超过这个长度的工具输出才值得替换；太短的替换了也省不下什么。</summary>
    public const int MinRedactChars = 200;

    private static readonly string[] Markers =
    {
        "inappropriate content",
        "data_inspection_failed",
        "datainspectionfailed",
        "content_filter",
        "contentfilter",
        "risk_control",
        "内容审核",
        "输入数据可能包含不适当的内容",
        "输出数据可能包含不适当的内容",
    };

    /// <summary>这个错误是内容审核拦截吗？</summary>
    public static bool IsBlocked(GatewayException ex)
    {
        var message = ex.Message ?? "";
        foreach (var marker in Markers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把最近一条还没被替换过的、足够大的工具输出换成占位符。
    /// 返回 false 表示没什么可换的了（再重试也是同样结果，应该停下来告诉用户）。
    /// </summary>
    public static bool Redact(List<ChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            if (m.Role != ChatRole.Tool || m.Content.Length < MinRedactChars || m.Content == Placeholder)
            {
                continue;
            }
            history[i] = new ChatMessage
            {
                Id = m.Id,
                Role = ChatRole.Tool,
                Content = Placeholder,
                ToolCallId = m.ToolCallId,
                ToolName = m.ToolName,
                CreatedAt = m.CreatedAt,
            };
            return true;
        }
        return false;
    }

    /// <summary>把模型服务的报错翻译成用户看得懂的话。</summary>
    public static string Explain(GatewayException ex)
    {
        var raw = ex.Message ?? "";
        if (IsBlocked(raw))
        {
            return "这一步被模型服务的**内容审核**拦截了。\n\n"
                 + "常见原因是刚读取的文件里有触发审核规则的词——文件本身通常没问题，是服务商的审核模型比较敏感。\n\n"
                 + "可以这样处理：\n"
                 + "- 告诉我只需要文件里的哪几项（比如「只要日期和数量」），我按需提取，不整段读进来\n"
                 + "- 或者在设置里换一个模型再试\n\n"
                 + $"（服务商原话：{raw}）";
        }
        return ex.StatusCode switch
        {
            401 or 403 => $"模型服务拒绝了这次请求（{raw}）。可能是本机的授权过期了，请联系 IT 管理员。",
            429 => $"模型服务繁忙或已达用量上限（{raw}）。稍等一下再试，或联系 IT 管理员增加额度。",
            >= 500 => $"模型服务出错了（{raw}）。这通常是服务端的临时故障，过一会儿再试。",
            _ => $"调用模型失败：{raw}",
        };
    }

    private static bool IsBlocked(string message)
    {
        foreach (var marker in Markers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
