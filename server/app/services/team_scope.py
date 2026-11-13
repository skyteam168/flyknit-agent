"""团队的可见范围（docs/team-mode.md 第 4 节）：员工端列表、派遣登记都用这一处判断。"""

from __future__ import annotations

from ..models import Device, Team
from . import item_scope
from .department_store import key


def visible(team: Team, device: Device) -> bool:
    """这台电脑看不看得到这个团队：解散的都看不到；个人团队只有建的人；部门团队只有同部门；IT 团队按使用范围。"""
    if team.dissolved_at is not None:
        return False
    if team.source == "personal":
        return team.owner_device_id == device.id
    if team.source == "department":
        return bool(team.department) and key(team.department) == key(device.department)
    return item_scope.in_scope(team, device)
