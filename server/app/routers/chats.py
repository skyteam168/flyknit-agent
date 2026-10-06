"""管理端查看聊天记录。

两条规矩贯穿这个文件：
  列表只给元数据（谁、哪台机器、几轮、什么时候），不带正文；
  一旦取正文，就记一条谁看了什么。没有这条记录，后台不该拿到聊天内容。
"""

import logging
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..crypto import hash_token, hash_password, new_token, verify_password
from ..db import get_session
from ..deps import require_admin, require_admin_user, require_chat_reader
from ..models import AdminAccess, AdminSession, AdminUser, ChatRecord
from ..schemas import (
    AdminLoginIn,
    AdminLoginOut,
    AdminUserIn,
    AdminUserOut,
    ChangePasswordIn,
    ChatConversationOut,
    ChatRecordOut,
)

log = logging.getLogger("flyknit.chats")
router = APIRouter(prefix="/api/v1/admin", tags=["chats"])

SESSION_HOURS = 12


# ---------- 账号 ----------

@router.post("/users", response_model=AdminUserOut, status_code=201, dependencies=[Depends(require_admin)])
async def create_user(data: AdminUserIn, session: AsyncSession = Depends(get_session)):
    """建管理员账号。用共享的 admin_token 建，之后这个人用自己的账号登录。"""
    if await session.scalar(select(AdminUser).where(AdminUser.username == data.username)):
        raise HTTPException(status.HTTP_409_CONFLICT, "该用户名已存在")
    user = AdminUser(
        username=data.username,
        display_name=data.display_name or data.username,
        password_hash=hash_password(data.password),
        can_read_chats=data.can_read_chats,
        must_change_password=True,
    )
    session.add(user)
    await session.commit()
    return user


@router.get("/users", response_model=list[AdminUserOut], dependencies=[Depends(require_admin)])
async def list_users(session: AsyncSession = Depends(get_session)):
    return list(await session.scalars(select(AdminUser).order_by(AdminUser.username)))


@router.post("/login", response_model=AdminLoginOut)
async def login(data: AdminLoginIn, session: AsyncSession = Depends(get_session)):
    user = await session.scalar(select(AdminUser).where(AdminUser.username == data.username))
    # 用户名不存在时也走一次哈希校验，否则响应快慢会暴露哪些用户名是真的
    stored = user.password_hash if user else hash_password("placeholder")
    ok = verify_password(data.password, stored)
    if user is None or not ok:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "用户名或密码不正确")
    if user.disabled:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "该账号已被停用")

    token = new_token()
    session.add(
        AdminSession(
            user_id=user.id,
            token_hash=hash_token(token),
            expires_at=datetime.now(timezone.utc) + timedelta(hours=SESSION_HOURS),
        )
    )
    user.last_login = datetime.now(timezone.utc)
    await session.commit()
    log.info("管理员登录 user=%s", user.username)
    return AdminLoginOut(
        token=token,
        display_name=user.display_name,
        can_read_chats=user.can_read_chats,
        must_change_password=user.must_change_password,
    )


@router.post("/password", status_code=204)
async def change_password(
    data: ChangePasswordIn,
    user: AdminUser = Depends(require_admin_user),
    session: AsyncSession = Depends(get_session),
):
    if not verify_password(data.old_password, user.password_hash):
        raise HTTPException(status.HTTP_403_FORBIDDEN, "原密码不正确")
    if len(data.new_password) < 8:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "新密码至少 8 位")
    user.password_hash = hash_password(data.new_password)
    user.must_change_password = False
    # 改密后别的地方的登录状态一并失效
    for row in await session.scalars(select(AdminSession).where(AdminSession.user_id == user.id)):
        await session.delete(row)
    await session.commit()


# ---------- 聊天记录 ----------

@router.get("/chats", response_model=list[ChatConversationOut])
async def list_conversations(
    device_id: int | None = None,
    days: int = Query(default=7, ge=1, le=365),
    limit: int = Query(default=50, ge=1, le=200),
    user: AdminUser = Depends(require_admin_user),
    session: AsyncSession = Depends(get_session),
):
    """
    会话列表。**只有元数据，没有正文**——先让人看见有什么，再决定要不要点开，
    而不是一打开后台就把所有人的对话摊在眼前。
    """
    since = datetime.now(timezone.utc) - timedelta(days=days)
    q = (
        select(
            ChatRecord.conversation_id,
            func.max(ChatRecord.machine_name).label("machine_name"),
            func.max(ChatRecord.user_name).label("user_name"),
            func.max(ChatRecord.scene).label("scene"),
            func.count(ChatRecord.id).label("turns"),
            func.min(ChatRecord.created_at).label("started_at"),
            func.max(ChatRecord.created_at).label("last_at"),
        )
        .where(ChatRecord.created_at >= since)
        .group_by(ChatRecord.conversation_id)
        .order_by(func.max(ChatRecord.created_at).desc())
        .limit(limit)
    )
    if device_id is not None:
        q = q.where(ChatRecord.device_id == device_id)
    return [ChatConversationOut.model_validate(row, from_attributes=True) for row in await session.execute(q)]


@router.get("/chats/{conversation_id}", response_model=list[ChatRecordOut])
async def read_conversation(
    conversation_id: str,
    user: AdminUser = Depends(require_chat_reader),
    session: AsyncSession = Depends(get_session),
):
    """取某次对话的正文。这一步会留痕。"""
    rows = list(
        await session.scalars(
            select(ChatRecord)
            .where(ChatRecord.conversation_id == conversation_id)
            .order_by(ChatRecord.created_at)
        )
    )
    if not rows:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这次对话的记录")

    session.add(
        AdminAccess(
            user_id=user.id,
            username=user.username,
            action="read_chat",
            target=conversation_id,
            detail=f"{rows[0].machine_name} / {rows[0].user_name}，{len(rows)} 轮",
        )
    )
    await session.commit()
    log.info("管理员 %s 查看了会话 %s（%s）", user.username, conversation_id, rows[0].machine_name)
    return rows


@router.get("/chat-access", dependencies=[Depends(require_admin)])
async def list_access(
    limit: int = Query(default=100, ge=1, le=500),
    session: AsyncSession = Depends(get_session),
):
    """谁看了谁的对话。这张表本身要能被查，否则留痕没有意义。"""
    rows = await session.scalars(select(AdminAccess).order_by(AdminAccess.created_at.desc()).limit(limit))
    return [
        {
            "username": r.username,
            "action": r.action,
            "target": r.target,
            "detail": r.detail,
            "created_at": r.created_at,
        }
        for r in rows
    ]
