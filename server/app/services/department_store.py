"""部门清单：设备的部门、各类条目的使用范围都从这里选。

部门在设备上和范围里存的是名字（和插件的 departments 一致），所以：
- 比较时忽略大小写和首尾空格；
- 改名时把设备、插件、技能 / 专家 / 连接器范围里的名字一起改；
- 删除前检查有没有人在用：范围里删掉最后一个部门会让范围变空，而空 = 全公司，等于悄悄放大了权限。
"""

from __future__ import annotations

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import Department, Device, Expert, McpVendor, Plugin, SkillPackage

#: 有使用范围的条目：(类型标识, 模型, 主键字段, 后台显示名)
SCOPED = (
    ("skill", SkillPackage, "name", "技能"),
    ("expert", Expert, "id", "专家"),
    ("mcp", McpVendor, "id", "连接器"),
)


def key(name: str | None) -> str:
    return (name or "").strip().casefold()


def clean(name: str | None) -> str:
    return " ".join((name or "").split())[:200]


async def all_departments(session: AsyncSession) -> list[Department]:
    return list((await session.scalars(select(Department).order_by(Department.sort_order, Department.name))).all())


async def ensure_from_devices(session: AsyncSession) -> int:
    """设备上已经填了、清单里还没有的部门补进清单（升级上来第一次打开、或旧接口写进来的）。返回补了几个。"""
    known = {key(d.name) for d in await all_departments(session)}
    added = 0
    for (name,) in (await session.execute(select(Device.department).distinct())).all():
        cleaned = clean(name)
        if cleaned and key(cleaned) not in known:
            session.add(Department(name=cleaned))
            known.add(key(cleaned))
            added += 1
    if added:
        await session.commit()
    return added


async def register(session: AsyncSession, names: list[str]) -> list[str]:
    """
    插件的下发部门：清单里有的换成清单里的写法，没有的加进清单（管理员给插件选部门时就是在定义部门）。
    返回规整后的名字。调用方提交。
    """
    out: list[str] = []
    for name in names:
        cleaned = clean(name)
        if not cleaned:
            continue
        dept = await find(session, cleaned)
        if dept is None:
            dept = Department(name=cleaned)
            session.add(dept)
            await session.flush()
        if dept.name not in out:
            out.append(dept.name)
    return out


async def find(session: AsyncSession, name: str | None) -> Department | None:
    wanted = key(name)
    if not wanted:
        return None
    for d in await all_departments(session):
        if key(d.name) == wanted:
            return d
    return None


def _replace(names: list | None, old: str, new: str | None) -> tuple[list[str], bool]:
    """范围列表里把 old 换成 new（new 为 None 表示删掉）。返回新列表和有没有变化。"""
    out: list[str] = []
    changed = False
    for n in names or []:
        if key(n) == key(old):
            changed = True
            if new is not None and key(new) not in {key(x) for x in out}:
                out.append(new)
        elif key(n) not in {key(x) for x in out}:
            out.append(n)
    return out, changed


async def rename(session: AsyncSession, dept: Department, new_name: str) -> None:
    """改名：设备、插件下发范围、技能 / 专家 / 连接器的使用范围里的旧名字一起换掉。调用方提交。"""
    old = dept.name
    dept.name = new_name
    for device in (await session.scalars(select(Device))).all():
        if key(device.department) == key(old):
            device.department = new_name
    for plugin in (await session.scalars(select(Plugin))).all():
        updated, changed = _replace(plugin.departments, old, new_name)
        if changed:
            plugin.departments = updated
    for _, model, _, _ in SCOPED:
        for row in (await session.scalars(select(model))).all():
            updated, changed = _replace(row.scope_departments, old, new_name)
            if changed:
                row.scope_departments = updated


async def usage(session: AsyncSession, dept: Department) -> dict[str, list[str]]:
    """哪些地方还在用这个部门：设备、插件、各类条目的使用范围。删除前给管理员看。"""
    used: dict[str, list[str]] = {"devices": [], "plugins": [], "skill": [], "expert": [], "mcp": []}
    for device in (await session.scalars(select(Device))).all():
        if key(device.department) == key(dept.name):
            used["devices"].append(device.machine_name)
    for plugin in (await session.scalars(select(Plugin))).all():
        if any(key(n) == key(dept.name) for n in plugin.departments or []):
            used["plugins"].append(plugin.name)
    for kind, model, pk, _ in SCOPED:
        for row in (await session.scalars(select(model))).all():
            if any(key(n) == key(dept.name) for n in row.scope_departments or []):
                used[kind].append(str(getattr(row, pk)))
    return used


async def device_counts(session: AsyncSession) -> dict[str, int]:
    counts: dict[str, int] = {}
    for (name,) in (await session.execute(select(Device.department).where(Device.disabled.is_(False)))).all():
        if key(name):
            counts[key(name)] = counts.get(key(name), 0) + 1
    return counts
