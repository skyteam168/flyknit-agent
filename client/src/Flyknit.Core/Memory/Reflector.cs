using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flyknit.Core.Chat;
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

    /// <summary>Completed / Cancelled / MaxSteps / TooManyFailures</summary>
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
    public List<string> Preferences { get; set; } = new();
    public List<string> Facts { get; set; } = new();
    public List<string> Successes { get; set; } = new();
    public List<string> Lessons { get; set; } = new();
    public LearnedSkill? Skill { get; set; }
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

    /// <summary>值得复盘的运行：用过工具的办事任务，或者用户给了评价。</summary>
    public static bool ShouldReflect(IReadOnlyList<ChatMessage> messages, int feedback) =>
        feedback != 0 || messages.Any(m => m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0);

    public async Task<LearningReport?> ReflectAsync(ReflectionInput input, CancellationToken ct)
    {
        ReflectionResult? result;
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
                Messages = new[] { ChatMessage.System(ReflectPrompt), ChatMessage.User(BuildTranscript(input)) },
            }, null, ct);
            result = Parse(turn.Content);
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

    /// <summary>把复盘结果写入记忆、历史任务和技能。</summary>
    public LearningReport Apply(ReflectionInput input, ReflectionResult r)
    {
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
                Lessons = r.Lessons.Take(5).Select(l => Clip(l, 200)).ToList(),
                Tools = input.Messages.SelectMany(m => m.ToolCalls).Select(c => c.Name).Distinct().ToList(),
                Feedback = Math.Sign(input.Feedback),
            };
        }

        var report = new LearningReport { Episode = episode };
        void Add(MemoryKind kind, IEnumerable<string> items)
        {
            foreach (var text in items.Where(t => !string.IsNullOrWhiteSpace(t)).Take(5))
            {
                if (_memory.Add(kind, text))
                {
                    report.Added.Add((kind, text.Trim()));
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
            if (r.Skill is { } skill && episode.Outcome == "success" && input.Feedback >= 0 && previous + 1 >= SkillThreshold)
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

    public static string BuildTranscript(ReflectionInput input)
    {
        var sb = new StringBuilder();
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
        sb.AppendLine($"【结束方式】{input.StopReason switch { "Cancelled" => "用户中途停止", "MaxSteps" => "步骤过多被暂停", "TooManyFailures" => "连续失败被暂停", _ => "正常完成" }}");
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

    private static List<string> List(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
            : new();

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
          "preferences": ["用户明确表达或通过纠正体现出的长期偏好，如“报表默认保存到 D:\\报表”“邮件用英文写”"],
          "facts": ["以后有用的稳定信息，如常用文件夹、系统地址、同事称呼、业务术语"],
          "successes": ["值得推广的有效做法（一句话）"],
          "lessons": ["出过的错及避免方法，如“读取 .xls 旧格式要先另存为 .xlsx”"],
          "skill": null                  // 仅当这是会反复出现的标准流程且已成功时，给出 {"name":"英文短横线名称","description":"什么时候用","body":"Markdown 步骤说明"}
        }
        要求：
        - 只记录长期有效的内容，不记录一次性的具体数值、临时文件名、本次的数据结论。
        - 偏好必须来自用户的明确表达、纠正或确认，不要凭空推测。
        - 不记录密码、验证码、身份证号等敏感信息。
        - 每个数组最多 3 条，每条一句话；没有就给空数组。
        - 用用户使用的语言书写。
        """;
}
