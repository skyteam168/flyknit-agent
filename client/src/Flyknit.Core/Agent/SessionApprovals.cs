using Flyknit.Core.Security;

namespace Flyknit.Core.Agent;

/// <summary>
/// 「本任务内不再询问」记下的授权（docs/approval-p0.md 第 3、4 节）。
///
/// 一次办事运行一份：主助手和它派出去的专家、子代理共用——员工在任何一张卡片上点了，
/// 这次任务里同类操作都不再问；任务结束就丢掉，不跨任务（拆开的几件是不同的任务，各用各的）。
/// 共用的只是员工本人已经给过的授权，专家能用哪些工具、权限多大照旧按专家配置收紧。
///
/// 可以挂一层子层（派遣时的一次授权，只在那位专家这次派遣里有效）：子层能看到父层，
/// 往子层加的不往上传；<see cref="Add"/> 总是加到最上面那层（整个任务）。
/// </summary>
public sealed class SessionApprovals
{
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public SessionApprovals(SessionApprovals? parent = null)
    {
        Parent = parent;
    }

    public SessionApprovals? Parent { get; }

    private SessionApprovals Root => Parent?.Root ?? this;

    /// <summary>这一层或上面任何一层记过。</summary>
    public bool Contains(string key)
    {
        lock (_gate)
        {
            if (_keys.Contains(key))
            {
                return true;
            }
        }
        return Parent?.Contains(key) ?? false;
    }

    /// <summary>员工点了「本任务内不再询问」：记到整个任务上，主助手和所有专家都能沿用。</summary>
    public void Add(string key) => Root.AddHere(key);

    /// <summary>只记在这一层（子层的授权不往上传）。</summary>
    public void AddHere(string key)
    {
        lock (_gate)
        {
            _keys.Add(key);
        }
    }

    /// <summary>
    /// 授权的键。判定里带了规则候选，就按规则记，和「以后自动」同一个粒度：
    /// MCP 按工具名（允许过「MES · 查询工单」，这次任务里查别的工单也不再问，别的工具照问）；
    /// 命令按分析出来的程序前缀，范围带工作区。
    ///
    /// 没有规则候选的（解释器、看不出网址的联网命令……）退回原来的算法：按摘要里的程序名记。
    /// 以前一律用摘要，MCP 的摘要是「参数名：参数值」，每次查询的键都不一样，点了等于没点。
    /// </summary>
    public static string KeyFor(string toolName, string summary, ApprovalCandidate? rule)
    {
        if (rule is { Prefix.Length: > 0 })
        {
            return $"{rule.Tool}:{rule.Prefix}@{rule.Scope}".ToLowerInvariant();
        }
        return $"{toolName}:{SummaryPrefix(summary)}".ToLowerInvariant();
    }

    private static string SummaryPrefix(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "*";
        }
        var parts = summary.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "*";
        }
        var exe = Path.GetFileNameWithoutExtension(parts[0].Trim('"', '\''));
        if (parts.Length > 1 && exe is "npm" or "pip" or "git" or "dotnet" or "docker" or "yarn" or "pnpm")
        {
            return $"{exe} {parts[1]}";
        }
        return exe;
    }
}
