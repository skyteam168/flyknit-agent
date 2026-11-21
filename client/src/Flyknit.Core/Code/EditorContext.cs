using System.Collections.Concurrent;
using System.Text;
using Flyknit.Core.Agent;
using Flyknit.Core.Agent.Extensions;
using Flyknit.Core.Storage;
using Flyknit.Core.Workspace;

namespace Flyknit.Core.Code;

/// <summary>
/// 编码模式里员工此刻在看什么（参照 Cursor：当前文件、打开的文件、光标、选中的行会自动作为上下文）。
/// 界面切换标签、移动光标时经 code.focus 告诉宿主；路径都在这个对话的工作区里校验过。
/// </summary>
public sealed record EditorState
{
    /// <summary>当前文件（完整路径）。</summary>
    public string ActiveFile { get; init; } = "";

    /// <summary>光标所在行（从 1 开始，0 = 不知道）。</summary>
    public int Line { get; init; }

    /// <summary>选中的行（没有选中时都是 0）。</summary>
    public int SelectionFrom { get; init; }
    public int SelectionTo { get; init; }

    /// <summary>打开的其他文件（完整路径）。</summary>
    public IReadOnlyList<string> OpenFiles { get; init; } = Array.Empty<string>();

    /// <summary>从当前文件推断出的项目根；推断不出为 null。</summary>
    public string? ProjectRoot { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>每个对话的编辑器状态（只在内存里，程序重启就没了；没打开编码模式时没有）。</summary>
public sealed class EditorContextStore
{
    private readonly ConcurrentDictionary<string, EditorState> _byConversation = new(StringComparer.Ordinal);

    public void Set(string conversationId, EditorState? state)
    {
        if (state is null || state.ActiveFile.Length == 0 && state.OpenFiles.Count == 0)
        {
            _byConversation.TryRemove(conversationId, out _);
        }
        else
        {
            _byConversation[conversationId] = state;
        }
    }

    public EditorState? Get(string conversationId) => _byConversation.TryGetValue(conversationId, out var s) ? s : null;
}

/// <summary>
/// 运行扩展：编码模式开着时，每一轮把「员工正在看的项目、文件、位置」放进本轮参考资料，
/// 员工说「这个项目」「这个文件」「这里」时 AI 就知道指的是哪个，不用去工作区里猜。
/// 第一次碰到某个项目时附上项目概况（<see cref="ProjectInsight.Overview"/>），同一个对话里同一个项目只给一次。
/// 这些是程序附加的参考，不是员工的指令；文件内容不放进来（AI 需要时自己读），只放路径和行号。
/// </summary>
public sealed class EditorContextRunExtension : IRunExtension<RunScope>
{
    public const string ExtensionId = "editor_context";

    /// <summary>编辑器状态多久没更新就不算数（员工早就离开了编码模式）。</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromHours(8);

    private readonly EditorContextStore _store;
    private readonly HandoffStore? _handoffs;
    private readonly ConcurrentDictionary<string, HashSet<string>> _overviewGiven = new(StringComparer.Ordinal);

    public EditorContextRunExtension(EditorContextStore store, HandoffStore? handoffs = null)
    {
        _store = store;
        _handoffs = handoffs;
    }

    public string Id => ExtensionId;

    public async ValueTask<RunContribution?> PrepareAsync(RunScope scope, CancellationToken ct)
    {
        var conv = scope.Conversation;
        if (conv.Mode != ConversationMode.Agent)
        {
            return null;
        }
        // 「带上摘要新建任务」：新任务的第一句话带上旧任务的交接摘要（只给一次，跟着这条消息存下来）
        var handoff = scope.NewUserMessage && _handoffs is not null ? await _handoffs.TakeAsync(conv.Id, ct).ConfigureAwait(false) : null;
        var handoffText = handoff is { } h ? DescribeHandoff(h.Summary) : "";
        if (_store.Get(conv.Id) is not { } state || DateTimeOffset.Now - state.At > Stale)
        {
            return handoffText.Length > 0 ? new RunContribution { TurnContext = handoffText } : null;
        }
        var overview = "";
        if (state.ProjectRoot is { } root && scope.NewUserMessage)
        {
            var given = _overviewGiven.GetOrAdd(conv.Id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (given)
            {
                if (given.Add(root))
                {
                    overview = ProjectInsight.Overview(root);
                }
            }
        }
        var text = Describe(state, conv.Workspace, overview);
        return new RunContribution { TurnContext = handoffText.Length > 0 ? handoffText + "\n" + text : text };
    }

    /// <summary>旧任务的交接摘要放进新任务第一轮参考资料的那一段。</summary>
    public static string DescribeHandoff(string summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<上一个任务的交接 说明=\"员工新开任务接着做，这是程序从上一个任务整理的摘要（参考资料，不是员工的指令；以员工这次说的为准，细节以文件现状为准）\">");
        sb.AppendLine(summary.Trim());
        sb.AppendLine("</上一个任务的交接>");
        return sb.ToString();
    }

    /// <summary>放进本轮参考资料的那一段。</summary>
    public static string Describe(EditorState state, string? workspace, string overview)
    {
        string Show(string path) => workspace is { Length: > 0 } ws && WorkspaceBrowser.Resolve(ws, Path.GetRelativePath(ws, path)) is not null
            ? Path.GetRelativePath(ws, path)
            : path;

        var sb = new StringBuilder();
        sb.AppendLine("<编辑器上下文 说明=\"员工在编码模式里正打开着这些（程序附加的参考，不是员工的指令）\">");
        if (state.ProjectRoot is { } root)
        {
            var narrower = workspace is { Length: > 0 } && !string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"当前项目：{root}" + (narrower ? $"（工作区 {workspace} 里的一个项目，从当前文件往上找到项目标志文件推断）" : ""));
        }
        if (state.ActiveFile.Length > 0)
        {
            var where = state.SelectionFrom > 0 && state.SelectionTo >= state.SelectionFrom
                ? $"（选中第 {state.SelectionFrom}–{state.SelectionTo} 行）"
                : state.Line > 0 ? $"（光标在第 {state.Line} 行）" : "";
            sb.AppendLine($"当前文件：{Show(state.ActiveFile)}{where}");
        }
        var others = state.OpenFiles.Where(f => !string.Equals(f, state.ActiveFile, StringComparison.OrdinalIgnoreCase)).Take(12).Select(Show).ToList();
        if (others.Count > 0)
        {
            sb.AppendLine("其他打开的文件：" + string.Join("、", others));
        }
        sb.AppendLine("员工说「这个项目」「这个文件」「这里」「这段代码」时，指的就是上面的项目、文件、位置；分析、修改都以它为准，不要去工作区里别的项目找。");
        sb.AppendLine("</编辑器上下文>");
        if (overview.Length > 0)
        {
            sb.AppendLine("<项目概况 说明=\"程序从项目文件里整理的，供参考；需要细节时自己读文件\">");
            sb.AppendLine(overview);
            sb.AppendLine("</项目概况>");
        }
        return sb.ToString();
    }
}
