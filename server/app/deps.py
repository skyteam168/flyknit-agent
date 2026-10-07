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


def _is_shared_token(token: str) -> bool:
    """共享的 admin_token。命令行脚本和初次部署用它，它是这套权限的「破窗」入口。"""
    return hmac.compare_digest(token.encode("utf-8"), get_settings().admin_token.encode("utf-8"))


def _check_password_changed(user: AdminUser) -> AdminUser:
    """
    初始密码是建号的人定的，所以在改掉之前，这个账号说不清是谁在用——
    审计也就无从谈起。除了「改密码」本身，别的都先挡住。
    """
    if user.must_change_password:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "请先修改初始密码")
    return user


async def require_admin(
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
) -> None:
    """
    配置类接口。收两种身份：共享的 admin_token（命令行脚本用），
    或管理后台网页上具名账号登录后的会话令牌。

    注意这一档**只代表「是管理端的人」**，不代表任何具体权限。改账号、改安全
    策略这类动作要用 require_owner；看聊天正文、下发任务各有自己的闸门。
    """
    token = _bearer(authorization)
    if _is_shared_token(token):
        return
    user = await _session_user(session, token)
    if user is None:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "管理员令牌无效")
    _check_password_changed(user)


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


async def require_active_user(user: AdminUser = Depends(require_admin_user)) -> AdminUser:
    """具名账号，且已经改过初始密码。除了改密码本身，具名接口都从这一档往上走。"""
    return _check_password_changed(user)


async def require_dispatcher(user: AdminUser = Depends(require_active_user)) -> AdminUser:
    """给员工电脑下发任务要单独授权；共享的 admin_token 不行，必须是具名账号。"""
    if not user.can_dispatch:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "该账号没有下发运维任务的权限")
    return user


async def require_chat_reader(user: AdminUser = Depends(require_active_user)) -> AdminUser:
    """看聊天正文要单独授权，不是当了管理员就自带。"""
    if not user.can_read_chats:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "该账号没有查看聊天内容的权限")
    return user


async def chat_reader_or_none(
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
) -> AdminUser | None:
    """
    能看聊天内容的那个人，看不了就是 None。

    给那些「大部分字段谁都能看、少数字段是聊天内容」的接口用——审计记录就是这样：
    什么时候、哪台机器、用了什么工具、判定如何，这些是管控数据；而工具参数里是
    员工让 AI 读写的文件内容，那是聊天内容的一部分，得按正文的规矩来。

    共享 admin_token 在这里一律算看不了：它答不出「是谁看的」。
    """
    if not authorization or not authorization.lower().startswith("bearer "):
        return None
    token = authorization[7:].strip()
    if _is_shared_token(token):
        return None
    user = await _session_user(session, token)
    if user is None or user.must_change_password or not user.can_read_chats:
        return None
    return user


async def require_owner(
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
) -> AdminUser | None:
    """
    最高一档：改账号和改安全策略。

    这两件事必须比「下发任务」更难，因为它们能生出下发权限来——给某台机器解掉
    工作区隔离，比给那台机器发一条指令严重得多。

    返回 None 表示用的是共享的 admin_token（部署和救急用）。调用方据此判断
    「操作者是不是本人」，拿不到人就不做自我保护那几条检查。
    """
    token = _bearer(authorization)
    if _is_shared_token(token):
        return None
    user = await _session_user(session, token)
    if user is None:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "管理员令牌无效")
    _check_password_changed(user)
    if not user.is_owner:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "只有超级管理员能做这个操作")
    return user
