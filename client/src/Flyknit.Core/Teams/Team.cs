using System.Text.Json.Serialization;

namespace Flyknit.Core.Teams;

/// <summary>
/// 团队（docs/team-mode.md）：2～8 位专家组成，主助手当队长。字段和服务端 schemas.TeamOut 一致。
/// 哪些团队看得到、谁能改由服务端决定（范围在服务端执行），员工端只按 <see cref="Editable"/> 显示按钮。
/// </summary>
public sealed class Team
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "team";
    [JsonPropertyName("leader_note")] public string LeaderNote { get; set; } = "";
    [JsonPropertyName("members")] public List<TeamMember> Members { get; set; } = new();
    [JsonPropertyName("model_id")] public int? ModelId { get; set; }

    /// <summary>这个团队不用的工具类别：shell / write / browser / mcp / developer。只能收紧。</summary>
    [JsonPropertyName("disabled_tools")] public List<string> DisabledTools { get; set; } = new();
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }

    /// <summary>it（IT 下发）/ department（本部门共享）/ personal（只我自己）。</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "it";
    [JsonPropertyName("owner_name")] public string OwnerName { get; set; } = "";
    [JsonPropertyName("department")] public string Department { get; set; } = "";

    /// <summary>这台电脑能不能改、解散（只有建的人）。</summary>
    [JsonPropertyName("editable")] public bool Editable { get; set; }
    [JsonPropertyName("runs")] public int Runs { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TeamMember
{
    [JsonPropertyName("expert_id")] public string ExpertId { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";

    /// <summary>这台电脑现在能不能用这位专家（下线、不在范围内 = false，开工时跳过）。</summary>
    [JsonPropertyName("available")] public bool Available { get; set; } = true;
}

/// <summary>员工建 / 改团队时提交的内容。部门由服务端按这台电脑登记的部门填。</summary>
public sealed class TeamInput
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "team";
    [JsonPropertyName("leader_note")] public string LeaderNote { get; set; } = "";
    [JsonPropertyName("members")] public List<TeamMember> Members { get; set; } = new();
    [JsonPropertyName("model_id")] public int? ModelId { get; set; }
    [JsonPropertyName("disabled_tools")] public List<string> DisabledTools { get; set; } = new();
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }

    /// <summary>department = 本部门共享；personal = 只我自己。</summary>
    [JsonPropertyName("share")] public string Share { get; set; } = "department";

    /// <summary>改的时候带上读到的 updated_at，别人先改过就会被拒绝（409）。</summary>
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>工具类别只认这几种（和服务端一致），其余丢掉。</summary>
    public static readonly IReadOnlySet<string> ToolKinds = new HashSet<string>(StringComparer.Ordinal) { "shell", "write", "browser", "mcp", "developer" };
}
