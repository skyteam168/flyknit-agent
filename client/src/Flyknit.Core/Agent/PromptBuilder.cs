using System.Text;
using Flyknit.Core.Memory;
using Flyknit.Core.Skills;

namespace Flyknit.Core.Agent;

public enum ConversationMode
{
    Chat,
    Translate,
    Agent,
}

public static class Languages
{
    /// <summary>界面语言。</summary>
    public static readonly string[] UiLanguages = { "zh-CN", "vi-VN", "en-US" };

    /// <summary>可翻译语言：代码 → 名称（用于提示词，统一用英文名，模型理解最稳定）。</summary>
    public static readonly IReadOnlyDictionary<string, string> TranslateTargets = new Dictionary<string, string>
    {
        ["zh-CN"] = "Simplified Chinese",
        ["vi"] = "Vietnamese",
        ["en"] = "English",
        ["km"] = "Khmer",
        ["th"] = "Thai",
        ["id"] = "Indonesian",
        ["ja"] = "Japanese",
        ["ko"] = "Korean",
    };

    public static string DisplayName(string uiLanguage) => uiLanguage switch
    {
        "vi-VN" => "Vietnamese",
        "en-US" => "English",
        _ => "Simplified Chinese",
    };
}

public sealed class PromptContext
{
    public ConversationMode Mode { get; init; } = ConversationMode.Agent;
    public string UiLanguage { get; init; } = "zh-CN";

    /// <summary>翻译模式：源语言（"auto" 表示自动检测）与目标语言代码。</summary>
    public string TranslateFrom { get; init; } = "auto";
    public string TranslateTo { get; init; } = "vi";

    /// <summary>办事模式：工作区目录与权限模式。</summary>
    public string? Workspace { get; init; }
    public Security.PermissionMode Permission { get; init; } = Security.PermissionMode.Workspace;

    /// <summary>工作区隔离是否生效（安全中心里的项）。</summary>
    public bool Sandboxed { get; init; } = true;

    /// <summary>
    /// 讲给模型听的权限。隔离开着时「完全权限」按「工作区内修改」讲——
    /// 跟它说有完全权限而实际写不出去，它只会反复试同一件做不到的事。
    /// </summary>
    public Security.PermissionMode EffectivePermission =>
        Sandboxed && Permission == Security.PermissionMode.Full
            ? Security.PermissionMode.Workspace
            : Permission;

    /// <summary>当前的用户请求，用于挑选相关的记忆和历史任务。</summary>
    public string Query { get; init; } = "";
}

public sealed class PromptBuilder
{
    /// <summary>工作区中的项目说明文件（同 Claude Code 的 CLAUDE.md、Codex 的 AGENTS.md），按顺序取第一个存在的。</summary>
    public static readonly string[] WorkspaceInstructionFiles = { "FLYKNIT.md", "AGENTS.md", "CLAUDE.md" };

    private const int MaxWorkspaceInstructionChars = 8000;

    /// <summary>
    /// 界面能把这几种代码块直接画出来（实现见 client/web/src/render/blocks.ts）。
    /// 这段说明要和那边注册的语言保持一致。
    /// </summary>
    public const string ChartAndDiagramGuide = """
        <图表与流程图>
        回答里可以直接画图，界面会把下面这几种代码块渲染成图形，不用让用户另外打开文件：

        1. 数据图表，用 ```chart 代码块，内容是 JSON：
           {"type":"bar","title":"九月各车间产量","categories":["一车间","二车间","三车间"],
            "series":[{"name":"计划","data":[12000,9500,7800]},{"name":"实际","data":[12480,9120,8010]}]}
           type 可选 bar（柱状）、hbar（条形）、line（折线）、area（面积）、pie（饼图）、donut（环形）、scatter（散点）。
           可选字段：xLabel、yLabel、stacked（堆叠）。
           饼图和环形图必须有 categories；每组 data 的个数必须和 categories 个数一致，否则画不出来。

        2. 流程图、时序图、甘特图，用 ```mermaid 代码块，写 Mermaid 语法。
        3. 关系图、拓扑图，用 ```dot 代码块，写 Graphviz DOT 语法。

        什么时候画：用户要对比、看趋势、看占比，或者你在解释一个有步骤、有分支的流程时。
        一两个数字说清楚就行的，直接写在文字里，不要为了画图而画图。
        图表数据必须来自你真实读到的内容，不能编。
        </图表与流程图>
        """;

    private readonly MemoryStore? _memory;
    private readonly SkillCatalog? _skills;
    private readonly EpisodeStore? _episodes;

    public PromptBuilder(MemoryStore? memory, SkillCatalog? skills, EpisodeStore? episodes = null)
    {
        _memory = memory;
        _skills = skills;
        _episodes = episodes;
    }

    /// <summary>本次提示词引用的历史任务数。</summary>
    public int EpisodesUsed { get; private set; }

    public string Build(PromptContext ctx)
    {
        return ctx.Mode == ConversationMode.Translate ? BuildTranslate(ctx) : BuildAssistant(ctx);
    }

    private string BuildAssistant(PromptContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是 Flyknit 智能办公助手，运行在用户的 Windows 办公电脑上。");
        sb.AppendLine($"当前时间：{DateTime.Now:yyyy-MM-dd HH:mm dddd}；电脑名：{Environment.MachineName}；Windows 用户：{Environment.UserName}。");
        sb.AppendLine($"用户的界面语言是 {Languages.DisplayName(ctx.UiLanguage)}。默认用用户提问所用的语言回答；无法判断时用界面语言。");
        sb.AppendLine();

        if (ctx.Mode == ConversationMode.Agent)
        {
            sb.AppendLine("""
                你可以调用工具操作这台电脑。工作方式：
                1. 多步骤任务先用 update_plan 列出计划，每完成一步更新一次。
                2. 调用会修改电脑的工具前，用一句话说明要做什么、为什么，用户会看到并确认。
                3. 每次工具返回后检查结果，失败时分析原因再决定下一步，不要盲目重试。
                4. 被安全策略阻止或被用户拒绝的操作，不要换方式绕过，直接向用户说明。
                5. 用户上传的文件会以路径形式给出，用工具读取。
                6. 完成后简要总结做了什么、结果在哪里。
                """);
            if (!string.IsNullOrWhiteSpace(ctx.Workspace))
            {
                sb.AppendLine($"工作区：{ctx.Workspace}。相对路径、命令的默认工作目录都在这里；生成的文件、脚本、编译输出等都放在工作区内（可以建子文件夹），不要散落到桌面或其他目录。");
            }
            sb.AppendLine("当前权限：" + Security.PermissionModes.Describe(ctx.EffectivePermission, ctx.Workspace));
            sb.AppendLine();
        }

        sb.AppendLine(ChartAndDiagramGuide);
        sb.AppendLine();

        if (_memory is not null)
        {
            sb.AppendLine("""
                <记忆使用说明>
                下面的偏好、信息、经验教训和历史任务来自你和这位用户以前的工作。已知的偏好和信息直接使用，不要再问用户；
                同类任务优先沿用以前成功的做法，避开记录过的错误。记忆可能过时，与用户当前的明确要求冲突时以当前要求为准，
                并用 memory_write 记下新的偏好。用户纠正你的做法、或说“记住”“以后都”时，也要用 memory_write 记下来。
                </记忆使用说明>
                """);
            sb.AppendLine();
            sb.Append(_memory.BuildPromptSection(ctx.Query));
        }
        if (ctx.Mode == ConversationMode.Agent)
        {
            var project = ReadWorkspaceInstructions(ctx.Workspace);
            if (project is not null)
            {
                sb.AppendLine($"<工作区说明 文件=\"{project.Value.File}\">");
                sb.AppendLine(project.Value.Text);
                sb.AppendLine("</工作区说明>");
                sb.AppendLine();
            }
            if (_episodes is not null && ctx.Query.Length > 0)
            {
                var found = _episodes.Search(ctx.Query, max: 3, workspace: ctx.Workspace);
                EpisodesUsed = found.Count;
                if (found.Count > 0)
                {
                    sb.Append(EpisodeStore.BuildPromptSection(found));
                    sb.AppendLine();
                    _episodes.MarkUsed(found.Select(f => f.Episode.Id));
                }
            }
            if (_skills is not null)
            {
                sb.Append(_skills.BuildPromptSection(ctx.Query));
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>读取工作区的项目说明（FLYKNIT.md / AGENTS.md / CLAUDE.md）。</summary>
    public static (string File, string Text)? ReadWorkspaceInstructions(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !System.IO.Directory.Exists(workspace))
        {
            return null;
        }
        foreach (var name in WorkspaceInstructionFiles)
        {
            var path = Path.Combine(workspace, name);
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                var text = File.ReadAllText(path).Trim();
                if (text.Length == 0)
                {
                    continue;
                }
                return (name, text.Length > MaxWorkspaceInstructionChars ? text[..MaxWorkspaceInstructionChars] + "\n…（已截断）" : text);
            }
            catch (IOException)
            {
                // 文件被占用时跳过
            }
        }
        return null;
    }

    private static string BuildTranslate(PromptContext ctx) => TranslatePrompt(ctx.TranslateFrom, ctx.TranslateTo);

    /// <summary>
    /// 翻译提示词。翻译模式和划词翻译共用。
    /// </summary>
    /// <param name="from">源语言代码，"auto" 表示自动检测。</param>
    /// <param name="to">目标语言代码。</param>
    /// <param name="fallback">原文本来就是目标语言时改译成哪种语言。</param>
    /// <param name="lookup">划词查词：单个词或短语时允许列出几个常见意思。</param>
    public static string TranslatePrompt(string from, string to, string fallback = "zh-CN", bool lookup = false)
    {
        var target = LanguageName(to);
        var source = from == "auto"
            ? "auto-detect the source language"
            : $"the source language is {LanguageName(from)}";
        var lookupRule = lookup
            ? "\n- If the text is a single word or short term with several distinct common meanings, put the most common translation first, then up to 2 others, one per line."
            : "";

        return $"""
            You are a professional translator working in a manufacturing factory (textile / footwear).
            Translate the user's text into {target}; {source}.
            If the text is already in {target}, translate it into {LanguageName(fallback)} instead.
            Rules:
            - Output only the translation. No explanations, no quotes, no notes.
            - Keep the original formatting, line breaks, numbers, units, codes and product names.
            - Use accurate factory and office terminology; keep a natural, professional tone.{lookupRule}
            """;
    }

    private static string LanguageName(string code) =>
        Languages.TranslateTargets.TryGetValue(code, out var name) ? name : code;

    /// <summary>生成会话标题的提示词。</summary>
    public static string TitlePrompt(string uiLanguage) => $"""
        Summarize the conversation topic as a short title in {Languages.DisplayName(uiLanguage)}.
        Chinese: 6-15 characters. Other languages: at most 8 words.
        Output only the title, without quotes or punctuation at the end.
        """;
}
