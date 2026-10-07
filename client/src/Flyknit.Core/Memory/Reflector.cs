using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flyknit.Core.Chat;
using Flyknit.Core.Context;
using Flyknit.Core.Gateway;

namespace Flyknit.Core.Memory;

/// <summary>复盘的输入：一轮任务的经过。</summary>
public sealed class ReflectionInput
{
    public required string ConversationId { get; init; }
    public string Workspace { get; init; } = "";

    /// <summary>这一轮的用户要求。</summary>
    public required string UserRequest { get; init; }

    /// <summary>上一轮助手的回答（用户这一轮可能是在纠正它）。</summary>
    public string PreviousAnswer { get; init; } = "";

    /// <summary>这一轮新产生的消息（助手、工具）。</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Completed / Cancelled / MaxSteps / TooManyFailures / Stuck</summary>
    public string StopReason { get; init; } = "Completed";

    /// <summary>用户评价：1 赞、-1 踩、0 无。点踩触发的复盘会着重总结教训。</summary>
    public int Feedback { get; init; }

    public string UiLanguage { get; init; } = "zh-CN";
}

public sealed class ReflectionResult
{
    public bool WorthSaving { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Outcome { get; set; } = "success";
    public string Procedure { get; set; } = "";
    public List<MemoryProposal> Preferences { get; set; } = new();
    public List<MemoryProposal> Facts { get; set; } = new();
    public List<MemoryProposal> Successes { get; set; } = new();
    public List<MemoryProposal> Lessons { get; set; } = new();
    public LearnedSkill? Skill { get; set; }

    /// <summary>发给模型的已有记忆编号（m1、m2…）对应的真实 ID。</summary>
    public Dictionary<string, string> Aliases { get; set; } = new();
}

/// <summary>
/// 复盘提出的一条记忆。和已有记忆的关系由模型判断：
/// Same 指向已有的一条，表示说的是同一件事（只加确认次数）；Replaces 表示取代那一条（偏好变了、信息更新了）。
/// </summary>
public sealed record MemoryProposal(string Text, string? Same = null, string? Replaces = null)
{
    /// <summary>来源：user_said / user_confirmed / inferred / from_content。只写了一句话（旧格式）时当作推测。</summary>
    public string Origin { get; init; } = MemoryOrigin.Inferred;

    /// <summary>依据：用户原话。偏好、置顶必须有，而且要真的出现在用户这一轮说的话里。</summary>
    public string Evidence { get; init; } = "";

    /// <summary>模型自己估的把握（0..1）；没给时按 0.7 算。</summary>
    public double? Confidence { get; init; }

    /// <summary>是不是长期有效。false（一次性的数值、临时路径、这次的结论）不记。</summary>
    public bool Durable { get; init; } = true;

    /// <summary>用户要求以后一直这样做（“以后都…”“一直…”“记住…”）。</summary>
    public bool Pinned { get; init; }

    public static implicit operator MemoryProposal(string text) => new(text);
    public override string ToString() => Text;
}

/// <summary>
/// 记忆写入门槛：复盘提出的每一条，先过这里再写。规则写死在代码里，不靠模型自觉：
/// - 来自文件、网页、工具返回内容的，不记（防止被文档里的话“教坏”）；
/// - 一次性的、把握不够（&lt; 0.7）的，不记；
/// - 偏好必须是用户亲口说或确认的，而且给出的原话要真的出现在用户这一轮的话里；
/// - 说是用户原话、但原话对不上的，降级为推测；推测的内容把握要 ≥ 0.8；
/// - 这次任务没有长期价值（worth_saving=false）时，只记用户亲口提出的偏好；
/// - 只有用户亲口要求（原话对得上）的才能置顶。
/// </summary>
public static class MemoryGate
{
    public const double MinConfidence = 0.7;
    public const double MinInferredConfidence = 0.8;

    public sealed record Verdict(bool Accept, string Origin, bool Pinned, string Reason);

    public static Verdict Check(MemoryKind kind, MemoryProposal p, string userText, bool worthSaving)
    {
        var origin = MemoryOrigin.Normalize(p.Origin);
        if (origin.Length == 0)
        {
            origin = MemoryOrigin.Inferred;
        }
        if (origin == MemoryOrigin.FromContent)
        {
            return new(false, origin, false, "来自文件、网页或工具返回的内容");
        }
        if (!p.Durable)
        {
            return new(false, origin, false, "一次性信息");
        }
        var confidence = p.Confidence ?? MinConfidence;
        if (confidence < MinConfidence)
        {
            return new(false, origin, false, "把握不够");
        }
        var verified = EvidenceIn(p.Evidence, userText);
        if (origin is MemoryOrigin.UserSaid or MemoryOrigin.UserConfirmed && !verified)
        {
            origin = MemoryOrigin.Inferred; // 说是用户原话，但用户这一轮没说过这句
        }
        if (kind == MemoryKind.Preference && origin == MemoryOrigin.Inferred)
        {
            return new(false, origin, false, "偏好必须有用户原话");
        }
        if (origin == MemoryOrigin.Inferred && confidence < MinInferredConfidence)
        {
            return new(false, origin, false, "推测的内容把握不够");
        }
        if (!worthSaving && !(kind == MemoryKind.Preference && origin == MemoryOrigin.UserSaid))
        {
            return new(false, origin, false, "这次任务没有长期价值");
        }
        return new(true, origin, p.Pinned && origin == MemoryOrigin.UserSaid, "");
    }

    /// <summary>原话是否真的出现在用户说的话里（忽略标点和空格；允许个别字不同）。</summary>
    public static bool EvidenceIn(string evidence, string userText)
    {
        var e = TextSimilarity.Normalize(evidence);
        if (e.Length < 2 || string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }
        return TextSimilarity.Normalize(userText).Contains(e) || (e.Length >= 6 && TextSimilarity.Coverage(evidence, userText) >= 0.85);
    }
}

public sealed class LearnedSkill
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Body { get; set; } = "";
}

/// <summary>复盘后实际写入的内容，界面据此提示“学到了什么”。</summary>
public sealed class LearningReport
{
    public Episode? Episode { get; init; }
    public List<(MemoryKind Kind, string Text)> Added { get; } = new();

    /// <summary>没过写入门槛的（原因 → 条数），用于日志。</summary>
    public Dictionary<string, int> Filtered { get; } = new();

    /// <summary>其中取代了旧说法的条数。</summary>
    public int Updated { get; set; }

    /// <summary>和已有记忆说的是一回事、只加了确认次数的条数。</summary>
    public int Reinforced { get; set; }

    /// <summary>因为含敏感信息等原因没记的条数。</summary>
    public int Rejected { get; set; }

    public string? SkillName { get; set; }

    public bool Any => Episode is not null || Added.Count > 0 || SkillName is not null;
}

/// <summary>
/// 任务复盘（自我改进），参照 Reflexion：任务结束后用模型回顾经过，
/// 提炼可复用的做法、用户偏好、成功经验与失败教训，写入长期记忆和历史任务库；
/// 同类任务多次成功后沉淀为技能（SKILL.md），以后直接按技能执行。
/// </summary>
public sealed class Reflector
{
    private readonly IChatGateway _gateway;
    private readonly MemoryStore _memory;
    private readonly EpisodeStore _episodes;
    private readonly string? _learnedSkillsDir;

    public string? LastError { get; private set; }

    /// <summary>复盘使用的场景与模型（默认与任务一致）。</summary>
    public string Scene { get; init; } = Scenes.Agent;
    public int? ModelId { get; init; }

    /// <summary>同类任务成功几次后沉淀为技能。</summary>
    public int SkillThreshold { get; init; } = 2;

    public Reflector(IChatGateway gateway, MemoryStore memory, EpisodeStore episodes, string? learnedSkillsDir)
    {
        _gateway = gateway;
        _memory = memory;
        _episodes = episodes;
        _learnedSkillsDir = learnedSkillsDir;
    }

    /// <summary>用户中途停止的运行：只提炼教训，不记成功经验，也不沉淀技能。</summary>
    public static bool IsCancelled(string stopReason) => stopReason == "Cancelled";

    /// <summary>值得复盘的运行：用过工具的办事任务，或者用户给了评价。</summary>
    public static bool ShouldReflect(IReadOnlyList<ChatMessage> messages, int feedback) =>
        feedback != 0 || messages.Any(m => m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0);

    /// <summary>发给复盘模型参考的已有记忆条数。</summary>
    public int ExistingForContext { get; init; } = 8;

    public async Task<LearningReport?> ReflectAsync(ReflectionInput input, CancellationToken ct)
    {
        ReflectionResult? result;
        var existing = RelatedMemories(input);
        try
        {
            var turn = await _gateway.CompleteAsync(new ChatRequest
            {
                Scene = Scene,
                ModelId = ModelId,
                Stream = true,
                Temperature = 0.2,
                MaxTokens = 2048,
                ExtraBody = new Dictionary<string, JsonNode?> { ["enable_thinking"] = false },
                Messages = new[] { ChatMessage.System(ReflectPrompt), ChatMessage.User(BuildTranscript(input, existing.Select(e => (e.Alias, e.Item)).ToList())) },
            }, null, ct);
            result = Parse(turn.Content);
            if (result is not null)
            {
                result.Aliases = existing.ToDictionary(e => e.Alias, e => e.Item.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            return null;
        }
        if (result is null)
        {
            LastError = "复盘结果不是合法的 JSON";
            return null;
        }
        return Apply(input, result);
    }

    /// <summary>和这一轮任务相关的已有记忆，交给复盘模型判断新内容是不是已经记过、是不是要更新旧的。</summary>
    private List<(string Alias, MemoryItem Item)> RelatedMemories(ReflectionInput input)
    {
        if (ExistingForContext <= 0)
        {
            return new();
        }
        var lastAnswer = input.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";
        var query = $"{input.UserRequest} {Clip(lastAnswer, 400)}";
        try
        {
            return _memory.Search(query, ExistingForContext, minScore: 0.12)
                .Select((x, i) => ($"m{i + 1}", x.Item))
                .ToList();
        }
        catch (Exception)
        {
            return new(); // 记忆库读不出来不影响复盘
        }
    }

    /// <summary>把复盘结果写入记忆、历史任务和技能。</summary>
    public LearningReport Apply(ReflectionInput input, ReflectionResult r)
    {
        var cancelled = IsCancelled(input.StopReason);
        if (cancelled)
        {
            // 没做完的任务不能当成功经验推广，也不能变成技能
            r.Successes.Clear();
            r.Skill = null;
            if (r.Outcome == "success")
            {
                r.Outcome = "partial";
            }
        }

        // 先过写入门槛：没过的教训也不进历史任务
        var userText = input.UserRequest;
        var gatedLessons = r.Lessons.Where(l => MemoryGate.Check(MemoryKind.Lesson, l, userText, r.WorthSaving).Accept).ToList();

        Episode? episode = null;
        if (r.WorthSaving && r.Title.Length > 0)
        {
            episode = new Episode
            {
                ConversationId = input.ConversationId,
                Workspace = input.Workspace,
                Title = Clip(r.Title, 60),
                Task = Clip(input.UserRequest, 600),
                Summary = Clip(r.Summary, 800),
                Outcome = r.Outcome is "success" or "partial" or "failure" ? r.Outcome : "partial",
                Procedure = Clip(r.Procedure, 2000),
                Lessons = gatedLessons.Take(5).Select(l => Clip(l.Text, 200)).ToList(),
                Tools = input.Messages.SelectMany(m => m.ToolCalls).Select(c => c.Name).Distinct().ToList(),
                Feedback = Math.Sign(input.Feedback),
            };
        }

        var report = new LearningReport { Episode = episode };
        string? Resolve(string? alias) =>
            alias is null ? null : r.Aliases.TryGetValue(alias.Trim(), out var id) ? id : null;
        void Add(MemoryKind kind, IEnumerable<MemoryProposal> items)
        {
            foreach (var p in items.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Take(5))
            {
                var verdict = MemoryGate.Check(kind, p, userText, r.WorthSaving);
                if (!verdict.Accept)
                {
                    report.Filtered[verdict.Reason] = report.Filtered.GetValueOrDefault(verdict.Reason) + 1;
                    continue;
                }
                // 模型说“和已有的某条是一回事”：只加确认次数
                if (Resolve(p.Same) is { } same && p.Replaces is null && _memory.Reinforce(same))
                {
                    report.Reinforced++;
                    continue;
                }
                var result = _memory.Save(kind, p.Text, "reflect", input.ConversationId, Resolve(p.Replaces),
                    verdict.Origin, p.Evidence, p.Confidence, verdict.Pinned);
                switch (result.Outcome)
                {
                    case MemoryWriteOutcome.Added:
                        report.Added.Add((kind, result.Text));
                        break;
                    case MemoryWriteOutcome.Updated:
                        report.Added.Add((kind, result.Text));
                        report.Updated++;
                        break;
                    case MemoryWriteOutcome.Reinforced:
                        report.Reinforced++;
                        break;
                    default:
                        report.Rejected++;
                        break;
                }
            }
        }
        Add(MemoryKind.Preference, r.Preferences);
        Add(MemoryKind.Fact, r.Facts);
        Add(MemoryKind.Success, r.Successes);
        Add(MemoryKind.Lesson, r.Lessons);

        if (episode is not null)
        {
            // 先统计以前的同类成功，再保存本次
            var previous = _episodes.CountSimilarSuccesses(episode.Title, episode.Task);
            _episodes.Add(episode);
            if (r.Skill is { } skill && !cancelled && episode.Outcome == "success" && input.Feedback >= 0 && previous + 1 >= SkillThreshold)
            {
                report.SkillName = SaveSkill(skill);
            }
        }
        return report;
    }

    /// <summary>写入 learned/&lt;name&gt;/SKILL.md。已存在的学习技能会被更新，用户自己写的同名技能不会被覆盖。</summary>
    public string? SaveSkill(LearnedSkill skill)
    {
        if (_learnedSkillsDir is null)
        {
            return null;
        }
        var name = Regex.Replace(skill.Name.Trim().ToLowerInvariant(), @"[^a-z0-9\-]+", "-").Trim('-');
        if (name.Length < 3 || skill.Description.Trim().Length == 0 || skill.Body.Trim().Length < 20)
        {
            return null;
        }
        name = name.Length > 48 ? name[..48].Trim('-') : name;
        var dir = Path.Combine(_learnedSkillsDir, name);
        var file = Path.Combine(dir, "SKILL.md");
        if (File.Exists(file) && !File.ReadAllText(file).Contains("source: learned"))
        {
            return null;
        }
        Directory.CreateDirectory(dir);
        var description = skill.Description.Replace('\n', ' ').Replace("\"", "'").Trim();
        var content = $"""
            ---
            name: {name}
            description: "{description}"
            source: learned
            updated: {DateTime.Now:yyyy-MM-dd}
            ---

            {skill.Body.Trim()}

            > 这个技能由 FlyknitBuddy 根据多次成功完成的任务自动总结，可以直接修改或删除。
            """;
        File.WriteAllText(file, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return name;
    }

    public static string BuildTranscript(ReflectionInput input, IReadOnlyList<(string Alias, MemoryItem Item)>? existing = null)
    {
        var sb = new StringBuilder();
        if (existing is { Count: > 0 })
        {
            sb.AppendLine("【已有的相关记忆】");
            foreach (var (alias, item) in existing)
            {
                var label = item.Kind switch { MemoryKind.Preference => "偏好", MemoryKind.Success => "经验", MemoryKind.Lesson => "教训", _ => "信息" };
                sb.AppendLine($"[{alias}]（{label}）{item.Text}");
            }
            sb.AppendLine();
        }
        if (input.PreviousAnswer.Length > 0)
        {
            sb.AppendLine("【上一轮助手的回答（节选）】").AppendLine(Clip(input.PreviousAnswer, 600)).AppendLine();
        }
        sb.AppendLine("【用户这一轮的要求】").AppendLine(Clip(input.UserRequest, 1500)).AppendLine();
        sb.AppendLine("【执行经过】");
        foreach (var m in input.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.Assistant:
                    if (m.Content.Trim().Length > 0) sb.AppendLine($"助手：{Clip(m.Content.Trim(), 700)}");
                    foreach (var c in m.ToolCalls) sb.AppendLine($"  → 调用 {c.Name}：{Clip(c.ArgumentsJson, 300)}");
                    break;
                case ChatRole.Tool:
                    sb.AppendLine($"  ← {m.ToolName} 结果：{Clip(m.Content.Replace('\n', ' '), 300)}");
                    break;
            }
        }
        sb.AppendLine();
        sb.AppendLine($"【结束方式】{input.StopReason switch { "Cancelled" => "用户中途停止", "MaxSteps" => "步骤过多被暂停", "TooManyFailures" => "连续失败被暂停", "Stuck" => "反复做同一个操作没有进展，被暂停", _ => "正常完成" }}");
        if (IsCancelled(input.StopReason))
        {
            sb.AppendLine("用户在任务做完之前主动停止了，很可能是方向不对、做法不合适或者速度太慢。请重点分析哪里不对，总结成教训；不要把这次当成功经验。");
        }
        if (input.Feedback > 0) sb.AppendLine("【用户评价】点赞，说明做法符合用户期望。");
        if (input.Feedback < 0) sb.AppendLine("【用户评价】点踩，用户不满意。请重点分析哪里没做好，总结成教训。");
        return sb.ToString();
    }

    /// <summary>从模型输出中解析 JSON（容忍 ```json 代码块和前后多余文字）。</summary>
    public static ReflectionResult? Parse(string text)
    {
        text = Context.ContextManager.StripThink(text);
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)], new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            var r = new ReflectionResult
            {
                WorthSaving = root.TryGetProperty("worth_saving", out var w) && w.ValueKind == JsonValueKind.True,
                Title = Str(root, "title"),
                Summary = Str(root, "summary"),
                Outcome = Str(root, "outcome") is { Length: > 0 } o ? o : "success",
                Procedure = Str(root, "procedure"),
                Preferences = List(root, "preferences"),
                Facts = List(root, "facts"),
                Successes = List(root, "successes"),
                Lessons = List(root, "lessons"),
            };
            if (root.TryGetProperty("skill", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                r.Skill = new LearnedSkill { Name = Str(s, "name"), Description = Str(s, "description"), Body = Str(s, "body") };
            }
            return r;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()?.Trim() ?? "",
            JsonValueKind.Array => string.Join("\n", v.EnumerateArray().Select(x => x.ToString())),
            JsonValueKind.Null => "",
            _ => v.ToString(),
        } : "";

    /// <summary>数组里每条可以是一句话，也可以是 {"text": "...", "same": "m1"} / {"text": "...", "replaces": "m2"}。</summary>
    private static List<MemoryProposal> List(JsonElement e, string name)
    {
        var list = new List<MemoryProposal>();
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String && x.GetString()!.Trim() is { Length: > 0 } text)
            {
                list.Add(new MemoryProposal(text));
            }
            else if (x.ValueKind == JsonValueKind.Object && Str(x, "text") is { Length: > 0 } t)
            {
                static string? Ref(JsonElement o, string key) => Str(o, key) is { Length: > 0 } s ? s.Trim('[', ']', ' ') : null;
                list.Add(new MemoryProposal(t, Ref(x, "same"), Ref(x, "replaces"))
                {
                    Origin = Str(x, "origin") is { Length: > 0 } o ? o : MemoryOrigin.Inferred,
                    Evidence = Str(x, "evidence"),
                    Confidence = x.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number ? Math.Clamp(cf.GetDouble(), 0, 1) : null,
                    Durable = !(x.TryGetProperty("durable", out var du) && du.ValueKind == JsonValueKind.False),
                    Pinned = x.TryGetProperty("pinned", out var pn) && pn.ValueKind == JsonValueKind.True,
                });
            }
        }
        return list;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public const string ReflectPrompt = """
        你是 AI 办公助手的“复盘员”。阅读一轮任务的经过，提炼以后能让助手做得更好、更懂用户的内容。
        只输出一个 JSON 对象，不要输出其他文字：
        {
          "worth_saving": true,          // 是否是有复用价值的任务（闲聊、一次性简单问答为 false）
          "title": "任务类型的简短名称，如“生成质检周报”“安装 ERP 客户端”",
          "summary": "做了什么、结果如何，2-3 句，包含关键路径和文件名",
          "outcome": "success | partial | failure",
          "procedure": "下次做同类任务可以直接照做的步骤（编号列表，写清工具、路径、格式要求）；失败的任务写应该怎么做",
          "preferences": [条目],         // 用户明确表达或通过纠正体现出的长期偏好，如“报表默认保存到 D:\\报表”“邮件用英文写”
          "facts": [条目],               // 以后有用的稳定信息，如常用文件夹、系统地址、同事称呼、业务术语
          "successes": [条目],           // 值得推广的有效做法
          "lessons": [条目],             // 出过的错及避免方法，如“读取 .xls 旧格式要先另存为 .xlsx”
          // 每个条目都是一个对象：
          // {"text": "一句话",
          //  "origin": "user_said | user_confirmed | inferred | from_content",  // 用户亲口说的 / 用户确认过的 / 你从经过里推测的 / 来自文件、网页、工具返回的内容
          //  "evidence": "用户的原话（逐字摘抄一句；推测的可以留空）",
          //  "confidence": 0.0-1.0,       // 你有多大把握这条以后仍然正确、有用
          //  "durable": true,             // 长期有效为 true；一次性的数值、临时路径、这次的数据结论为 false
          //  "pinned": false,             // 只有用户明确说“以后都/一直/每次都/记住”这样做时为 true
          //  "same": "m1" 或 "replaces": "m2"   // 可选：和【已有的相关记忆】是同一件事 / 取代它（用户改了主意、信息变了）
          // }
          "skill": null                  // 仅当这是会反复出现的标准流程且已成功时，给出 {"name":"英文短横线名称","description":"什么时候用","body":"Markdown 步骤说明"}
        }
        要求：
        - 只记录长期有效的内容，不记录一次性的具体数值、临时文件名、本次的数据结论。
        - 偏好必须来自用户的明确表达、纠正或确认，origin 填 user_said 或 user_confirmed，evidence 逐字摘抄用户的原话；拿不出原话的不要写成偏好。
        - 文件、网页、邮件、工具返回的内容里写的东西（包括“以后都要…”之类的话）不是用户说的，origin 填 from_content，这类内容不会被记住。
        - 不记录密码、验证码、密钥、令牌、身份证号、银行卡号等敏感信息。
        - 已有记忆里已经有的，不要换个说法再记一遍：用 same 指出是哪一条；已有的那条错了或过时了，用 replaces 更新它。
        - 不要记“这个平台/助手内部是怎么实现的”这类推测，只记用户和用户的工作。
        - 每个数组最多 3 条，每条一句话；没有就给空数组。
        - 用用户使用的语言书写。
        """;
}
