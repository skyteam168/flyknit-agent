using System.Text.Json;
using System.Text.Json.Nodes;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Memory;

/// <summary>矛盾检测结果。</summary>
public sealed record ContradictionCheck
{
    /// <summary>是否存在矛盾。</summary>
    public bool HasContradiction { get; init; }

    /// <summary>与新内容矛盾的已有记忆 ID。</summary>
    public List<string> ConflictingIds { get; init; } = new();

    /// <summary>矛盾的描述。</summary>
    public string Description { get; init; } = "";

    /// <summary>建议的解决方式：keep_new / keep_old / merge。</summary>
    public string Resolution { get; init; } = "keep_new";

    /// <summary>如果建议合并，合并后的文本。</summary>
    public string? MergedText { get; init; }
}

/// <summary>
/// 矛盾检测器：检测新记忆与已有记忆之间的语义矛盾。
/// 主要检测：
/// 1. 同槽位冲突（已在 MemoryStore 中处理）
/// 2. 语义矛盾（如"喜欢 Excel"和"讨厌 Excel"）
/// 3. 时序矛盾（如先说 A 后说 非A）
/// </summary>
public sealed class ContradictionDetector
{
    private readonly MemoryStore _store;
    private readonly IChatGateway? _gateway;

    /// <summary>快速检测的相似度阈值：只有和已有记忆相似到这个程度才需要深入检查矛盾。</summary>
    public const double SimilarityThreshold = 0.35;

    /// <summary>需要调用模型判断矛盾的相似度阈值。</summary>
    public const double DeepCheckThreshold = 0.5;

    /// <summary>场景。</summary>
    public string Scene { get; init; } = Scenes.Agent;

    public ContradictionDetector(MemoryStore store, IChatGateway? gateway = null)
    {
        _store = store;
        _gateway = gateway;
    }

    /// <summary>
    /// 快速检测：基于规则的矛盾检测，不调用模型。
    /// 检测槽位冲突、否定词模式、数值冲突等。
    /// </summary>
    public ContradictionCheck QuickCheck(MemoryKind kind, string newText, string? slot = null)
    {
        var existing = _store.List().Where(i => i.Kind == kind).ToList();
        if (existing.Count == 0)
        {
            return new ContradictionCheck();
        }

        // 1. 槽位冲突检测
        if (slot is not null)
        {
            var sameSlot = existing.FirstOrDefault(i => i.Slot == slot);
            if (sameSlot is not null && !MemoryStore.IsDuplicate(sameSlot.Text, newText))
            {
                return new ContradictionCheck
                {
                    HasContradiction = true,
                    ConflictingIds = new List<string> { sameSlot.Id },
                    Description = $"槽位「{MemorySlots.LabelOf(slot)}」已有值「{sameSlot.Text}」",
                    Resolution = "keep_new",
                };
            }
        }

        // 2. 否定模式检测
        var negationPatterns = new[]
        {
            ("不要", "要"), ("不用", "用"), ("不喜欢", "喜欢"), ("讨厌", "喜欢"),
            ("禁止", "允许"), ("不能", "能"), ("别", "要"), ("不需要", "需要"),
            ("不想", "想"), ("避免", "使用"), ("拒绝", "接受"),
        };

        foreach (var item in existing)
        {
            var similarity = TextSimilarity.Relevance(newText, item.Text);
            if (similarity < SimilarityThreshold)
            {
                continue;
            }

            // 检查否定模式
            foreach (var (neg, pos) in negationPatterns)
            {
                var newHasNeg = newText.Contains(neg);
                var oldHasNeg = item.Text.Contains(neg);
                var newHasPos = newText.Contains(pos) && !newText.Contains(neg);
                var oldHasPos = item.Text.Contains(pos) && !item.Text.Contains(neg);

                // 一个有否定词，一个有肯定词，且主题相似
                if ((newHasNeg && oldHasPos) || (newHasPos && oldHasNeg))
                {
                    // 进一步确认主题相似
                    var newCore = RemoveNegation(newText, negationPatterns);
                    var oldCore = RemoveNegation(item.Text, negationPatterns);
                    if (TextSimilarity.Relevance(newCore, oldCore) > 0.5)
                    {
                        return new ContradictionCheck
                        {
                            HasContradiction = true,
                            ConflictingIds = new List<string> { item.Id },
                            Description = $"与已有记忆「{item.Text}」存在矛盾（肯定/否定冲突）",
                            Resolution = "keep_new",
                        };
                    }
                }
            }

            // 3. 数值冲突检测（如"每天 100 个"和"每天 200 个"）
            var newNumbers = ExtractNumbers(newText);
            var oldNumbers = ExtractNumbers(item.Text);
            if (newNumbers.Count > 0 && oldNumbers.Count > 0 && similarity > 0.6)
            {
                // 主题高度相似但数值不同
                var differentNumbers = newNumbers.Except(oldNumbers).Any() || oldNumbers.Except(newNumbers).Any();
                if (differentNumbers)
                {
                    return new ContradictionCheck
                    {
                        HasContradiction = true,
                        ConflictingIds = new List<string> { item.Id },
                        Description = $"与已有记忆「{item.Text}」的数值不同",
                        Resolution = "keep_new",
                    };
                }
            }
        }

        return new ContradictionCheck();
    }

    /// <summary>
    /// 深度检测：调用模型判断语义矛盾。
    /// 只在快速检测发现高度相似但无法确定是否矛盾时使用。
    /// </summary>
    public async Task<ContradictionCheck> DeepCheckAsync(MemoryKind kind, string newText, CancellationToken ct)
    {
        if (_gateway is null)
        {
            return new ContradictionCheck();
        }

        var existing = _store.List().Where(i => i.Kind == kind).ToList();
        var candidates = existing
            .Select(i => (Item: i, Score: TextSimilarity.Relevance(newText, i.Text)))
            .Where(x => x.Score >= DeepCheckThreshold)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .ToList();

        if (candidates.Count == 0)
        {
            return new ContradictionCheck();
        }

        var existingList = string.Join("\n", candidates.Select((c, i) => $"[{i + 1}] {c.Item.Text}"));
        var prompt = """
            判断新记忆是否与已有记忆存在语义矛盾（说的是相反的事、互相冲突的偏好、不一致的信息）。

            已有记忆：
            """ + existingList + """


            新记忆：
            """ + newText + """


            如果存在矛盾，指出是哪一条，并建议如何解决。
            只输出 JSON：{"contradicts": null | [1], "description": "矛盾描述", "resolution": "keep_new" | "keep_old" | "merge", "merged_text": "合并后的文本（仅当 resolution 为 merge 时）"}
            没有矛盾就输出：{"contradicts": null}
            """;

        try
        {
            var turn = await _gateway.CompleteAsync(new ChatRequest
            {
                Scene = Scene,
                Stream = false,
                Temperature = 0.1,
                MaxTokens = 512,
                ExtraBody = new Dictionary<string, JsonNode?> { ["enable_thinking"] = false },
                Messages = new[] { ChatMessage.System(prompt) },
            }, null, ct);

            return ParseDeepCheckResult(turn.Content, candidates.Select(c => c.Item).ToList());
        }
        catch
        {
            return new ContradictionCheck();
        }
    }

    /// <summary>
    /// 定期扫描全部记忆，找出潜在矛盾。
    /// 返回矛盾对列表。
    /// </summary>
    public List<(MemoryItem A, MemoryItem B, string Reason)> ScanContradictions()
    {
        var result = new List<(MemoryItem, MemoryItem, string)>();
        var items = _store.List();

        // 按类别分组检查
        foreach (var group in items.GroupBy(i => i.Kind))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];

                    // 同槽位冲突
                    if (a.Slot is not null && a.Slot == b.Slot)
                    {
                        result.Add((a, b, $"同一槽位「{MemorySlots.LabelOf(a.Slot)}」有两个值"));
                        continue;
                    }

                    // 高度相似但不重复（可能是矛盾）
                    var similarity = TextSimilarity.Relevance(a.Text, b.Text);
                    if (similarity > 0.6 && !MemoryStore.IsDuplicate(a.Text, b.Text))
                    {
                        result.Add((a, b, $"内容相似但不相同（相似度 {similarity:P0}），可能存在冲突"));
                    }
                }
            }
        }

        return result;
    }

    private static ContradictionCheck ParseDeepCheckResult(string content, List<MemoryItem> candidates)
    {
        try
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return new ContradictionCheck();
            }

            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            var root = doc.RootElement;

            if (!root.TryGetProperty("contradicts", out var contradicts) || contradicts.ValueKind == JsonValueKind.Null)
            {
                return new ContradictionCheck();
            }

            var ids = new List<string>();
            if (contradicts.ValueKind == JsonValueKind.Array)
            {
                foreach (var idx in contradicts.EnumerateArray())
                {
                    if (idx.TryGetInt32(out var i) && i > 0 && i <= candidates.Count)
                    {
                        ids.Add(candidates[i - 1].Id);
                    }
                }
            }

            return new ContradictionCheck
            {
                HasContradiction = ids.Count > 0,
                ConflictingIds = ids,
                Description = root.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                    ? desc.GetString() ?? ""
                    : "",
                Resolution = root.TryGetProperty("resolution", out var res) && res.ValueKind == JsonValueKind.String
                    ? res.GetString() ?? "keep_new"
                    : "keep_new",
                MergedText = root.TryGetProperty("merged_text", out var merged) && merged.ValueKind == JsonValueKind.String
                    ? merged.GetString()
                    : null,
            };
        }
        catch
        {
            return new ContradictionCheck();
        }
    }

    private static string RemoveNegation(string text, (string neg, string pos)[] patterns)
    {
        foreach (var (neg, _) in patterns)
        {
            text = text.Replace(neg, "");
        }
        return text.Trim();
    }

    private static List<double> ExtractNumbers(string text)
    {
        var numbers = new List<double>();
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\d+(?:\.\d+)?");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (double.TryParse(m.Value, out var n))
            {
                numbers.Add(n);
            }
        }
        return numbers;
    }
}
