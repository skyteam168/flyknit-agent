using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flyknit.Core.Agent;
using Flyknit.Core.Mcp;
using Flyknit.Core.Security;
using Flyknit.Core.Tools;

namespace Flyknit.Core.Experts;

/// <summary>
/// 专家：管理员在后台建好、发布的垂直场景智能体（MES 生产分析专家、IT 运维专家……）。
/// 员工端拿到的是发布时的快照。
///
/// 专家不是另一套引擎：跑的还是同一个 <see cref="AgentLoop"/>，只是换上专家的工作方式和知识，
/// 按专家的限制筛一遍工具。技能、连接器、工具默认全部沿用，以后新加的自动可用。
/// </summary>
public sealed class Expert
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>一个 emoji，或图片链接 / data: URL。</summary>
    [JsonPropertyName("avatar")] public string Avatar { get; set; } = "";
    [JsonPropertyName("tagline")] public string Tagline { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    /// <summary>署名：谁维护这位专家。</summary>
    [JsonPropertyName("author")] public string Author { get; set; } = "";

    /// <summary>名片：负责什么、适合什么任务、不负责什么、需要什么信息。</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>工作方式：专家的提示词。</summary>
    [JsonPropertyName("instructions")] public string Instructions { get; set; } = "";

    /// <summary>专家知识：管理员维护的固定业务信息。</summary>
    [JsonPropertyName("knowledge")] public string Knowledge { get; set; } = "";
    [JsonPropertyName("examples")] public List<string> Examples { get; set; } = new();

    /// <summary>优先使用的技能。只是提示，别的技能照样能找到、能用。</summary>
    [JsonPropertyName("preferred_skills")] public List<string> PreferredSkills { get; set; } = new();

    /// <summary>只用这些连接器。空 = 员工连上的全部都能用。</summary>
    [JsonPropertyName("mcp_servers")] public List<string> McpServers { get; set; } = new();

    /// <summary>不让专家用的工具。</summary>
    [JsonPropertyName("disallowed_tools")] public List<string> DisallowedTools { get; set; } = new();

    /// <summary>只读：权限压到「仅可查看」，并去掉写文件、删文件。</summary>
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }

    /// <summary>指定的模型（服务端模型 ID）。空 = 和普通「办事」一样。</summary>
    [JsonPropertyName("model_id")] public int? ModelId { get; set; }
    [JsonPropertyName("allow_direct")] public bool AllowDirect { get; set; } = true;
    [JsonPropertyName("allow_delegate")] public bool AllowDelegate { get; set; } = true;
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }

    /// <summary>全公司累计使用次数（员工每向专家发一句话算一次）。</summary>
    [JsonPropertyName("uses")] public int Uses { get; set; }

    /// <summary>用过这位专家的设备数。</summary>
    [JsonPropertyName("users")] public int Users { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>专家在一次运行里的具体效果：筛工具、压权限、写进提示词。</summary>
public static class ExpertRuntime
{
    /// <summary>只读专家去掉的工具。执行命令保留：「仅可查看」权限下命令策略只放行查询类命令。</summary>
    public static readonly IReadOnlySet<string> WriteTools = new HashSet<string>(StringComparer.Ordinal) { "write_file", "delete_path" };

    public static bool Allows(Expert expert, ITool tool)
    {
        if (expert.DisallowedTools.Contains(tool.Name, StringComparer.Ordinal))
        {
            return false;
        }
        if (expert.ReadOnly && WriteTools.Contains(tool.Name))
        {
            return false;
        }
        if (tool is McpTool mcp && expert.McpServers.Count > 0)
        {
            return expert.McpServers.Contains(mcp.ServerId, StringComparer.OrdinalIgnoreCase);
        }
        return true;
    }

    /// <summary>按专家的限制筛出这次能用的工具。原来的工具表不动（别的对话还在用）。</summary>
    public static ToolRegistry FilterTools(ToolRegistry all, Expert expert)
    {
        var filtered = new ToolRegistry();
        foreach (var tool in all.All.Where(t => Allows(expert, t)))
        {
            filtered.Add(tool);
        }
        return filtered;
    }

    /// <summary>只读专家把权限压到「仅可查看」；员工自己选的更严时以员工的为准。</summary>
    public static PermissionMode CapPermission(Expert expert, PermissionMode chosen) =>
        expert.ReadOnly ? PermissionMode.ReadOnly : chosen;

    public static IReadOnlyList<McpPromptInfo> FilterMcp(Expert expert, IReadOnlyList<McpPromptInfo> servers) =>
        expert.McpServers.Count == 0
            ? servers
            : servers.Where(s => expert.McpServers.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>每段最多放这么多字，免得一位专家的长文挤掉记忆和技能。</summary>
    private const int MaxSectionChars = 8000;

    /// <summary>提示词里的专家身份：工作方式、职责、专家知识、优先技能。</summary>
    public static string BuildPromptSection(Expert expert)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<专家身份 名称=\"{expert.Name}\">");
        sb.AppendLine($"这个对话里你作为“{expert.Name}”工作。下面是管理员为这位专家写的工作方式和业务知识，按它来做；"
            + "但上面的安全规则、权限限制和用户确认照样有效，不能因为专家身份绕过。");
        if (!string.IsNullOrWhiteSpace(expert.Description))
        {
            sb.AppendLine("<职责>");
            sb.AppendLine(Clip(expert.Description));
            sb.AppendLine("</职责>");
            sb.AppendLine("用户的问题超出职责范围（属于“不负责”的内容）时，直接说明这不是你负责的，建议用户找普通助手或对应的专家，不要硬做。");
        }
        if (!string.IsNullOrWhiteSpace(expert.Instructions))
        {
            sb.AppendLine("<工作方式>");
            sb.AppendLine(Clip(expert.Instructions));
            sb.AppendLine("</工作方式>");
        }
        if (!string.IsNullOrWhiteSpace(expert.Knowledge))
        {
            sb.AppendLine("<专家知识>");
            sb.AppendLine(Clip(expert.Knowledge));
            sb.AppendLine("</专家知识>");
        }
        if (expert.PreferredSkills.Count > 0)
        {
            sb.AppendLine($"做这类任务时优先考虑这些技能：{string.Join("、", expert.PreferredSkills)}（需要时用 load_skill 加载）。别的技能需要时也可以用。");
        }
        if (expert.ReadOnly)
        {
            sb.AppendLine("你是只读专家：只做查询和分析，不修改、不删除任何文件和数据。需要改动时告诉用户怎么做，或者建议用户回到普通助手去做。");
        }
        sb.AppendLine("</专家身份>");
        return sb.ToString();
    }

    private static string Clip(string text)
    {
        var t = text.Trim();
        return t.Length > MaxSectionChars ? t[..MaxSectionChars] + "…" : t;
    }
}

/// <summary>
/// 本机缓存的专家列表（服务端发布的快照）。存在一个 JSON 文件里：没网的时候照样能打开专家对话，
/// 联网后按服务端的列表整体替换——下线、删除的专家随之消失。
/// </summary>
public sealed class ExpertCatalog
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly object _gate = new();
    private List<Expert> _experts = new();

    public ExpertCatalog(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file))
            {
                _experts = JsonSerializer.Deserialize<List<Expert>>(File.ReadAllText(file)) ?? new();
            }
        }
        catch (Exception)
        {
            // 缓存坏了就当没有，联网后重新拉
            _experts = new();
        }
    }

    public event Action? Changed;

    public IReadOnlyList<Expert> All
    {
        get
        {
            lock (_gate)
            {
                return _experts.ToList();
            }
        }
    }

    /// <summary>员工能在「专家」里看到、呼叫的。</summary>
    public IReadOnlyList<Expert> Callable => All.Where(e => e.AllowDirect).ToList();

    public Expert? Get(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        lock (_gate)
        {
            return _experts.FirstOrDefault(e => e.Id == id);
        }
    }

    /// <summary>换成服务端的最新列表。内容没变就什么也不做，返回是否有变化。</summary>
    public bool Replace(IEnumerable<Expert> experts)
    {
        var next = experts.Where(e => !string.IsNullOrWhiteSpace(e.Id)).ToList();
        var text = JsonSerializer.Serialize(next, Json);
        lock (_gate)
        {
            if (JsonSerializer.Serialize(_experts, Json) == text)
            {
                return false;
            }
            _experts = next;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, text);
            }
            catch (Exception)
            {
                // 写不了缓存不影响这次使用，下次联网再拉
            }
        }
        Changed?.Invoke();
        return true;
    }
}
