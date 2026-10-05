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

    /// <summary>当前的用户请求，用于挑选相关的记忆和历史任务。</summary>
    public string Query { get; init; } = "";
}

public sealed class PromptBuilder
{
    /// <summary>工作区中的项目说明文件（同 Claude Code 的 CLAUDE.md、Codex 的 AGENTS.md），按顺序取第一个存在的。</summary>
    public static readonly string[] WorkspaceInstructionFiles = { "FLYKNIT.md", "AGENTS.md", "CLAUDE.md" };

    private const int MaxWorkspaceInstructionChars = 8000;

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
            sb.AppendLine("当前权限：" + Security.PermissionModes.Describe(ctx.Permission, ctx.Workspace));
            sb.AppendLine();
        }

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

    private static string BuildTranslate(PromptContext ctx)
    {
        var target = Languages.TranslateTargets.TryGetValue(ctx.TranslateTo, out var t) ? t : ctx.TranslateTo;
        var source = ctx.TranslateFrom == "auto"
            ? "auto-detect the source language"
            : $"the source language is {(Languages.TranslateTargets.TryGetValue(ctx.TranslateFrom, out var s) ? s : ctx.TranslateFrom)}";

        return $"""
            You are a professional translator working in a manufacturing factory (textile / footwear).
            Translate the user's text into {target}; {source}.
            If the text is already in {target}, translate it into Simplified Chinese instead.
            Rules:
            - Output only the translation. No explanations, no quotes, no notes.
            - Keep the original formatting, line breaks, numbers, units, codes and product names.
            - Use accurate factory and office terminology; keep a natural, professional tone.
            """;
    }

    /// <summary>生成会话标题的提示词。</summary>
    public static string TitlePrompt(string uiLanguage) => $"""
        Summarize the conversation topic as a short title in {Languages.DisplayName(uiLanguage)}.
        Chinese: 6-15 characters. Other languages: at most 8 words.
        Output only the title, without quotes or punctuation at the end.
        """;
}
