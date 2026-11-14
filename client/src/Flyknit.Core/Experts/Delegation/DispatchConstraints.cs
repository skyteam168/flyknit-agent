namespace Flyknit.Core.Experts.Delegation;

/// <summary>
/// 调用方给一次派遣另加的约束（团队、以后的工作流之类的编排插件用）。派遣器只认约束，不认识「团队」。
///
/// 约束只会更严（docs/extension-architecture.md「只做减法」）：关掉的工具类别从专家工具里去掉、
/// 只读压成只读、专家没被 IT 指定模型时才用这里的模型；开关、次数上限由调用方给，服务端另有一层校验。
/// </summary>
public sealed record DispatchConstraints
{
    /// <summary>
    /// 服务端按它核对这批派遣（目前只有团队：登记子任务时作为 team_id 发给服务端，服务端查团队开关、范围、成员）。
    /// 空 = 普通派遣。
    /// </summary>
    public string GroupId { get; init; } = "";

    /// <summary>这类派遣看哪个开关：关着时不派，正在跑的停下。null = 看「派遣专家」开关。</summary>
    public Func<bool>? Enabled { get; init; }

    /// <summary>开关关着时交回主助手的话。</summary>
    public string DisabledMessage { get; init; } = "管理员关闭了这项功能。";

    /// <summary>专家没被 IT 指定模型时用这个模型（服务端模型 ID）。</summary>
    public int? ModelId { get; init; }

    /// <summary>另外关掉的工具类别（<see cref="Tools.ToolKinds"/>）。</summary>
    public IReadOnlyList<string> DisabledKinds { get; init; } = Array.Empty<string>();

    /// <summary>压成只读。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>这次运行最多派遣几次。null = 派遣器默认（<see cref="DelegationLimits.MaxPerRun"/>）。</summary>
    public int? MaxPerRun { get; init; }

    /// <summary>
    /// 调用方开工前已经统一问过员工授权：不再单独出派遣授权卡，<see cref="PreGranted"/> 里的会话授权键记在这次派遣那层。
    /// </summary>
    public bool GrantedUpfront { get; init; }

    /// <summary>开工授权里员工同意了的会话授权键。</summary>
    public IReadOnlyCollection<string> PreGranted { get; init; } = Array.Empty<string>();

    /// <summary>在专家快照上收紧一层（之后的工具筛选、只读、确认都照专家的逻辑走）。</summary>
    internal void ApplyTo(Expert snapshot)
    {
        snapshot.ConstraintDisabledKinds = DisabledKinds.Distinct(StringComparer.Ordinal).ToList();
        snapshot.ReadOnly = snapshot.ReadOnly || ReadOnly;
        snapshot.ModelId ??= ModelId;
    }
}
