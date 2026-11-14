using System.Text.Json;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Teams;

/// <summary>分工计划里的一项：哪位成员、做什么、交付标准、等谁。</summary>
public sealed record TeamPlanItem(string ExpertId, string Task, string Done, IReadOnlyList<string> DependsOn);

/// <summary>
/// 分工计划（docs/team-mode.md 第 3.1、6 节）：队长用 team_plan 提交，程序校验后给员工看，员工点「开始执行」才跑。
/// 校验只看结构：成员必须是团队里能用的专家、不重复、依赖必须在计划里、不能成环。
/// </summary>
public sealed record TeamPlan(string Goal, IReadOnlyList<TeamPlanItem> Items)
{
    public const int MaxTaskChars = 4000;
    public const int MaxDoneChars = 500;

    /// <summary>从工具参数读出计划（缺字段的项丢掉，交给 <see cref="Validate"/> 报错）。</summary>
    public static TeamPlan Parse(JsonElement args)
    {
        var items = new List<TeamPlanItem>();
        if (args.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var deps = new List<string>();
                if (it.TryGetProperty("depends_on", out var d) && d.ValueKind == JsonValueKind.Array)
                {
                    deps.AddRange(d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0));
                }
                items.Add(new TeamPlanItem(
                    it.Str("member").Trim(),
                    Clip(it.Str("task").Trim(), MaxTaskChars),
                    Clip(it.Str("done").Trim(), MaxDoneChars),
                    deps.Distinct(StringComparer.Ordinal).ToList()));
            }
        }
        return new TeamPlan(Clip(args.Str("goal").Trim(), 500), items);
    }

    /// <summary>检查计划能不能执行。返回错误（给队长看的中文），没有错误返回空列表。</summary>
    public IReadOnlyList<string> Validate(IReadOnlyCollection<string> usableMembers, int maxItems)
    {
        var errors = new List<string>();
        if (Items.Count == 0)
        {
            errors.Add("计划里至少要有一位成员");
            return errors;
        }
        if (Items.Count > maxItems)
        {
            errors.Add($"这一轮最多派 {maxItems} 位成员，计划里有 {Items.Count} 项");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (!usableMembers.Contains(item.ExpertId))
            {
                errors.Add($"「{item.ExpertId}」不是这个团队里能用的成员");
            }
            if (!seen.Add(item.ExpertId))
            {
                errors.Add($"「{item.ExpertId}」出现了两次：一位成员一轮只领一项任务，把要做的写在一起");
            }
            if (item.Task.Length < 10)
            {
                errors.Add($"给「{item.ExpertId}」的任务太短：成员看不到这段对话，要写清目标、范围和要交回什么");
            }
            if (item.Done.Length == 0)
            {
                errors.Add($"给「{item.ExpertId}」的任务没写交付标准（done）：要交回什么才算完成");
            }
        }
        var ids = Items.Select(i => i.ExpertId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            foreach (var dep in item.DependsOn)
            {
                if (dep == item.ExpertId)
                {
                    errors.Add($"「{item.ExpertId}」不能依赖自己");
                }
                else if (!ids.Contains(dep))
                {
                    errors.Add($"「{item.ExpertId}」依赖的「{dep}」不在这次计划里");
                }
            }
        }
        if (errors.Count == 0 && HasCycle())
        {
            errors.Add("依赖关系成了环（互相等对方），没法开始。请去掉其中一条依赖");
        }
        return errors;
    }

    /// <summary>员工在计划卡上去掉了几位：去掉这些项，别人对它们的依赖一起去掉。</summary>
    public TeamPlan Without(IReadOnlyCollection<string> removed)
    {
        if (removed.Count == 0)
        {
            return this;
        }
        var keep = Items.Where(i => !removed.Contains(i.ExpertId))
            .Select(i => i with { DependsOn = i.DependsOn.Where(d => !removed.Contains(d)).ToList() })
            .ToList();
        return this with { Items = keep };
    }

    private bool HasCycle()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 未访问 1 访问中 2 完成
        var deps = Items.ToDictionary(i => i.ExpertId, i => i.DependsOn, StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (state.TryGetValue(id, out var s))
            {
                return s == 1;
            }
            state[id] = 1;
            foreach (var d in deps.GetValueOrDefault(id) ?? Array.Empty<string>())
            {
                if (deps.ContainsKey(d) && Visit(d))
                {
                    return true;
                }
            }
            state[id] = 2;
            return false;
        }
        return Items.Any(i => Visit(i.ExpertId));
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
