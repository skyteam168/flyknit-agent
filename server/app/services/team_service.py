"""团队模式在服务端的规则（docs/team-mode.md）：可见范围、派遣登记前的检查、派遣次数。

团队作为一个功能模块接在派遣上（docs/extension-architecture.md）：子任务路由只在请求带了 team_id 时调用
:func:`check_dispatch`，团队的开关、范围、成员资格、次数规则都在这里，不散在路由里。
"""

from __future__ import annotations

from sqlalchemy.ext.asyncio import AsyncSession

from ..models import Device, Team
from . import device_security, item_scope
from .department_store import key

#: 团队任务的派遣次数：每位成员两次（首次 + 一次重试或补派），最多 16 次
TEAM_MAX_PER_RUN = 16


def visible(team: Team, device: Device) -> bool:
    """这台电脑看不看得到这个团队：解散的都看不到；个人团队只有建的人；部门团队只有同部门；IT 团队按使用范围。"""
    if team.dissolved_at is not None:
        return False
    if team.source == "personal":
        return team.owner_device_id == device.id
    if team.source == "department":
        return bool(team.department) and key(team.department) == key(device.department)
    return item_scope.in_scope(team, device)


def dispatch_limit(members: int) -> int:
    """一次团队任务最多登记几个子任务。"""
    return max(1, min(members * 2, TEAM_MAX_PER_RUN))


class DispatchDenied(Exception):
    """团队派遣不通过：kind 是给员工端看的错误码，message 是原因。"""

    def __init__(self, kind: str, message: str):
        super().__init__(message)
        self.kind = kind
        self.message = message


async def check_dispatch(session: AsyncSession, device: Device, team_id: str, expert_id: str) -> int:
    """团队成员派遣登记前的检查：团队模式开着、这台电脑看得到这个团队、专家是团队成员。

    看的是团队模式开关，不看「派遣专家」开关。通过时返回这次运行最多派几次；不通过抛 :class:`DispatchDenied`。
    """
    if not (await device_security.effective_for(session, device))["teams"]["value"]:
        raise DispatchDenied("teams_disabled", "IT 没有开启团队模式")
    team = await session.get(Team, team_id)
    if team is None or not visible(team, device):
        raise DispatchDenied("team_unavailable", "这个团队不存在、已解散或不在你的使用范围内")
    members = team.members or []
    if expert_id not in {m.get("expert_id") for m in members}:
        raise DispatchDenied("expert_unavailable", "这位专家不是这个团队的成员")
    return dispatch_limit(len(members))
