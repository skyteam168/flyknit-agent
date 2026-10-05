import hmac
from datetime import datetime, timezone

from fastapi import Depends, Header, HTTPException, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .config import get_settings
from .crypto import hash_token
from .db import get_session
from .models import Device


def _bearer(authorization: str | None) -> str:
    if not authorization or not authorization.lower().startswith("bearer "):
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "缺少认证信息")
    return authorization[7:].strip()


async def require_admin(authorization: str | None = Header(default=None)) -> None:
    token = _bearer(authorization)
    if not hmac.compare_digest(token, get_settings().admin_token):
        raise HTTPException(status.HTTP_403_FORBIDDEN, "管理员令牌无效")


async def require_device(
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
) -> Device:
    token = _bearer(authorization)
    device = await session.scalar(select(Device).where(Device.token_hash == hash_token(token)))
    if device is None:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "设备未注册或令牌无效")
    if device.disabled:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "设备已被管理员禁用")
    device.last_seen = datetime.now(timezone.utc)
    await session.commit()
    return device
