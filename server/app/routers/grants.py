"""任务授权记录：远程 / 定时 / 事件任务开始前，员工端报一份「谁让做、做到哪」。

- 只插入：同一个 id 再报一次直接返回已有的那份（重试安全），内容不会被改；没有修改、删除接口。
- 通道（手机、钉钉、企业微信）和指令中心发来的任务：发起人和原话由服务端按自己的消息记录填，
  员工端报的不算（verified=True）。对不上这台电脑的消息拒收——不能拿别人的消息给自己授权。
- 后台查看：原话属于聊天正文，没有聊天查看权限的账号看不到（和审计参数一个规矩）。
"""

from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Query, status
from pydantic import BaseModel, Field
from sqlalchemy import desc, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import chat_reader_or_none, require_admin, require_device
from ..models import AdminUser, ChannelBinding, ChannelMessage, Device, InstructionRun, RemoteInstruction, TaskGrant

client_router = APIRouter(prefix="/api/v1/client/grants", tags=["grants"])
admin_router = APIRouter(prefix="/api/v1/admin/grants", tags=["grants"], dependencies=[Depends(require_admin)])

ORIGINS = ("mobile", "dingtalk", "wecom", "instruction", "schedule", "event")
#: 服务端自己有记录、可以核对的来源
CHANNELS = ("mobile", "dingtalk", "wecom")


class GrantIn(BaseModel):
    id: str = Field(pattern=r"^[A-Za-z0-9_-]{8,40}$")
    conversation_id: str = Field(default="", max_length=64)
    origin: str = Field(pattern="^(" + "|".join(ORIGINS) + ")$")
    reference: str = Field(default="", max_length=100)
    requested_by: str = Field(default="", max_length=200)
    instruction: str = Field(default="", max_length=20000)
    permission: str = Field(pattern="^(readonly|workspace|full)$")
    workspace: str = Field(default="", max_length=500)


def _out(g: TaskGrant, with_content: bool) -> dict:
    return {
        "id": g.id, "device_id": g.device_id, "conversation_id": g.conversation_id, "origin": g.origin,
        "reference": g.reference, "requested_by": g.requested_by,
        "instruction": g.instruction if with_content else "",
        "permission": g.permission, "workspace": g.workspace, "verified": g.verified, "created_at": g.created_at,
    }


async def _verify(session: AsyncSession, device: Device, data: GrantIn) -> tuple[str, str] | None:
    """服务端自己的记录：(发起人, 原话)。不是可核对的来源返回 None。"""
    if data.origin in CHANNELS:
        msg = await session.get(ChannelMessage, int(data.reference)) if data.reference.isdigit() else None
        if msg is None or msg.device_id != device.id:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "找不到这台电脑收到的这条消息，不能据此授权")
        who = msg.external_name or msg.external_id
        if not who and msg.binding_id:
            binding = await session.get(ChannelBinding, msg.binding_id)
            who = getattr(binding, "external_name", "") or getattr(binding, "external_id", "") if binding else ""
        return f"{msg.channel}:{who}" if who else msg.channel, msg.text
    if data.origin == "instruction":
        run = await session.get(InstructionRun, int(data.reference)) if data.reference.isdigit() else None
        if run is None or run.device_id != device.id:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "找不到这台电脑的这次指令执行，不能据此授权")
        ins = await session.get(RemoteInstruction, run.instruction_id)
        if ins is None:
            return "指令中心", ""
        by = getattr(ins, "created_by", "") or "指令中心"
        return f"指令中心:{by}", getattr(ins, "prompt", "")
    return None


@client_router.post("", status_code=201)
async def report_grant(data: GrantIn, device: Device = Depends(require_device),
                       session: AsyncSession = Depends(get_session)):
    existing = await session.get(TaskGrant, data.id)
    if existing is not None:
        if existing.device_id != device.id:
            raise HTTPException(status.HTTP_409_CONFLICT, "授权记录编号冲突")
        return _out(existing, True)  # 重试：原样返回，不改内容
    checked = await _verify(session, device, data)
    g = TaskGrant(
        id=data.id, device_id=device.id, conversation_id=data.conversation_id, origin=data.origin,
        reference=data.reference, permission=data.permission, workspace=data.workspace,
        requested_by=(checked[0] if checked else data.requested_by)[:200],
        instruction=(checked[1] if checked else data.instruction)[:20000],
        verified=checked is not None,
    )
    session.add(g)
    await session.commit()
    return _out(g, True)


@admin_router.get("")
async def list_grants(
    device_id: int | None = None,
    conversation_id: str | None = None,
    origin: str | None = None,
    limit: int = Query(100, ge=1, le=1000),
    offset: int = Query(0, ge=0),
    reader: AdminUser | None = Depends(chat_reader_or_none),
    session: AsyncSession = Depends(get_session),
):
    q = select(TaskGrant).order_by(desc(TaskGrant.created_at)).limit(limit).offset(offset)
    if device_id:
        q = q.where(TaskGrant.device_id == device_id)
    if conversation_id:
        q = q.where(TaskGrant.conversation_id == conversation_id)
    if origin:
        q = q.where(TaskGrant.origin == origin)
    rows = (await session.scalars(q)).all()
    return [_out(g, reader is not None) for g in rows]
