using System.Text;
using Flyknit.Core.Context;

namespace Flyknit.Core.Skills;

/// <summary>技能的来源，决定存放位置与能否修改。</summary>
public enum SkillSource
{
    /// <summary>用户自己安装的。</summary>
    Personal,

    /// <summary>企业统一下发的，只读。</summary>
    Organization,

    /// <summary>Agent 复盘自动沉淀的。</summary>
    Learned,
}

/// <summary>一个 Agent Skill（SKILL.md 格式，兼容社区开放标准）。</summary>
public sealed class SkillInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Directory { get; init; }

    public SkillSource Source { get; init; } = SkillSource.Personal;

    /// <summary>frontmatter 中的其余字段（version、license、author、homepage、keywords 等）。</summary>
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();

    public bool Enabled { get; set; } = true;

    /// <summary>企业必装的技能不允许停用或卸载。</summary>
    public bool Required { get; set; }

    public bool IsOrganization => Source == SkillSource.Organization;
    public bool IsLearned => Source == SkillSource.Learned;

    public string Version => Meta.TryGetValue("version", out var v) ? v : "";
    public string Author => Meta.TryGetValue("author", out var v) ? v : "";
    public string License => Meta.TryGetValue("license", out var v) ? v : "";
    public string Homepage => Meta.TryGetValue("homepage", out var v) ? v : Meta.TryGetValue("repository", out var r) ? r : "";

    /// <summary>安装来源说明（本地文件、公司技能库、GitHub 链接等）。</summary>
    public string Origin => Meta.TryGetValue("origin", out var v) ? v : "";

    /// <summary>frontmatter 中的 keywords / tags，参与相关度匹配。</summary>
    public IReadOnlyList<string> Keywords =>
        (Meta.TryGetValue("keywords", out var k) ? k : Meta.TryGetValue("tags", out var t) ? t : "")
        .Split(new[] { ',', '，', ';', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => s.Trim('[', ']', '"', '\''))
        .Where(s => s.Length > 0)
        .ToList();

    public string SkillFile => Path.Combine(Directory, "SKILL.md");

    /// <summary>相关度匹配用的文本。</summary>
    public string SearchText => $"{Name} {Description} {string.Join(" ", Keywords)}";

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

    /// <summary>包含的可执行脚本（安装时提示用户注意）。</summary>
    public IReadOnlyList<string> ListScripts() =>
        ListFiles(500).Where(f => SkillPackage.ScriptExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();

    public long TotalBytes()
    {
        try
        {
            return new DirectoryInfo(Directory)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}

/// <summary>
/// 技能目录的扫描与注册。支持多个根目录（企业、个人、复盘沉淀），
/// 文件变化后自动重新扫描（热插拔），停用状态由宿主提供。
/// </summary>
public sealed class SkillCatalog : IDisposable
{
    /// <summary>提示词里逐条列出技能的上限；超过后按相关度挑选。</summary>
    public const int PromptListLimit = 12;

    private readonly List<(string Root, SkillSource Source)> _roots = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _lock = new();
    private List<SkillInfo> _skills = new();
    private ISet<string> _disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private ISet<string> _required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _debounce;

    public IReadOnlyList<SkillInfo> Skills => _skills;

    /// <summary>目录内容变化、或启用状态变化后触发（界面据此刷新）。</summary>
    public event Action? Changed;

    public SkillCatalog AddRoot(string root, SkillSource source = SkillSource.Personal)
    {
        _roots.Add((root, source));
        return this;
    }

    /// <summary>兼容旧调用。</summary>
    public SkillCatalog AddRoot(string root, bool isOrganization) =>
        AddRoot(root, isOrganization ? SkillSource.Organization : SkillSource.Personal);

    /// <summary>设置停用的技能名与企业必装的技能名。</summary>
    public void SetState(ISet<string>? disabled, ISet<string>? required = null)
    {
        _disabled = disabled ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (required is not null)
        {
            _required = required;
        }
        Refresh();
    }

    /// <summary>监听技能目录，拷入、删除、修改技能后自动重新注册（防抖 400ms）。</summary>
    public void StartWatching()
    {
        foreach (var (root, _) in _roots)
        {
            try
            {
                System.IO.Directory.CreateDirectory(root);
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                w.Created += OnFileEvent;
                w.Deleted += OnFileEvent;
                w.Changed += OnFileEvent;
                w.Renamed += OnFileEvent;
                _watchers.Add(w);
            }
            catch (Exception)
            {
                // 目录不可用时放弃监听，手动刷新仍然可用
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        _debounce ??= new System.Threading.Timer(_ =>
        {
            Refresh();
            Changed?.Invoke();
        });
        _debounce.Change(400, Timeout.Infinite);
    }

    /// <summary>扫描所有根目录，查找包含 SKILL.md 的文件夹（支持分类子目录）。</summary>
    public IReadOnlyList<SkillInfo> Refresh(ISet<string>? disabled = null)
    {
        if (disabled is not null)
        {
            _disabled = disabled;
        }
        var found = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, source) in _roots)
        {
            if (!System.IO.Directory.Exists(root))
            {
                continue;
            }
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var file in System.IO.Directory.EnumerateFiles(root, "SKILL.md", options))
            {
                var skill = TryParse(file, source);
                if (skill is null)
                {
                    continue;
                }
                skill.Required = skill.IsOrganization && _required.Contains(skill.Name);
                skill.Enabled = skill.Required || !_disabled.Contains(skill.Name);
                // 企业技能优先，同名的个人技能被忽略
                if (!found.TryGetValue(skill.Name, out var existing) || (!existing.IsOrganization && skill.IsOrganization))
                {
                    found[skill.Name] = skill;
                }
            }
        }
        lock (_lock)
        {
            _skills = found.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return _skills;
    }

    public SkillInfo? Find(string name) =>
        _skills.FirstOrDefault(s => s.Enabled && s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public SkillInfo? FindAny(string name) =>
        _skills.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>按相关度搜索已启用的技能。</summary>
    public List<(SkillInfo Skill, double Score)> Search(string query, int max = 8, double minScore = 0.05)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _skills.Where(s => s.Enabled).Take(max).Select(s => (s, 0d)).ToList();
        }
        return _skills
            .Where(s => s.Enabled)
            .Select(s => (Skill: s, Score: Math.Max(
                TextSimilarity.Relevance(query, s.Name) * 1.2,
                TextSimilarity.Relevance(query, s.SearchText))))
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// 系统提示词中的技能清单（只含名称和描述，模型需要时再用 load_skill 读全文）。
    /// 技能较多时只列出与当前任务相关的，其余让模型用 search_skills 检索。
    /// </summary>
    public string BuildPromptSection(string? query = null)
    {
        var enabled = _skills.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return "";
        }

        var listed = enabled;
        var truncated = false;
        if (enabled.Count > PromptListLimit && !string.IsNullOrWhiteSpace(query))
        {
            var ranked = enabled
                .Select(s => (Skill: s, Score: Math.Max(
                    TextSimilarity.Relevance(query!, s.Name) * 1.2,
                    TextSimilarity.Relevance(query!, s.SearchText))))
                .OrderByDescending(x => x.Score)
                .Take(PromptListLimit)
                .Select(x => x.Skill)
                .ToHashSet();
            // 企业必装的技能始终列出
            foreach (var s in enabled.Where(s => s.Required))
            {
                ranked.Add(s);
            }
            listed = enabled.Where(ranked.Contains).ToList();
            truncated = listed.Count < enabled.Count;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<可用技能>");
        sb.AppendLine("技能是针对某类任务写好的工作说明。判断某个技能与当前任务相关时，先调用 load_skill 读取完整说明，再按说明操作；不相关就不要加载。");
        foreach (var s in listed)
        {
            sb.AppendLine($"- {s.Name}：{s.Description}");
        }
        if (truncated)
        {
            sb.AppendLine($"（以上是与当前任务最相关的 {listed.Count} 个，共有 {enabled.Count} 个技能。没有合适的就用 search_skills 按关键词检索。）");
        }
        sb.AppendLine("</可用技能>");
        return sb.ToString();
    }

    public static SkillInfo? TryParse(string skillFile, SkillSource source)
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
            var kind = meta.TryGetValue("source", out var src) && src.Equals("learned", StringComparison.OrdinalIgnoreCase)
                ? SkillSource.Learned
                : source;
            return new SkillInfo { Name = name, Description = description, Directory = dir, Source = kind, Meta = meta };
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>兼容旧调用。</summary>
    public static SkillInfo? TryParse(string skillFile, bool isOrg) =>
        TryParse(skillFile, isOrg ? SkillSource.Organization : SkillSource.Personal);

    /// <summary>解析 YAML frontmatter 中的简单键值（支持引号与 &gt; / | 多行写法）。</summary>
    public static (Dictionary<string, string> Meta, string Body) SplitFrontmatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.StartsWith('﻿'))
        {
            normalized = normalized[1..];
        }
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
                var item = line.Trim();
                // 列表写法（keywords:\n  - excel\n  - report）转成逗号分隔，续行写法用空格拼接
                var isItem = item.StartsWith("- ", StringComparison.Ordinal);
                if (block.Length > 0)
                {
                    block.Append(isItem ? ", " : " ");
                }
                block.Append(isItem ? item[2..].Trim().Trim('"', '\'') : item);
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
            if (value is ">" or "|" or ">-" or "|-" or "")
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

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        _debounce?.Dispose();
    }
}
