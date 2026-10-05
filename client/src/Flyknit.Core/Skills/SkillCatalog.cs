using System.Text;

namespace Flyknit.Core.Skills;

/// <summary>一个 Agent Skill（SKILL.md 格式，兼容 agentskills.io 开放标准）。</summary>
public sealed class SkillInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Directory { get; init; }

    /// <summary>企业下发的技能，只读。</summary>
    public bool IsOrganization { get; init; }

    public bool Enabled { get; set; } = true;

    /// <summary>由 Agent 复盘自动沉淀的技能（frontmatter 中 source: learned）。</summary>
    public bool IsLearned { get; init; }

    public string SkillFile => Path.Combine(Directory, "SKILL.md");

    /// <summary>SKILL.md 去掉 frontmatter 后的正文。</summary>
    public string LoadBody()
    {
        var text = File.ReadAllText(SkillFile);
        return SkillCatalog.SplitFrontmatter(text).Body.Trim();
    }

    public IReadOnlyList<string> ListFiles(int max = 200)
    {
        return System.IO.Directory
            .EnumerateFiles(Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Select(f => Path.GetRelativePath(Directory, f))
            .Where(f => !f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .Take(max)
            .ToList();
    }
}

public sealed class SkillCatalog
{
    private readonly List<(string Root, bool IsOrg)> _roots = new();
    private List<SkillInfo> _skills = new();

    public IReadOnlyList<SkillInfo> Skills => _skills;

    public SkillCatalog AddRoot(string root, bool isOrganization = false)
    {
        _roots.Add((root, isOrganization));
        return this;
    }

    /// <summary>扫描所有根目录，查找包含 SKILL.md 的文件夹（支持分类子目录）。</summary>
    public IReadOnlyList<SkillInfo> Refresh(ISet<string>? disabled = null)
    {
        var found = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, isOrg) in _roots)
        {
            if (!System.IO.Directory.Exists(root))
            {
                continue;
            }
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var file in System.IO.Directory.EnumerateFiles(root, "SKILL.md", options))
            {
                var skill = TryParse(file, isOrg);
                if (skill is null)
                {
                    continue;
                }
                skill.Enabled = disabled is null || !disabled.Contains(skill.Name);
                // 企业技能优先，同名的个人技能被忽略
                if (!found.TryGetValue(skill.Name, out var existing) || (!existing.IsOrganization && isOrg))
                {
                    found[skill.Name] = skill;
                }
            }
        }
        _skills = found.Values.OrderBy(s => s.Name).ToList();
        return _skills;
    }

    public SkillInfo? Find(string name) =>
        _skills.FirstOrDefault(s => s.Enabled && s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>系统提示词中的技能清单（只含名称和描述）。</summary>
    public string BuildPromptSection()
    {
        var enabled = _skills.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return "";
        }
        var sb = new StringBuilder();
        sb.AppendLine("<可用技能>");
        sb.AppendLine("以下技能与任务相关时，先调用 load_skill 读取完整说明，再按说明操作：");
        foreach (var s in enabled)
        {
            sb.AppendLine($"- {s.Name}：{s.Description}");
        }
        sb.AppendLine("</可用技能>");
        return sb.ToString();
    }

    public static SkillInfo? TryParse(string skillFile, bool isOrg)
    {
        try
        {
            var (meta, _) = SplitFrontmatter(File.ReadAllText(skillFile));
            var dir = Path.GetDirectoryName(skillFile)!;
            var name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileName(dir);
            var description = meta.TryGetValue("description", out var d) ? d : "";
            if (description.Length == 0)
            {
                return null; // 规范要求 description，没有就无法判断何时使用
            }
            var learned = meta.TryGetValue("source", out var src) && src.Equals("learned", StringComparison.OrdinalIgnoreCase);
            return new SkillInfo { Name = name, Description = description, Directory = dir, IsOrganization = isOrg, IsLearned = learned };
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>解析 YAML frontmatter 中的简单键值（支持引号与 &gt; / | 多行写法）。</summary>
    public static (Dictionary<string, string> Meta, string Body) SplitFrontmatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (meta, normalized);
        }
        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return (meta, normalized);
        }
        var header = normalized[4..end];
        var bodyStart = normalized.IndexOf('\n', end + 4);
        var body = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..];

        string? currentKey = null;
        var block = new StringBuilder();
        foreach (var line in header.Split('\n'))
        {
            if (currentKey is not null && (line.StartsWith(' ') || line.StartsWith('\t') || line.Length == 0))
            {
                block.Append(block.Length > 0 ? " " : "").Append(line.Trim());
                continue;
            }
            if (currentKey is not null)
            {
                meta[currentKey] = block.ToString().Trim();
                currentKey = null;
                block.Clear();
            }
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.StartsWith(' '))
            {
                continue;
            }
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is ">" or "|" or ">-" or "|-")
            {
                currentKey = key;
                continue;
            }
            meta[key] = Unquote(value);
        }
        if (currentKey is not null)
        {
            meta[currentKey] = block.ToString().Trim();
        }
        return (meta, body);
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
        {
            return v[1..^1];
        }
        return v;
    }
}
