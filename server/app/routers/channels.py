"""
通道（钉钉、企业微信）的接口。

  /api/v1/client/channels…   员工端：看开没开、生成绑定码、看和解除自己的绑定、长轮询领消息、回结果
  /api/v1/admin/channels…    后台：钉钉 / 企业微信的凭证和开关、连接状态、绑定列表、消息记录
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
from ..services import mobile_hub
from ..services.channel_hub import (
    get_config,
    get_wecom_config,
    hub,
    ready,
    save_config,
    save_wecom_config,
    secret_of,
    wecom_ready,
    wecom_secret,
)

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
    #: 后台开了企业微信通道
    wecom: bool = False
    #: 后台开了手机端（手机绑定也在 bindings 里，channel = mobile）
    mobile: bool = False
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
    wc = await get_wecom_config(session)
    rows = (await session.scalars(select(ChannelBinding).where(ChannelBinding.device_id == device.id))).all()
    mobile = bool((await mobile_hub.get_config(session))["enabled"])
    return ClientChannelsOut(dingtalk=ready(cfg), wecom=wecom_ready(wc), mobile=mobile, bindings=[_binding_out(b) for b in rows])


@client_router.post("/bind-code", response_model=BindCodeOut)
async def bind_code(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    if not ready(await get_config(session)) and not wecom_ready(await get_wecom_config(session)):
        raise HTTPException(409, "管理员还没有开启钉钉或企业微信通道")
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
        if r.channel == mobile_hub.CHANNEL:
            await mobile_hub.hub.task_changed(r)  # 手机上显示「电脑已开始处理」
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


class TaskEventIn(BaseModel):
    #: step（正在做哪一步）/ tool（调用了什么工具）/ text（阶段性的文字）
    kind: str = Field(default="step", max_length=20)
    text: str = Field(default="", max_length=2000)


async def _running(session: AsyncSession, message_id: int, device: Device) -> ChannelMessage:
    row = await session.get(ChannelMessage, message_id)
    if row is None or row.device_id != device.id:
        raise HTTPException(404, "没有这条消息")
    return row


@client_router.post("/messages/{message_id}/events")
async def task_event(message_id: int, data: TaskEventIn, device: Device = Depends(require_device),
                     session: AsyncSession = Depends(get_session)):
    """执行进度：手机 App 实时显示；钉钉、企业微信不显示过程，直接忽略。"""
    row = await _running(session, message_id, device)
    if row.status == "running" and row.channel == mobile_hub.CHANNEL:
        await mobile_hub.hub.task_event(row, data.kind, data.text)
    return {"ok": True}


@client_router.get("/messages/{message_id}/inputs")
async def task_inputs(message_id: int, wait: int = Query(default=25, ge=0, le=30), device: Device = Depends(require_device),
                      session: AsyncSession = Depends(get_session)):
    """
    电脑执行手机发来的事时，顺便长轮询手机上补充的话（插话）和打断请求。
    done=true 表示这件事已经结束，不用再来取了。
    """
    row = await _running(session, message_id, device)
    if row.channel != mobile_hub.CHANNEL or row.status != "running":
        return {"inputs": [], "stop": False, "done": True}
    texts, stop = await mobile_hub.hub.take_inputs(row.id, float(wait))
    for t in texts:
        await mobile_hub.hub.task_event(row, "input", "已交给电脑：" + (t if len(t) <= 40 else t[:40] + "…"))
    return {"inputs": texts, "stop": stop, "done": False}


class ApprovalIn(BaseModel):
    summary: str = Field(max_length=500)
    detail: str = Field(default="", max_length=2000)
    risk: str = Field(default="confirm", max_length=20)


@client_router.post("/messages/{message_id}/approvals")
async def request_approval(message_id: int, data: ApprovalIn, device: Device = Depends(require_device),
                           session: AsyncSession = Depends(get_session)):
    """
    远程执行到了需要确认的操作。手机发起的、后台允许手机批准的，交给那部手机决定；
    其余（钉钉、企业微信，或者后台没开）返回 enabled=false，员工端照旧拒绝并在结果里列出来。
    """
    row = await _running(session, message_id, device)
    cfg = await mobile_hub.get_config(session)
    if row.channel != mobile_hub.CHANNEL or row.status != "running" or not (cfg["enabled"] and cfg["allow_approval"]):
        return {"enabled": False}
    a = mobile_hub.hub.request_approval(row, data.summary, data.detail, data.risk, int(cfg["approval_timeout_min"]))
    if a is None:
        return {"enabled": False}
    # delivered=false：手机此刻没连着（App 在后台被系统掐断了连接之类）。手机下次打开 App 时照样能看到，
    # 电脑那边据此提示员工也可以直接在电脑上确认
    delivered = await mobile_hub.hub.send(a.mobile_id, {"type": "approval.request", "approval": a.out()})
    return {"enabled": True, "id": a.id, "timeout_sec": int(cfg["approval_timeout_min"]) * 60, "delivered": delivered}


@client_router.delete("/approvals/{approval_id}")
async def cancel_approval(approval_id: str, device: Device = Depends(require_device)):
    """
    电脑上已经处理了这一步（员工在电脑上点了确认 / 拒绝，或者任务停了）：手机上的确认卡片收起来。
    已经回答过的不变。
    """
    a = mobile_hub.hub.get_approval(approval_id)
    if a is None or a.device_id != device.id:
        raise HTTPException(404, "没有这个审批")
    await mobile_hub.hub.cancel(a)
    return {"status": a.status}


@client_router.get("/approvals/{approval_id}")
async def approval_status(approval_id: str, wait: int = Query(default=0, ge=0, le=MAX_WAIT),
                          device: Device = Depends(require_device)):
    """等手机上的回答：最多挂 wait 秒。超时没答当拒绝（expired）。"""
    a = mobile_hub.hub.get_approval(approval_id)
    if a is None or a.device_id != device.id:
        raise HTTPException(404, "没有这个审批")
    st = await mobile_hub.hub.wait_approval(a, wait) if wait else a.status
    return {"status": st}


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
    st = hub.status("dingtalk")
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


# ---------- 企业微信 ----------

class WeComConfigOut(BaseModel):
    enabled: bool
    bot_id: str
    #: 打码后的，原文不回显
    secret: str
    connected: bool
    connected_since: datetime | None
    last_error: str
    received: int
    last_message_at: datetime | None
    bindings: int


class WeComConfigIn(BaseModel):
    enabled: bool = False
    bot_id: str = Field(default="", max_length=100)
    #: 留空表示不改
    secret: str = Field(default="", max_length=200)


async def _wecom_out(session: AsyncSession) -> WeComConfigOut:
    cfg = await get_wecom_config(session)
    count = len((await session.scalars(select(ChannelBinding.id).where(ChannelBinding.channel == "wecom"))).all())
    st = hub.status("wecom")
    return WeComConfigOut(
        enabled=bool(cfg["enabled"]), bot_id=cfg["bot_id"], secret=crypto.mask(wecom_secret(cfg)),
        connected=st.connected, connected_since=st.since, last_error=st.last_error, received=st.received,
        last_message_at=st.last_message_at, bindings=count,
    )


@admin_router.get("/wecom", response_model=WeComConfigOut)
async def get_wecom(session: AsyncSession = Depends(get_session)):
    return await _wecom_out(session)


@admin_router.put("/wecom", response_model=WeComConfigOut)
async def put_wecom(
    data: WeComConfigIn,
    # 和钉钉一样：开了它，企业微信里发的话就能让员工电脑执行操作
    _owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    if data.enabled and not data.bot_id.strip():
        raise HTTPException(422, "请填写企业微信智能机器人的 Bot ID")
    if data.enabled and not data.secret and not wecom_secret(await get_wecom_config(session)):
        raise HTTPException(422, "请填写企业微信智能机器人的 Secret")
    await save_wecom_config(session, data.enabled, data.bot_id, data.secret or None)
    return await _wecom_out(session)
