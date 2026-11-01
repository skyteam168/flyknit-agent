namespace Flyknit.Core.Agent;

/// <summary>运行中用户又发的一句话。</summary>
/// <param name="Mode"><see cref="RunInbox.Steer"/>：插话，下一步就交给 AI；<see cref="RunInbox.Queue"/>：排队，这一轮结束后作为新的一条发出。</param>
public sealed record PendingInput(string Id, string Text, string Mode, DateTimeOffset At);

/// <summary>
/// 每个对话「还没交给 AI 的话」：插话和排队。AI 还没读到之前都能改、能撤回。
///
/// - 插话（steer）：AgentLoop 每一步开始前、准备收尾前取走，接进上下文；
/// - 排队（queue）：这一轮结束后按顺序一条一条发出；
/// - 打断优先：调用方先停掉这一轮，再把这句排到队首。
/// 这一轮没来得及读的插话（被停止、出错、步数用完）在结束时合成一条，排在队首接着发，不会丢。
/// </summary>
public sealed class RunInbox
{
    public const string Steer = "steer";
    public const string Queue = "queue";

    private readonly object _lock = new();
    private readonly Dictionary<string, List<PendingInput>> _items = new();

    /// <summary>某个对话的待发列表变了（界面据此刷新）。</summary>
    public event Action<string>? Changed;

    public PendingInput Add(string conversationId, string text, string mode, string? id = null, bool front = false)
    {
        var item = new PendingInput(id ?? Guid.NewGuid().ToString("N"), text.Trim(), mode == Queue ? Queue : Steer, DateTimeOffset.Now);
        lock (_lock)
        {
            var list = ListOf(conversationId);
            if (front)
            {
                list.Insert(0, item);
            }
            else
            {
                list.Add(item);
            }
        }
        Changed?.Invoke(conversationId);
        return item;
    }

    public IReadOnlyList<PendingInput> List(string conversationId)
    {
        lock (_lock)
        {
            return _items.TryGetValue(conversationId, out var list) ? list.ToList() : new List<PendingInput>();
        }
    }

    /// <summary>改一句还没交给 AI 的话。已经被取走的改不了，返回 false。</summary>
    public bool Update(string conversationId, string id, string text, string? mode = null)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(conversationId, out var list))
            {
                return false;
            }
            var i = list.FindIndex(p => p.Id == id);
            if (i < 0)
            {
                return false;
            }
            list[i] = list[i] with { Text = text.Trim(), Mode = mode is null ? list[i].Mode : mode == Queue ? Queue : Steer };
        }
        Changed?.Invoke(conversationId);
        return true;
    }

    /// <summary>撤回。已经被取走的撤不回，返回 false。</summary>
    public bool Remove(string conversationId, string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _items.TryGetValue(conversationId, out var list) && list.RemoveAll(p => p.Id == id) > 0;
        }
        if (removed)
        {
            Changed?.Invoke(conversationId);
        }
        return removed;
    }

    /// <summary>AgentLoop 每一步取走插话（排队的不动）。</summary>
    public IReadOnlyList<PendingInput> TakeSteer(string conversationId) => Take(conversationId, p => p.Mode == Steer);

    /// <summary>
    /// 这一轮结束了：下一条该发什么。没读到的插话合成一条优先发，否则发队列里的第一条；都没有返回 null。
    /// </summary>
    public PendingInput? TakeNext(string conversationId)
    {
        var steer = Take(conversationId, p => p.Mode == Steer);
        if (steer.Count > 0)
        {
            return steer.Count == 1 ? steer[0] : steer[0] with { Text = string.Join("\n\n", steer.Select(p => p.Text)) };
        }
        PendingInput? next = null;
        lock (_lock)
        {
            if (_items.TryGetValue(conversationId, out var list) && list.Count > 0)
            {
                next = list[0];
                list.RemoveAt(0);
            }
        }
        if (next is not null)
        {
            Changed?.Invoke(conversationId);
        }
        return next;
    }

    /// <summary>全部取走（对话被删、远程任务结束时）。</summary>
    public IReadOnlyList<PendingInput> TakeAll(string conversationId) => Take(conversationId, _ => true);

    private IReadOnlyList<PendingInput> Take(string conversationId, Func<PendingInput, bool> match)
    {
        List<PendingInput> taken;
        lock (_lock)
        {
            if (!_items.TryGetValue(conversationId, out var list))
            {
                return Array.Empty<PendingInput>();
            }
            taken = list.Where(match).ToList();
            if (taken.Count == 0)
            {
                return taken;
            }
            list.RemoveAll(p => match(p));
        }
        Changed?.Invoke(conversationId);
        return taken;
    }

    private List<PendingInput> ListOf(string conversationId)
    {
        if (!_items.TryGetValue(conversationId, out var list))
        {
            _items[conversationId] = list = new List<PendingInput>();
        }
        return list;
    }
}
