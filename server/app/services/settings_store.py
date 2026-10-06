import copy

from sqlalchemy.ext.asyncio import AsyncSession

from ..default_policy import DEFAULT_POLICY
from ..models import Setting

POLICY_KEY = "policy"
SMB_KEY = "smb"


async def get_value(session: AsyncSession, key: str, default: dict) -> dict:
    row = await session.get(Setting, key)
    return copy.deepcopy(row.value) if row else copy.deepcopy(default)


async def set_value(session: AsyncSession, key: str, value: dict) -> None:
    row = await session.get(Setting, key)
    if row is None:
        session.add(Setting(key=key, value=value))
    else:
        row.value = value
    await session.commit()
    # 通知在线的员工端立刻来拉新配置（安全中心、命令策略等即时生效）
    from . import config_events

    config_events.bump()


async def get_policy(session: AsyncSession) -> dict:
    policy = await get_value(session, POLICY_KEY, DEFAULT_POLICY)
    # 管理员以前存过的策略里没有后来新增的字段（比如 allowed_domains），
    # 原样下发的话客户端会拿到一份空白名单，把所有外网都拦掉。缺的字段用默认值补上。
    for key, value in DEFAULT_POLICY.items():
        policy.setdefault(key, copy.deepcopy(value))
    return policy


SECURITY_KEY = "security"


async def get_security(session: AsyncSession) -> dict:
    """全厂的安全设置默认值与锁状态。"""
    return await get_value(session, SECURITY_KEY, {"values": {}, "locks": {}})


async def set_security(session: AsyncSession, values: dict, locks: dict) -> dict:
    from . import security_settings

    merged = {
        "values": security_settings.sanitize(values),
        "locks": security_settings.sanitize_locks(locks),
    }
    await set_value(session, SECURITY_KEY, merged)
    return merged
