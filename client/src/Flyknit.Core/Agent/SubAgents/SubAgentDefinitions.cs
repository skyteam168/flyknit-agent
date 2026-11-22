using Flyknit.Core.Security;

namespace Flyknit.Core.Agent.SubAgents;

/// <summary>子代理能选的模型：IT 在后台启用、能调用工具的那些（和员工在输入框里能选的是同一批）。</summary>
public sealed record SubAgentModel(int Id, string Name, string Model);

/// <summary>
/// 项目里定义的子代理（参照 Cursor 的子代理文件）：放在工作区的 <c>.flyknit/agents/*.md</c>，开发部门自己维护、跟着项目走。
/// <code>
/// ---
/// name: reviewer
/// description: 改完代码后做代码审查，找 bug 和不符合约定的地方
/// model: 某个后台启用的模型名（可选）
/// read_only: true            # 可选，默认 true
/// tools: read_file, grep     # 可选：只给这些工具（只能更少）
/// ---
/// 你是代码审查员……（角色说明）
/// </code>
/// 和「专家」的分工：专家是 IT 在后台配的、全公司用的固定角色；这里是项目自己的、主助手按名字派的分身。
///
/// 这些文件来自项目（谁都能改），不是员工的指令：
/// - 只能收紧：工具只能从主助手这一轮的工具里再挑；read_only: false 也只是和 spawn_agent 自己的 read_only=false 一样，每个改动照常确认；
/// - 有疑似提示词注入的文件不用；
/// - 模型只能是后台启用的（服务端还会再查一遍是否启用）。
/// </summary>
public sealed record SubAgentDefinition(string Name, string Description, string Prompt, string? Model, bool ReadOnly, IReadOnlySet<string>? Tools, string File);

public static class SubAgentDefinitions
{
    public const string Folder = ".flyknit/agents";
    public const int MaxFiles = 20;
    public const int MaxFileChars = 8000;

    /// <summary>读工作区里的子代理定义。读不到、没有文件夹返回空；有问题的文件跳过，原因放在 rejected 里（写日志用）。</summary>
    public static (IReadOnlyList<SubAgentDefinition> Definitions, IReadOnlyList<string> Rejected) Load(string? workspace)
    {
        var defs = new List<SubAgentDefinition>();
        var rejected = new List<string>();
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return (defs, rejected);
        }
        var dir = Path.Combine(workspace, ".flyknit", "agents");
        IEnumerable<string> files;
        try
        {
            if (!Directory.Exists(dir))
            {
                return (defs, rejected);
            }
            files = Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (defs, rejected);
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                // 链接指到工作区外面的不读（和编辑器读写一样的规矩）
                if (new FileInfo(file).LinkTarget is not null)
                {
                    rejected.Add($"{name}：是链接，不读");
                    continue;
                }
                var text = File.ReadAllText(file);
                if (text.Length > MaxFileChars)
                {
                    rejected.Add($"{name}：超过 {MaxFileChars} 字");
                    continue;
                }
                var parsed = Parse(text, file, out var why);
                if (parsed is null)
                {
                    rejected.Add($"{name}：{why}");
                }
                else if (defs.Any(d => d.Name.Equals(parsed.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    rejected.Add($"{name}：和别的文件重名（{parsed.Name}）");
                }
                else
                {
                    defs.Add(parsed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rejected.Add($"{name}：读不了");
            }
        }
        return (defs, rejected);
    }

    /// <summary>解析一个定义文件：开头 --- 之间是 key: value，后面是角色说明。</summary>
    public static SubAgentDefinition? Parse(string text, string file, out string why)
    {
        why = "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (end < 0)
            {
                why = "开头的 --- 没有结束";
                return null;
            }
            foreach (var line in lines[1..end])
            {
                var i = line.IndexOf(':');
                if (i > 0)
                {
                    fields[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"', '\'');
                }
            }
            start = end + 1;
        }
        var body = string.Join('\n', lines[start..]).Trim();
        var name = fields.GetValueOrDefault("name") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(file);
        name = name.Trim();
        if (name.Length is 0 or > 40 || name.Any(c => char.IsWhiteSpace(c) || c is '"' or '<' or '>'))
        {
            why = "name 要 1~40 个字，不能有空格和引号";
            return null;
        }
        if (body.Length < 10)
        {
            why = "角色说明太短";
            return null;
        }
        // 项目文件是数据：像在给 AI 下指令（「忽略之前的规则」之类）的不用
        if (PromptInjectionGuard.Scan(body + "\n" + fields.GetValueOrDefault("description")) is { } bad)
        {
            why = $"有疑似提示词注入的内容（{bad.Excerpt}），没有采用";
            return null;
        }
        var tools = fields.GetValueOrDefault("tools") is { Length: > 0 } t
            ? t.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Trim('"', '\'')).Where(x => x.Length > 0)
                .ToHashSet(StringComparer.Ordinal)
            : null;
        var readOnly = !(fields.GetValueOrDefault("read_only") is { } ro && ro.Equals("false", StringComparison.OrdinalIgnoreCase));
        var description = fields.GetValueOrDefault("description") ?? "";
        return new SubAgentDefinition(name, description.Length > 200 ? description[..200] : description, body,
            fields.GetValueOrDefault("model") is { Length: > 0 } m ? m : null, readOnly, tools is { Count: > 0 } ? tools : null, file);
    }

    /// <summary>按名字找模型（后台的显示名或模型标识都认，不分大小写）。</summary>
    public static SubAgentModel? FindModel(IReadOnlyList<SubAgentModel> models, string name) =>
        models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault(m => m.Model.Equals(name, StringComparison.OrdinalIgnoreCase));
}
