using Flyknit.Core.Chat;
using Flyknit.Core.Experts;
using Flyknit.Core.Storage;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Agent.Extensions;

/// <summary>
/// 一轮办事运行开始前，宿主交给运行扩展看的信息（只读）。宿主可以派生，加自己的东西（比如界面事件通道）。
/// 见 docs/extension-architecture.md。
/// </summary>
public class RunScope
{
    public required Conversation Conversation { get; init; }

    /// <summary>专家对话里的专家（快照）；普通对话为 null。</summary>
    public Expert? Expert { get; init; }

    /// <summary>员工这一轮说的话。</summary>
    public required string Query { get; init; }

    /// <summary>这个对话存下来的消息（含这一轮员工的话）。</summary>
    public IReadOnlyList<ChatMessage> History { get; init; } = Array.Empty<ChatMessage>();

    /// <summary>这句话和记忆、技能、专家的语义分数；服务端没配向量模型时为 null。</summary>
    public IReadOnlyDictionary<string, double>? Semantic { get; init; }

    public required string RunId { get; init; }

    /// <summary>这一轮的场景（agent / code）。</summary>
    public required string Scene { get; init; }

    /// <summary>员工刚发了新消息（不是续跑、重试）。</summary>
    public bool NewUserMessage { get; init; }

    /// <summary>这一轮不许改东西（计划模式、预演）。</summary>
    public bool ReadOnlyRun { get; init; }

    public required IConfirmationHandler Confirm { get; init; }

    /// <summary>往界面推事件（团队面板、计划卡之类）。宿主没有界面时什么都不做。</summary>
    public Action<object> Post { get; init; } = _ => { };

    /// <summary>这一轮最后才定下来的东西（工具表、模型）。扩展准备时还没定，工具执行时再取。</summary>
    public RunBinding Binding { get; } = new();
}

/// <summary>内置运行扩展的编号（互斥时按编号指）。</summary>
public static class RunExtensionIds
{
    public const string Team = "team";
    public const string Delegation = "delegation";
    public const string SubAgents = "subagents";
}

/// <summary>这一轮组装完才知道的东西：宿主组装完填上，扩展加的工具执行时来取。</summary>
public sealed class RunBinding
{
    /// <summary>这一轮最终的工具表（含所有扩展加的工具、专家筛选、计划模式过滤之后）。</summary>
    public ToolRegistry? Tools { get; set; }

    /// <summary>这一轮最终用的模型（对话指定 → 专家指定 → 扩展给的）。</summary>
    public int? ModelId { get; set; }
}

/// <summary>
/// 一个运行扩展这一轮加了什么。只能「加」：提示词、本轮参考资料、工具；模型只在对话和专家都没指定时才用，
/// token 上限取所有扩展里最小的。安全闸门、确认、审计不归扩展管。
/// </summary>
public sealed class RunContribution
{
    /// <summary>追加到系统提示词末尾（整个对话不变的东西放这里，提示词缓存才命中）。</summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>追加到本轮参考资料（每轮会变的东西放这里，跟着这条用户消息存下来）。</summary>
    public string TurnContext { get; init; } = "";

    /// <summary>这一轮加的工具。专家对话里宿主还会按专家的限制再筛一遍。</summary>
    public IReadOnlyList<ITool> Tools { get; init; } = Array.Empty<ITool>();

    /// <summary>对话和专家都没指定模型时用这个。</summary>
    public int? ModelId { get; init; }

    /// <summary>这一轮的 token 上限（null = 宿主默认）。</summary>
    public long? MaxRunTokens { get; init; }

    /// <summary>这一轮结束（不管成功、失败、停止）时调用，用来收拾这一轮的状态。</summary>
    public Action? OnEnd { get; init; }
}

/// <summary>
/// 运行扩展：一个可以插拔的能力（团队、派遣专家、子代理……），在每轮办事开始前决定这一轮要不要出场、加什么。
/// 底座（<see cref="AgentLoop"/>、安全闸门）不认识任何具体扩展。
/// </summary>
public interface IRunExtension<in TScope> where TScope : RunScope
{
    /// <summary>扩展的编号（唯一，用于互斥和日志）。</summary>
    string Id { get; }

    /// <summary>这个扩展出场后，哪些扩展这一轮不再出场（比如团队任务里不给主助手 ask_expert）。</summary>
    IReadOnlyCollection<string> Excludes => Array.Empty<string>();

    /// <summary>这一轮要不要出场：不出场返回 null。不该抛异常；抛了只算这个扩展这一轮缺席。</summary>
    ValueTask<RunContribution?> PrepareAsync(TScope scope, CancellationToken ct);
}

/// <summary>
/// 按注册顺序问每个扩展这一轮要不要出场，把加的东西合成一份。互斥按 <see cref="IRunExtension{TScope}.Excludes"/>：
/// 先出场的扩展排除掉的，后面的不再问；后出场的扩展排除掉先出场的，先出场的那份作废（并收尾）。
/// 某个扩展准备时出错只算它缺席，不影响这一轮和别的扩展（停止任务的取消照常往外抛）。
/// </summary>
public sealed class RunExtensionPipeline<TScope> where TScope : RunScope
{
    private readonly IReadOnlyList<IRunExtension<TScope>> _extensions;
    private readonly Action<string, Exception>? _onError;

    public RunExtensionPipeline(IEnumerable<IRunExtension<TScope>> extensions, Action<string, Exception>? onError = null)
    {
        _extensions = extensions.ToList();
        _onError = onError;
        var duplicate = _extensions.GroupBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"运行扩展编号重复：{duplicate.Key}");
        }
    }

    public IReadOnlyList<string> Ids => _extensions.Select(e => e.Id).ToList();

    public async Task<RunAssembly> PrepareAsync(TScope scope, CancellationToken ct)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var active = new List<(string Id, RunContribution Part)>();
        try
        {
            foreach (var extension in _extensions)
            {
                if (excluded.Contains(extension.Id))
                {
                    continue;
                }
                RunContribution? part;
                try
                {
                    part = await extension.PrepareAsync(scope, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(extension.Id, ex);
                    continue;
                }
                if (part is null)
                {
                    continue;
                }
                active.Add((extension.Id, part));
                excluded.UnionWith(extension.Excludes);
            }
        }
        catch
        {
            new RunAssembly(active, _onError).Dispose();
            throw;
        }
        // 后出场的排除掉先出场的：先出场的那份作废
        var dropped = active.Where(a => excluded.Contains(a.Id)).ToList();
        new RunAssembly(dropped, _onError).Dispose();
        return new RunAssembly(active.Except(dropped).ToList(), _onError);
    }
}

/// <summary>这一轮所有出场扩展加的东西合在一起。用完（这一轮结束）要 Dispose，触发各扩展的收尾。</summary>
public sealed class RunAssembly : IDisposable
{
    private readonly IReadOnlyList<(string Id, RunContribution Part)> _parts;
    private readonly Action<string, Exception>? _onError;
    private int _disposed;

    internal RunAssembly(IReadOnlyList<(string Id, RunContribution Part)> parts, Action<string, Exception>? onError)
    {
        _parts = parts;
        _onError = onError;
    }

    public static RunAssembly Empty { get; } = new(Array.Empty<(string, RunContribution)>(), null);

    public IReadOnlyList<string> ActiveIds => _parts.Select(p => p.Id).ToList();

    public bool IsActive(string id) => _parts.Any(p => p.Id == id);

    public string SystemPrompt => Join(_parts.Select(p => p.Part.SystemPrompt));

    public string TurnContext => Join(_parts.Select(p => p.Part.TurnContext));

    public IReadOnlyList<ITool> Tools => _parts.SelectMany(p => p.Part.Tools).ToList();

    /// <summary>第一个给了模型的扩展的模型。</summary>
    public int? ModelId => _parts.Select(p => p.Part.ModelId).FirstOrDefault(m => m is not null);

    /// <summary>所有扩展给的 token 上限里最小的；都没给为 null。</summary>
    public long? MaxRunTokens => _parts.Select(p => p.Part.MaxRunTokens).Where(t => t is not null).Min();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        foreach (var (id, part) in _parts)
        {
            try
            {
                part.OnEnd?.Invoke();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(id, ex);
            }
        }
    }

    private static string Join(IEnumerable<string> texts) => string.Join("\n\n", texts.Where(t => !string.IsNullOrEmpty(t)));
}
