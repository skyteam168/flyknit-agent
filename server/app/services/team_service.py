"""团队模式在服务端的规则（docs/team-mode.md）：可见范围、派遣登记前的检查、派遣次数。

团队作为一个功能模块接在派遣上（docs/extension-architecture.md）：子任务路由只在请求带了 team_id 时调用
:func:`check_dispatch`，团队的开关、范围、成员资格、次数规则都在这里，不散在路由里。
"""

from __future__ import annotations

import re

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


# ---------- 团队经验（团队积累）----------

#: 一个团队最多存几条经验（满了要先清理；不自动删，免得把有用的挤掉）
MAX_NOTES = 200

#: 开工时给队长的经验条数（最近的在前）
NOTES_FOR_LEADER = 20

# 和员工端 Flyknit.Core.Memory.SensitiveScanner 同一套：一旦泄露就能直接用的，整条拒收。
# 员工端写之前已经查过一遍；团队经验是多人共享的，服务端再查一遍，不信客户端
_SECRETS: list[tuple[str, re.Pattern[str]]] = [
    ("私钥", re.compile(r"BEGIN [A-Z ]*PRIVATE KEY")),
    ("密码", re.compile(r"(密码|口令|password|passwd|pwd)\s*(?:是|为|[:：=])\s*[\"'“]?[A-Za-z0-9!@#$%^&*._+\-~]{4,}", re.I)),
    ("验证码", re.compile(r"(验证码|动态码|OTP)\s*(?:是|为|[:：=])?\s*\d{4,8}\b", re.I)),
    ("API 密钥", re.compile(r"\b(?:sk|pk|rk)-[A-Za-z0-9_\-]{16,}|\bgh[pousr]_[A-Za-z0-9]{20,}|\bAKIA[0-9A-Z]{16}\b"
                          r"|\bxox[abpr]-[A-Za-z0-9\-]{10,}|\bAIza[0-9A-Za-z_\-]{30,}")),
    ("令牌", re.compile(r"\bBearer\s+[A-Za-z0-9\-._~+/]{20,}=*|\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}")),
    ("密钥", re.compile(r"(api[_\- ]?key|secret|access[_\- ]?key|client[_\- ]?secret|token|密钥)\s*(?:是|为|[:：=])\s*[\"'“]?[A-Za-z0-9_\-./+=]{16,}", re.I)),
]
_MOBILE = re.compile(r"(?<!\d)(?:\+?86[ -]?)?1[3-9]\d[ -]?\d{4}[ -]?\d{4}(?!\d)")
_ID_CARD = re.compile(r"(?<![0-9A-Za-z])\d{17}[\dXx](?![0-9A-Za-z])")


def secrets_in(text: str) -> list[str]:
    """文字里有没有密码、密钥、令牌这类（只看值，不看词）。"""
    return [name for name, pattern in _SECRETS if pattern.search(text)]


def mask_personal(text: str) -> str:
    """手机号、身份证号打码（保留「有这么个号」，不保留号码本身）。"""
    text = _ID_CARD.sub(lambda m: m.group(0)[:4] + "*" * 10 + m.group(0)[-4:], text)
    return _MOBILE.sub(lambda m: re.sub(r"\d(?=(?:\D*\d){4})", "*", m.group(0)), text)
