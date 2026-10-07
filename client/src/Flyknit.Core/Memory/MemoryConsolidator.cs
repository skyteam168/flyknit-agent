using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Memory;

/// <summary>一次“整理记忆”的结果。</summary>
public sealed class ConsolidationReport
{
    /// <summary>合并了几组。</summary>
    public int Groups { get; set; }

    /// <summary>合并前一共多少条参与了合并。</summary>
    public int ItemsMerged { get; set; }

    public List<string> Errors { get; } = new();
}

/// <summary>
/// 整理记忆：把换了个说法重复记下的条目合成一条。
/// 写入时的去重只看字面（“PowerShell 里 python -c 容易被引号坑”和“PowerShell 中 python -c 多行脚本引号转义易出错”
/// 字面差得多，挡不住），这里先按字面相似度挑出“可能重复”的几组，再让模型判断哪些真是一回事、合成一句。
/// 合并后旧条目留作历史，确认次数累加。由用户在记忆面板里手动触发。
/// </summary>
public sealed class MemoryConsolidator
{
    private readonly IChatGateway _gateway;
    private readonly MemoryStore _memory;

    public string Scene { get; init; } = Scenes.Agent;
    public int? ModelId { get; init; }

    /// <summary>两条记忆相似到这个程度才算“可能重复”，交给模型判断。</summary>
    public double CandidateThreshold { get; init; } = 0.3;

    /// <summary>一次请求最多发多少条，免得提示词太长。</summary>
    public int MaxItemsPerRequest { get; init; } = 80;

    public MemoryConsolidator(IChatGateway gateway, MemoryStore memory)
    {
        _gateway = gateway;
        _memory = memory;
    }

    public async Task<ConsolidationReport> RunAsync(CancellationToken ct)
    {
        var report = new ConsolidationReport();
        foreach (var kind in Enum.GetValues<MemoryKind>())
        {
            foreach (var batch in Batches(CandidateGroups(_memory.List().Where(i => i.Kind == kind).ToList(), CandidateThreshold), MaxItemsPerRequest))
            {
                if (batch.Length < 2)
                {
                    continue;
                }
                try
                {
                    await MergeBatchAsync(kind, batch, report, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    report.Errors.Add(ex.Message);
                }
            }
        }
        return report;
    }

    /// <summary>挑出至少和另一条有几分像的条目，按相似的放在一起（模型更容易看出重复）。</summary>
    public static List<MemoryItem> Candidates(List<MemoryItem> items, double threshold) =>
        CandidateGroups(items, threshold).SelectMany(g => g).ToList();

    /// <summary>按组装批，一组相似的不拆到两次请求里（太大的组只能拆）。</summary>
    private static IEnumerable<MemoryItem[]> Batches(List<List<MemoryItem>> groups, int max)
    {
        var batch = new List<MemoryItem>();
        foreach (var group in groups)
        {
            if (batch.Count > 0 && batch.Count + group.Count > max)
            {
                yield return batch.ToArray();
                batch.Clear();
            }
            batch.AddRange(group);
            while (batch.Count > max)
            {
                yield return batch.Take(max).ToArray();
                batch.RemoveRange(0, max);
            }
        }
        if (batch.Count > 0)
        {
            yield return batch.ToArray();
        }
    }

    public static List<List<MemoryItem>> CandidateGroups(List<MemoryItem> items, double threshold)
    {
        var picked = new List<List<MemoryItem>>();
        var used = new HashSet<string>();
        for (var i = 0; i < items.Count; i++)
        {
            if (used.Contains(items[i].Id))
            {
                continue;
            }
            var group = new List<MemoryItem> { items[i] };
            for (var j = i + 1; j < items.Count; j++)
            {
                if (!used.Contains(items[j].Id) && group.Any(g => Similar(g.Text, items[j].Text) >= threshold))
                {
                    group.Add(items[j]);
                }
            }
            if (group.Count > 1)
            {
                picked.Add(group);
                foreach (var g in group)
                {
                    used.Add(g.Id);
                }
            }
        }
        return picked;
    }

    private static double Similar(string a, string b) => Math.Max(TextSimilarity.Jaccard(a, b), TextSimilarity.Relevance(a, b) * 0.9);

    private async Task MergeBatchAsync(MemoryKind kind, MemoryItem[] batch, ConsolidationReport report, CancellationToken ct)
    {
        var aliases = batch.Select((item, i) => (Alias: $"m{i + 1}", Item: item)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"类别：{MemoryStore.HeaderOf(kind)}");
        foreach (var (alias, item) in aliases)
        {
            sb.AppendLine($"[{alias}] {item.Text}");
        }
        var turn = await _gateway.CompleteAsync(new ChatRequest
        {
            Scene = Scene,
            ModelId = ModelId,
            Stream = true,
            Temperature = 0.1,
            MaxTokens = 4096,
            ExtraBody = new Dictionary<string, JsonNode?> { ["enable_thinking"] = false },
            Messages = new[] { ChatMessage.System(Prompt), ChatMessage.User(sb.ToString()) },
        }, null, ct);

        var byAlias = aliases.ToDictionary(a => a.Alias, a => a.Item.Id);
        foreach (var (ids, text) in Parse(turn.Content))
        {
            var real = ids.Select(a => byAlias.GetValueOrDefault(a.Trim('[', ']', ' '))).OfType<string>().Distinct().ToList();
            if (real.Count < 2 || text.Length < 2)
            {
                continue;
            }
            if (_memory.Merge(real, text) is not null)
            {
                report.Groups++;
                report.ItemsMerged += real.Count;
                // 合并过的不能再出现在后面的组里
                foreach (var id in real)
                {
                    byAlias.Remove(byAlias.First(kv => kv.Value == id).Key);
                }
            }
        }
    }

    /// <summary>解析 {"merge":[{"ids":["m1","m3"],"text":"..."}]}，容忍代码块和前后多余文字。</summary>
    public static List<(List<string> Ids, string Text)> Parse(string content)
    {
        var result = new List<(List<string>, string)>();
        content = ContextManager.StripThink(content);
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return result;
        }
        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)], new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("merge", out var merge) || merge.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var g in merge.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object
                    || !g.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Array
                    || !g.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var list = ids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
                result.Add((list, text.GetString()!.Trim()));
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    public const string Prompt = """
        你在帮 AI 办公助手整理它对用户的长期记忆。下面是同一类别里“可能重复”的记忆条目，每条前面有编号。
        找出说的是同一件事的几条，合成一句完整、准确的话（保留所有有用的细节：路径、名称、数值、做法），去掉重复。
        只输出一个 JSON 对象，不要输出其他文字：
        {"merge": [{"ids": ["m1", "m4"], "text": "合并后的一句话"}]}
        要求：
        - 只合并确实是同一件事的；只是话题相近、但各有不同信息的，不要合并。
        - 两条互相矛盾时，以编号靠后的（较新的）为准。
        - 合并后的句子不超过 200 字，用原来的语言书写。
        - 不能加入原文里没有的信息。
        - 没有可合并的就输出 {"merge": []}。
        """;
}
