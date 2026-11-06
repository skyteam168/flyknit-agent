"""插件的下发范围：这台电脑看得到哪些插件带来的东西。

技能、专家、连接器、快捷指令、工具规则的员工端接口都过一遍这里。范围在服务端算，
员工端拿不到范围外插件的任何内容（包括技能包下载），不靠员工端自觉。
"""

from __future__ import annotations

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import Device, Plugin


def _dept(text: str) -> str:
    return (text or "").strip().casefold()


def in_scope(plugin: Plugin, device: Device | None) -> bool:
    """插件启用、且这台电脑的部门在范围里（范围为空 = 全公司）。"""
    if not plugin.enabled:
        return False
    depts = {_dept(d) for d in (plugin.departments or []) if _dept(d)}
    if not depts:
        return True
    return device is not None and _dept(device.department) in depts


async def visible(session: AsyncSession, device: Device | None) -> list[Plugin]:
    rows = (await session.scalars(select(Plugin).order_by(Plugin.name))).all()
    return [p for p in rows if in_scope(p, device)]


async def visible_ids(session: AsyncSession, device: Device | None) -> set[str]:
    """这台电脑看得到的插件 id。来源列（plugin）不在这里面的行都要滤掉（插件记录没了的也一样）。"""
    return {p.id for p in await visible(session, device)}


def allowed(row_plugin: str | None, visible: set[str]) -> bool:
    return not row_plugin or row_plugin in visible
