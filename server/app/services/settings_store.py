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


async def get_policy(session: AsyncSession) -> dict:
    return await get_value(session, POLICY_KEY, DEFAULT_POLICY)
