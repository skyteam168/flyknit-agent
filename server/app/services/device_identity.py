"""
一台电脑上的一个员工，在后台只占一行设备记录。

员工退出登录再登录、升级或重装员工端，都会重新注册拿新令牌。以前每次注册都新建一行，
后台就出现同一台电脑好几行（旧的「已退出登录」），今天用掉的 Token 也算在旧行上、新行显示 0。

现在按「系统标识（machine_guid）+ Windows 账号」认人：
- 注册时找到原来那一行就沿用它，只换令牌（用量、额度、备注、单独的安全设置都还在）；
- 已经分裂出来的几行，在新行第一次上报系统标识时并到新行上（用量相加、聊天和审计记录改挂过来），旧行删掉。

同一台电脑换了个员工登录，账号不同，还是各算各的。
"""

from __future__ import annotations

import logging

from sqlalchemy import delete, desc, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import AuditLog, ChatRecord, Device, DevicePolicy, Feedback, InstructionRun, UsageDaily

log = logging.getLogger("flyknit.audit")


def account_key(user_name: str) -> str:
    """注册时报的是 DOMAIN\\user，心跳报的是 user：只比账号本身，不分大小写。"""
    return (user_name or "").rsplit("\\", 1)[-1].strip().lower()


async def _same_person(session: AsyncSession, machine_guid: str, user_name: str) -> list[Device]:
    guid = (machine_guid or "").strip().lower()
    key = account_key(user_name)
    if not guid or not key:
        return []
    rows = await session.scalars(
        select(Device).where(Device.machine_guid == guid).order_by(desc(Device.last_seen), desc(Device.id))
    )
    return [d for d in rows if account_key(d.user_name) == key]


async def find_previous(session: AsyncSession, machine_guid: str, user_name: str) -> Device | None:
    """这台电脑上这个员工原来那一行（最近在线的那行），没有就 None。"""
    found = await _same_person(session, machine_guid, user_name)
    return found[0] if found else None


async def merge_duplicates(session: AsyncSession, keep: Device) -> int:
    """把同一台电脑、同一个员工的其它行并到 keep 上。返回并掉了几行。调用方负责 commit。"""
    others = [d for d in await _same_person(session, keep.machine_guid, keep.user_name) if d.id != keep.id]
    for old in others:
        await _merge_into(session, old, keep)
    if others:
        log.info("合并重复设备 keep=%s merged=%s machine=%s user=%s",
                 keep.id, [d.id for d in others], keep.machine_name, keep.user_name)
    return len(others)


async def _merge_into(session: AsyncSession, old: Device, keep: Device) -> None:
    # 用量：同一天同一场景两边都有就相加
    for row in list(await session.scalars(select(UsageDaily).where(UsageDaily.device_id == old.id))):
        same = await session.scalar(
            select(UsageDaily).where(
                UsageDaily.device_id == keep.id, UsageDaily.day == row.day, UsageDaily.scene == row.scene
            )
        )
        if same is None:
            row.device_id = keep.id
        else:
            same.prompt_tokens += row.prompt_tokens
            same.completion_tokens += row.completion_tokens
            same.requests += row.requests
            await session.delete(row)
    await session.flush()

    for model in (AuditLog, ChatRecord, Feedback, InstructionRun):
        await session.execute(update(model).where(model.device_id == old.id).values(device_id=keep.id))

    # 单独的安全设置：新行没有就把旧行的搬过来
    old_policy = await session.get(DevicePolicy, old.id)
    if old_policy is not None:
        if await session.get(DevicePolicy, keep.id) is None:
            session.add(DevicePolicy(device_id=keep.id, overrides=dict(old_policy.overrides or {}),
                                     locks=dict(old_policy.locks or {}), note=old_policy.note))
        await session.delete(old_policy)

    # 后台给旧行填的信息带过来；旧行被停用过的，合并后也是停用（不能靠重新登录绕开）
    keep.owner = keep.owner or old.owner
    keep.department = keep.department or old.department
    keep.note = keep.note or old.note
    if keep.daily_tokens is None:
        keep.daily_tokens = old.daily_tokens
    keep.disabled = keep.disabled or old.disabled
    if old.created_at and (keep.created_at is None or old.created_at < keep.created_at):
        keep.created_at = old.created_at
    await session.flush()
    await session.delete(old)
    await session.flush()


async def delete_device(session: AsyncSession, device: Device) -> None:
    """后台删除一台设备：用量、指令执行记录、单独设置随它删掉；聊天、审计、反馈记录留着（记录里有电脑名和账号）。"""
    for model in (AuditLog, ChatRecord, Feedback):
        await session.execute(update(model).where(model.device_id == device.id).values(device_id=None))
    for model in (UsageDaily, InstructionRun, DevicePolicy):
        await session.execute(delete(model).where(model.device_id == device.id))
    await session.delete(device)
