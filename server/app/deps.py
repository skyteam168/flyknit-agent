import hmac
from datetime import datetime, timezone

from fastapi import Depends, Header, HTTPException, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .config import get_settings
from .crypto import hash_token
from .db import get_session
from .models import AdminSession, AdminUser, Device, MachineAgent


def _bearer(authorization: str | None) -> str:
    if not authorization or not authorization.lower().startswith("bearer "):
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "缺少认证信息")
    return authorization[7:].strip()


async def _session_user(session: AsyncSession, token: str) -> AdminUser | None:
    row = await session.scalar(select(AdminSession).where(AdminSession.token_hash == hash_token(token)))
    if row is None or row.expires_at.replace(tzinfo=timezone.utc) < datetime.now(timezone.utc):
        return None
    user = await session.get(AdminUser, row.user_id)
    return None if user is None or user.disabled else user


async def require_admin(
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
) -> None:
    """
    配置类接口。收两种身份：共享的 admin_token（命令行脚本用），
    或管理后台网页上具名账号登录后的会话令牌。
    """
    token = _bearer(authorization)
    if hmac.compare_digest(token, get_settings().admin_token):
        return
    if await _session_user(session, token) is None:
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


async def require_admin_user(
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
) -> AdminUser:
    """
    具名管理员。配置类接口继续收共享的 admin_token（脚本要用），
    但看聊天正文必须是某个人——「谁看了谁的对话」要答得上来。
    """
    token = _bearer(authorization)
    row = await session.scalar(select(AdminSession).where(AdminSession.token_hash == hash_token(token)))
    if row is None:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "请先登录管理端")
    if row.expires_at.replace(tzinfo=timezone.utc) < datetime.now(timezone.utc):
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "登录已过期，请重新登录")
    user = await session.get(AdminUser, row.user_id)
    if user is None or user.disabled:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "该管理员账号已被停用")
    return user


async def require_agent(
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
) -> MachineAgent:
    token = _bearer(authorization)
    agent = await session.scalar(select(MachineAgent).where(MachineAgent.token_hash == hash_token(token)))
    if agent is None:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "运维代理未注册或令牌无效")
    if agent.disabled:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "运维代理已被管理员停用")
    agent.last_seen = datetime.now(timezone.utc)
    await session.commit()
    return agent


async def require_dispatcher(user: AdminUser = Depends(require_admin_user)) -> AdminUser:
    """给员工电脑下发任务要单独授权；共享的 admin_token 不行，必须是具名账号。"""
    if not user.can_dispatch:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "该账号没有下发运维任务的权限")
    if user.must_change_password:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "请先修改初始密码")
    return user


async def require_chat_reader(user: AdminUser = Depends(require_admin_user)) -> AdminUser:
    """看聊天正文要单独授权，不是当了管理员就自带。"""
    if not user.can_read_chats:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "该账号没有查看聊天内容的权限")
    if user.must_change_password:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "请先修改初始密码")
    return user
