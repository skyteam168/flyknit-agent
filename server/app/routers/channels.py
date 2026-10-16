"""
通道（钉钉）的接口。

  /api/v1/client/channels…   员工端：看开没开、生成绑定码、看和解除自己的绑定、长轮询领消息、回结果
  /api/v1/admin/channels…    后台：钉钉应用凭证和开关、连接状态、绑定列表、消息记录
"""

from __future__ import annotations

from datetime import datetime

from fastapi import APIRouter, Depends, HTTPException, Query
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .. import crypto
from ..db import get_session
from ..deps import require_admin, require_device, require_owner
from ..models import AdminUser, ChannelBinding, ChannelMessage, Device
from ..services.channel_hub import get_config, hub, ready, save_config, secret_of

client_router = APIRouter(prefix="/api/v1/client/channels", tags=["channels"])
admin_router = APIRouter(prefix="/api/v1/admin/channels", tags=["channels"], dependencies=[Depends(require_admin)])

MAX_WAIT = 25
MAX_ANSWER = 20_000


class BindingOut(BaseModel):
    id: int
    channel: str
    external_name: str
    created_at: datetime
    last_used: datetime | None


class ClientChannelsOut(BaseModel):
    #: 后台开了钉钉通道并填好了凭证
    dingtalk: bool
    bindings: list[BindingOut]


class BindCodeOut(BaseModel):
    code: str
    expires_at: datetime


class ChannelTaskOut(BaseModel):
    id: int
    channel: str
    text: str
    sender: str
    #: 接着上次的对话聊；空表示新开一个
    conversation_id: str


class ChannelPollOut(BaseModel):
    messages: list[ChannelTaskOut]


class ChannelFinishIn(BaseModel):
    status: str = Field(pattern="^(succeeded|failed)$")
    answer: str = Field(default="", max_length=MAX_ANSWER)
    error: str = Field(default="", max_length=2000)
    conversation_id: str = Field(default="", max_length=64)


def _binding_out(b: ChannelBinding) -> BindingOut:
    return BindingOut(id=b.id, channel=b.channel, external_name=b.external_name, created_at=b.created_at, last_used=b.last_used)


# =====================================================================
# 员工端
# =====================================================================

@client_router.get("", response_model=ClientChannelsOut)
async def my_channels(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    cfg = await get_config(session)
    rows = (await session.scalars(select(ChannelBinding).where(ChannelBinding.device_id == device.id))).all()
    return ClientChannelsOut(dingtalk=ready(cfg), bindings=[_binding_out(b) for b in rows])


@client_router.post("/bind-code", response_model=BindCodeOut)
async def bind_code(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    if not ready(await get_config(session)):
        raise HTTPException(409, "管理员还没有开启钉钉通道")
    row = await hub.new_bind_code(session, device.id)
    return BindCodeOut(code=row.code, expires_at=row.expires_at)


@client_router.delete("/bindings/{binding_id}")
async def unbind(binding_id: int, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    row = await session.get(ChannelBinding, binding_id)
    if row is None or row.device_id != device.id:
        raise HTTPException(404, "没有这个绑定")
    await session.delete(row)
    await session.commit()
    return {"ok": True}


@client_router.get("/messages", response_model=ChannelPollOut)
async def poll_messages(
    wait: int = Query(default=0, ge=0, le=MAX_WAIT),
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    """领发给本机的消息。没有就最多挂 wait 秒，期间来了消息立刻返回。"""
    hub.mark_polling(device.id)
    rows = await hub.take_pending(session, device.id)
    if not rows and wait:
        await hub.wait(device.id, wait)
        hub.mark_polling(device.id)
        rows = await hub.take_pending(session, device.id)
    out = []
    for r in rows:
        binding = await session.get(ChannelBinding, r.binding_id) if r.binding_id else None
        out.append(ChannelTaskOut(id=r.id, channel=r.channel, text=r.text, sender=r.external_name,
                                  conversation_id=binding.conversation_id if binding else ""))
    return ChannelPollOut(messages=out)


@client_router.post("/messages/{message_id}/finish")
async def finish_message(
    message_id: int,
    data: ChannelFinishIn,
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    row = await session.get(ChannelMessage, message_id)
    if row is None or row.device_id != device.id:
        raise HTTPException(404, "没有这条消息")
    if row.status != "running":
        return {"ok": False}  # 已经超时作废或者回过了
    await hub.finish(session, row, data.status == "succeeded", data.answer, data.error, data.conversation_id)
    return {"ok": True}


# =====================================================================
# 后台
# =====================================================================

class DingTalkConfigOut(BaseModel):
    enabled: bool
    client_id: str
    #: 打码后的，原文不回显
    client_secret: str
    robot_code: str
    connected: bool
    connected_since: datetime | None
    last_error: str
    received: int
    last_message_at: datetime | None
    bindings: int


class DingTalkConfigIn(BaseModel):
    enabled: bool = False
    client_id: str = Field(default="", max_length=100)
    #: 留空表示不改
    client_secret: str = Field(default="", max_length=200)
    robot_code: str = Field(default="", max_length=100)


async def _config_out(session: AsyncSession) -> DingTalkConfigOut:
    cfg = await get_config(session)
    count = len((await session.scalars(select(ChannelBinding.id).where(ChannelBinding.channel == "dingtalk"))).all())
    st = hub.status
    return DingTalkConfigOut(
        enabled=bool(cfg["enabled"]), client_id=cfg["client_id"], client_secret=crypto.mask(secret_of(cfg)),
        robot_code=cfg.get("robot_code") or "", connected=st.connected, connected_since=st.since,
        last_error=st.last_error, received=st.received, last_message_at=st.last_message_at, bindings=count,
    )


@admin_router.get("/dingtalk", response_model=DingTalkConfigOut)
async def get_dingtalk(session: AsyncSession = Depends(get_session)):
    return await _config_out(session)


@admin_router.put("/dingtalk", response_model=DingTalkConfigOut)
async def put_dingtalk(
    data: DingTalkConfigIn,
    # 开了它，钉钉里发的话就能让员工电脑执行操作：和改安全策略同一档权限
    _owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    if data.enabled and not data.client_id.strip():
        raise HTTPException(422, "请填写钉钉应用的 Client ID（AppKey）")
    if data.enabled and not data.client_secret and not secret_of(await get_config(session)):
        raise HTTPException(422, "请填写钉钉应用的 Client Secret（AppSecret）")
    await save_config(session, data.enabled, data.client_id, data.client_secret or None, data.robot_code)
    return await _config_out(session)


class AdminBindingOut(BindingOut):
    device_id: int
    machine_name: str
    user_name: str


@admin_router.get("/bindings", response_model=list[AdminBindingOut])
async def list_bindings(session: AsyncSession = Depends(get_session)):
    rows = (await session.execute(
        select(ChannelBinding, Device).join(Device, Device.id == ChannelBinding.device_id).order_by(ChannelBinding.id.desc())
    )).all()
    return [AdminBindingOut(**_binding_out(b).model_dump(), device_id=d.id, machine_name=d.machine_name, user_name=d.user_name)
            for b, d in rows]


@admin_router.delete("/bindings/{binding_id}")
async def admin_unbind(binding_id: int, session: AsyncSession = Depends(get_session)):
    row = await session.get(ChannelBinding, binding_id)
    if row is None:
        raise HTTPException(404, "没有这个绑定")
    await session.delete(row)
    await session.commit()
    return {"ok": True}


class AdminMessageOut(BaseModel):
    id: int
    channel: str
    external_name: str
    machine_name: str
    text: str
    status: str
    answer: str
    error: str
    replied: bool
    created_at: datetime
    finished_at: datetime | None


@admin_router.get("/messages", response_model=list[AdminMessageOut])
async def list_messages(limit: int = Query(default=100, ge=1, le=500), session: AsyncSession = Depends(get_session)):
    rows = (await session.execute(
        select(ChannelMessage, Device.machine_name)
        .join(Device, Device.id == ChannelMessage.device_id, isouter=True)
        .order_by(ChannelMessage.id.desc())
        .limit(limit)
    )).all()
    return [AdminMessageOut(id=m.id, channel=m.channel, external_name=m.external_name, machine_name=name or "",
                            text=m.text, status=m.status, answer=m.answer, error=m.error, replied=m.replied,
                            created_at=m.created_at, finished_at=m.finished_at) for m, name in rows]
