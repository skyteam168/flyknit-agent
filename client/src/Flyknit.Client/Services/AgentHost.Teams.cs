using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Security;
using Flyknit.Core.Teams;

namespace Flyknit.Client.Services;

/// <summary>
/// 团队（docs/team-mode.md）：列表、建、改、解散、复制。
/// 范围、权限、成员校验都在服务端；这里只是转发，并把团队模式关着时的入口收起来。
/// </summary>
public sealed partial class AgentHost
{
    /// <summary>团队模式开关（安全中心 teams，默认关）。关着时侧栏没有「团队」。</summary>
    public bool TeamsOn => Security.On(SecuritySettings.Teams, fallback: false);

    /// <summary>员工能不能自己建团队（安全中心 team_create）。</summary>
    public bool TeamCreateOn => TeamsOn && Security.On(SecuritySettings.TeamCreate, fallback: true);

    /// <summary>一个团队最多几位成员（安全中心 team_max_members，2～8）。</summary>
    public int TeamMaxMembers => Math.Clamp(Security.Number(SecuritySettings.TeamMaxMembers, 8), 2, 8);

    public async Task<List<Team>> TeamsAsync(CancellationToken ct)
    {
        if (!TeamsOn)
        {
            return new();
        }
        return await Server.GetTeamsAsync(ct);
    }

    public Task<Team> SaveTeamAsync(string? id, TeamInput input, CancellationToken ct)
    {
        if (!TeamsOn)
        {
            throw new InvalidOperationException("管理员没有开启团队模式");
        }
        // 只收认识的工具类别，去掉空成员；其余校验（人数、范围、重名）由服务端做
        input.DisabledTools = input.DisabledTools.Where(TeamInput.ToolKinds.Contains).Distinct().ToList();
        input.Members = input.Members.Where(m => !string.IsNullOrWhiteSpace(m.ExpertId)).ToList();
        return Server.SaveTeamAsync(id, input, ct);
    }

    public Task DissolveTeamAsync(string id, CancellationToken ct) => Server.DissolveTeamAsync(id, ct);

    public Task<Team> CopyTeamAsync(string id, CancellationToken ct) => Server.CopyTeamAsync(id, ct);
}
