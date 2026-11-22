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
    private readonly Func<IReadOnlyList<SubAgentModel>> _models;
    private readonly Action<string>? _log;

    /// <param name="models">子代理能选的模型（后台启用、能调用工具的）；不给就只能用主助手的模型。</param>
    /// <param name="log">项目里的子代理定义有问题（跳过了哪个、为什么）写日志。</param>
    public SubAgentRunExtension(SecuritySettings security, Func<RunScope, SubAgentRunner> runnerFor,
        Func<Conversation, IReadOnlyDictionary<string, JsonNode?>?> extraBody,
        Func<IReadOnlyList<SubAgentModel>>? models = null, Action<string>? log = null)
    {
        _security = security;
        _runnerFor = runnerFor;
        _extraBody = extraBody;
        _models = models ?? (() => Array.Empty<SubAgentModel>());
        _log = log;
    }

    public string Id => RunExtensionIds.SubAgents;

    public ValueTask<RunContribution?> PrepareAsync(RunScope scope, CancellationToken ct)
    {
        if (!SubAgentRunner.Enabled(_security, scope.Conversation.Mode == ConversationMode.Agent))
        {
            return ValueTask.FromResult<RunContribution?>(null);
        }
        var binding = scope.Binding;
        // 项目里定义的子代理（工作区的 .flyknit/agents/*.md）：每轮读一次，改了马上生效
        var (definitions, rejected) = SubAgentDefinitions.Load(scope.Conversation.Workspace);
        foreach (var why in rejected)
        {
            _log?.Invoke($"子代理定义没有采用：{why}");
        }
        var spawn = new SpawnAgentTool(_runnerFor(scope),
            () => binding.Tools ?? throw new InvalidOperationException("这一轮的工具表还没组装好"),
            scope.Scene, () => binding.ModelId, _extraBody(scope.Conversation), forceReadOnly: scope.ReadOnlyRun,
            definitions: definitions, models: _models);
        return ValueTask.FromResult<RunContribution?>(new RunContribution { Tools = new ITool[] { spawn } });
    }
}
