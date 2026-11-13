using System.Text.Json.Nodes;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Security;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Agent.SubAgents;

/// <summary>
/// 子代理（spawn_agent）作为运行扩展：IT 打开了「子代理」、这一轮是办事时出场。
/// 子代理用这一轮最终的工具表和模型（工具执行时再取），计划模式、预演下只能只读。
/// </summary>
public sealed class SubAgentRunExtension : IRunExtension<RunScope>
{
    private readonly SecuritySettings _security;
    private readonly Func<RunScope, SubAgentRunner> _runnerFor;
    private readonly Func<Conversation, IReadOnlyDictionary<string, JsonNode?>?> _extraBody;

    public SubAgentRunExtension(SecuritySettings security, Func<RunScope, SubAgentRunner> runnerFor,
        Func<Conversation, IReadOnlyDictionary<string, JsonNode?>?> extraBody)
    {
        _security = security;
        _runnerFor = runnerFor;
        _extraBody = extraBody;
    }

    public string Id => RunExtensionIds.SubAgents;

    public ValueTask<RunContribution?> PrepareAsync(RunScope scope, CancellationToken ct)
    {
        if (!SubAgentRunner.Enabled(_security, scope.Conversation.Mode == ConversationMode.Agent))
        {
            return ValueTask.FromResult<RunContribution?>(null);
        }
        var binding = scope.Binding;
        var spawn = new SpawnAgentTool(_runnerFor(scope),
            () => binding.Tools ?? throw new InvalidOperationException("这一轮的工具表还没组装好"),
            scope.Scene, () => binding.ModelId, _extraBody(scope.Conversation), forceReadOnly: scope.ReadOnlyRun);
        return ValueTask.FromResult<RunContribution?>(new RunContribution { Tools = new ITool[] { spawn } });
    }
}
