using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Chat;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Experts.Delegation;

/// <summary>
/// 主助手派遣专家（ask_expert）作为运行扩展：普通的办事对话里、「派遣专家」开着、这一轮挑得出候选专家时出场，
/// 加上候选名单（本轮参考资料，每轮按问题挑）和 ask_expert。专家对话里不派遣别的专家。
/// </summary>
public sealed class DelegationRunExtension : IRunExtension<RunScope>
{
    private readonly Func<bool> _enabled;
    private readonly Func<IEnumerable<Expert>> _experts;
    private readonly Func<ExpertDispatcher> _dispatcher;

    public DelegationRunExtension(Func<bool> enabled, Func<IEnumerable<Expert>> experts, Func<ExpertDispatcher> dispatcher)
    {
        _enabled = enabled;
        _experts = experts;
        _dispatcher = dispatcher;
    }

    public string Id => RunExtensionIds.Delegation;

    public ValueTask<RunContribution?> PrepareAsync(RunScope scope, CancellationToken ct)
    {
        if (scope.Conversation.Mode != ConversationMode.Agent || scope.Expert is not null || !_enabled())
        {
            return ValueTask.FromResult<RunContribution?>(null);
        }
        var candidates = ExpertCandidates.Pick(_experts(), Query(scope.History, scope.Query), scope.Semantic);
        if (candidates.Count == 0)
        {
            return ValueTask.FromResult<RunContribution?>(null);
        }
        return ValueTask.FromResult<RunContribution?>(new RunContribution
        {
            // 候选专家按问题挑，每轮不同：和记忆、技能一样放本轮参考资料
            TurnContext = ExpertCandidates.PromptSection(candidates),
            Tools = new ITool[] { new AskExpertTool(_dispatcher(), candidates) },
        });
    }

    /// <summary>挑候选专家用的文字：员工这句话，加上一句之前说的（「继续」「那 B 车间呢」这种要靠上一句）。</summary>
    public static string Query(IReadOnlyList<ChatMessage> history, string text)
    {
        var previous = history.Where(m => m.Role == ChatRole.User).Reverse().Skip(1).FirstOrDefault()?.Content ?? "";
        if (previous.Length > 200)
        {
            previous = previous[..200];
        }
        return previous.Length > 0 ? $"{text}\n{previous}" : text;
    }
}
