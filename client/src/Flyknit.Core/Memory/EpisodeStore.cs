using System.Text;
using System.Text.Json;
using Flyknit.Core.Context;

namespace Flyknit.Core.Memory;

/// <summary>一次已完成任务的复盘记录（情景记忆）。</summary>
public sealed class Episode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string ConversationId { get; set; } = "";
    public string Workspace { get; set; } = "";

    /// <summary>任务类型的简短名称，例如“生成质检周报”。</summary>
    public string Title { get; set; } = "";

    /// <summary>用户的原始要求。</summary>
    public string Task { get; set; } = "";

    /// <summary>做了什么、结果如何。</summary>
    public string Summary { get; set; } = "";

    /// <summary>success / partial / failure</summary>
    public string Outcome { get; set; } = "success";

    /// <summary>下次可以直接复用的步骤。</summary>
    public string Procedure { get; set; } = "";

    public List<string> Lessons { get; set; } = new();
    public List<string> Tools { get; set; } = new();

    /// <summary>用户评价：1 赞、-1 踩、0 未评价。</summary>
    public int Feedback { get; set; }

    public int Uses { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string SearchText => $"{Title} {Task} {Summary}";
}

/// <summary>
/// 历史任务库（情景记忆），保存在记忆目录的 episodes.json。
/// 新任务开始时按相似度找出相关的历史任务，把当时的做法和教训交给模型，避免每次从头摸索。
/// </summary>
public sealed class EpisodeStore
{
    public const string FileName = "episodes.json";
    public const int MaxEpisodes = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _file;
    private readonly object _lock = new();
    private List<Episode>? _cache;

    /// <summary>测试用的“现在”。</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public EpisodeStore(string directory)
    {
        _file = Path.Combine(directory, FileName);
    }

    public IReadOnlyList<Episode> List()
    {
        lock (_lock)
        {
            return Load().OrderByDescending(e => e.CreatedAt).ToList();
        }
    }

    public void Add(Episode episode)
    {
        lock (_lock)
        {
            var list = Load();
            list.Add(episode);
            if (list.Count > MaxEpisodes)
            {
                // 优先淘汰早期、未被复用、没有好评的记录
                var drop = list.OrderBy(e => e.Feedback > 0 ? 1 : 0).ThenBy(e => e.Uses).ThenBy(e => e.CreatedAt).First();
                list.Remove(drop);
            }
            Save(list);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var list = Load();
            var removed = list.RemoveAll(e => e.Id == id) > 0;
            if (removed)
            {
                Save(list);
            }
            return removed;
        }
    }

    /// <summary>用户对某个对话的回答点赞或点踩时，同步到该对话最近的复盘记录。</summary>
    public void SetFeedback(string conversationId, int feedback)
    {
        lock (_lock)
        {
            var list = Load();
            var latest = list.Where(e => e.ConversationId == conversationId).OrderByDescending(e => e.CreatedAt).FirstOrDefault();
            if (latest is null)
            {
                return;
            }
            latest.Feedback = Math.Sign(feedback);
            if (feedback < 0 && latest.Outcome == "success")
            {
                latest.Outcome = "partial";
            }
            Save(list);
        }
    }

    /// <summary>找出与当前任务相似的历史任务。被点踩、失败的任务也会返回（用于吸取教训），但排在后面。</summary>
    public List<(Episode Episode, double Score)> Search(string query, int max = 3, double minScore = 0.22, string? workspace = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new();
        }
        // “把上周做的周报再做一遍”：上周做过的排前面；算内容相关度时去掉“上周”两个字
        var range = TimeRange.Parse(query, Clock());
        var q = range?.Strip(query) ?? query;
        lock (_lock)
        {
            return Load()
                .Select(e =>
                {
                    // 标题最能代表任务类型；长文本的相关度会被长度稀释，所以取两者较大者
                    var score = Math.Max(TextSimilarity.Relevance(q, e.Title), 0.8 * TextSimilarity.Relevance(q, e.SearchText));
                    if (range is not null && range.Contains(e.CreatedAt.LocalDateTime))
                    {
                        // 只说了时间（“昨天做了什么”）也能找到
                        score = range.IsTimeOnly(query) ? Math.Max(score, minScore) + 0.3 : score * 1.3 + 0.1;
                    }
                    if (e.Outcome == "success") score *= 1.1;
                    if (e.Feedback > 0) score *= 1.15;
                    if (e.Feedback < 0) score *= 0.9;
                    if (workspace is not null && string.Equals(e.Workspace, workspace, StringComparison.OrdinalIgnoreCase)) score *= 1.1;
                    return (Episode: e, Score: score);
                })
                .Where(x => x.Score >= minScore)
                .OrderByDescending(x => x.Score)
                .Take(max)
                .ToList();
        }
    }

    /// <summary>标记被引用过的历史任务（用于淘汰排序）。</summary>
    public void MarkUsed(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0)
        {
            return;
        }
        lock (_lock)
        {
            var list = Load();
            foreach (var e in list.Where(e => set.Contains(e.Id)))
            {
                e.Uses++;
            }
            Save(list);
        }
    }

    /// <summary>同类任务成功的次数（用于决定是否沉淀为技能）。</summary>
    public int CountSimilarSuccesses(string title, string task, double threshold = 0.45)
    {
        lock (_lock)
        {
            return Load().Count(e => e.Outcome == "success" && e.Feedback >= 0
                                     && TextSimilarity.Relevance($"{title} {task}", $"{e.Title} {e.Task}") >= threshold);
        }
    }

    /// <summary>放进系统提示词的“相关历史任务”。</summary>
    public static string BuildPromptSection(IReadOnlyList<(Episode Episode, double Score)> found)
    {
        if (found.Count == 0)
        {
            return "";
        }
        var sb = new StringBuilder();
        sb.AppendLine("<相关的历史任务>");
        sb.AppendLine("以下是用户以前做过的相似任务。优先沿用当时成功的做法和用户确认过的偏好，避开当时的错误；情况不同时以当前要求为准。");
        foreach (var (e, _) in found)
        {
            var outcome = e.Outcome switch { "success" => "成功", "partial" => "部分完成", _ => "失败" };
            if (e.Feedback > 0) outcome += "，用户点赞";
            if (e.Feedback < 0) outcome += "，用户不满意";
            sb.AppendLine($"### {e.Title}（{e.CreatedAt:yyyy-MM-dd}，{outcome}）");
            if (e.Task.Length > 0) sb.AppendLine($"要求：{Clip(e.Task, 300)}");
            if (e.Summary.Length > 0) sb.AppendLine($"经过：{Clip(e.Summary, 500)}");
            if (e.Procedure.Length > 0) sb.AppendLine($"可复用的步骤：\n{Clip(e.Procedure, 900)}");
            foreach (var l in e.Lessons.Take(4)) sb.AppendLine($"教训：{l}");
        }
        sb.AppendLine("</相关的历史任务>");
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private List<Episode> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }
        try
        {
            _cache = File.Exists(_file) ? JsonSerializer.Deserialize<List<Episode>>(File.ReadAllText(_file), Json) ?? new() : new();
        }
        catch (Exception)
        {
            _cache = new(); // 文件损坏时从空白开始，原文件保留为 .bak
            try { File.Copy(_file, _file + ".bak", overwrite: true); } catch (Exception) { }
        }
        return _cache;
    }

    private void Save(List<Episode> list)
    {
        _cache = list;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, _file, overwrite: true);
    }
}
