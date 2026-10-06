"""
远程指令：管理员下发一段自然语言意图，由员工电脑里的 AI agent 自行理解并执行。

两头：
  /api/v1/client/instructions…   员工端调：拉待办指令、回报执行结果
  /api/v1/admin/instructions…    后台调：下发、查进度、取消、管模板

和运维代理（agents.py）分工：代理走 SYSTEM 服务按固定目录执行（装软件、系统修复这类要
管理员权限的）；这里走员工端里的对话式 agent，内容是自由文本，执行结果是一段回答。

下发只认具名且有 can_dispatch 权限的管理员，每次下发和取消都记名。
"""

from __future__ import annotations

import logging
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, Depends, HTTPException, Query, status
from pydantic import BaseModel, Field
from sqlalchemy import func, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_admin, require_device, require_dispatcher
from ..models import AdminUser, Device, InstructionRun, InstructionTemplate, RemoteInstruction

log = logging.getLogger("flyknit.instructions")
router = APIRouter(prefix="/api/v1", tags=["instructions"])

#: 下发后多久没被领走就作废（电脑一直关机、没人登录……）
PENDING_TTL = timedelta(days=7)
MAX_ANSWER = 20_000
MAX_PROMPT = 4000
ACTIVE = ("pending", "running")


def _now() -> datetime:
    return datetime.now(timezone.utc)


# =====================================================================
# 员工端
# =====================================================================

class ClientInstructionOut(BaseModel):
    run_id: int
    instruction_id: int
    title: str
    prompt: str


class ClientPollOut(BaseModel):
    instructions: list[ClientInstructionOut]
    poll_after: int = 60


class InstructionFinishIn(BaseModel):
    status: str = Field(pattern="^(succeeded|failed)$")
    answer: str = ""
    error: str = ""
    conversation_id: str = Field(default="", max_length=64)


@router.get("/client/instructions", response_model=ClientPollOut)
async def poll_instructions(
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    """员工端拉取发给本机、还没执行的指令。"""
    await session.execute(
        update(InstructionRun)
        .where(
            InstructionRun.device_id == device.id,
            InstructionRun.status == "pending",
            InstructionRun.created_at < _now() - PENDING_TTL,
        )
        .values(status="expired", finished_at=_now())
    )
    rows = (
        await session.execute(
            select(InstructionRun, RemoteInstruction)
            .join(RemoteInstruction, RemoteInstruction.id == InstructionRun.instruction_id)
            .where(InstructionRun.device_id == device.id, InstructionRun.status == "pending")
            .order_by(InstructionRun.id)
            .limit(10)
        )
    ).all()
    await session.commit()
    return ClientPollOut(
        instructions=[
            ClientInstructionOut(run_id=r.id, instruction_id=i.id, title=i.title, prompt=i.prompt)
            for r, i in rows
        ]
    )


async def _own_run(session: AsyncSession, device: Device, run_id: int) -> InstructionRun:
    run = await session.get(InstructionRun, run_id)
    if run is None or run.device_id != device.id:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "指令不存在")
    return run


@router.post("/client/instructions/{run_id}/start")
async def start_instruction(
    run_id: int, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)
):
    """开始执行前报一声。后台已取消的，返回 ok=false，员工端就不做了。"""
    run = await _own_run(session, device, run_id)
    if run.status != "pending":
        return {"ok": False, "status": run.status}
    run.status = "running"
    run.started_at = _now()
    await session.commit()
    return {"ok": True, "status": run.status}


@router.post("/client/instructions/{run_id}/finish", status_code=204)
async def finish_instruction(
    run_id: int,
    data: InstructionFinishIn,
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    run = await _own_run(session, device, run_id)
    if run.status not in ACTIVE:
        return
    run.status = data.status
    run.answer = data.answer[:MAX_ANSWER]
    run.error = data.error[:MAX_ANSWER]
    run.conversation_id = data.conversation_id
    run.finished_at = _now()
    await session.commit()
    log.info("远程指令完成 run=%s device=%s status=%s", run.id, device.id, data.status)


# =====================================================================
# 后台
# =====================================================================

class InstructionIn(BaseModel):
    prompt: str = Field(min_length=1, max_length=MAX_PROMPT)
    title: str = Field(default="", max_length=200)
    device_ids: list[int] = Field(min_length=1, max_length=5000)


class InstructionOut(BaseModel):
    id: int
    prompt: str
    title: str
    created_by: str
    created_at: datetime
    cancelled_by: str
    total: int = 0
    counts: dict[str, int] = Field(default_factory=dict)


class InstructionRunOut(BaseModel):
    id: int
    device_id: int
    machine_name: str
    user_name: str
    status: str
    created_at: datetime
    started_at: datetime | None
    finished_at: datetime | None
    answer: str
    error: str
    conversation_id: str


class InstructionDetailOut(InstructionOut):
    runs: list[InstructionRunOut]


class TemplateIn(BaseModel):
    name: str = Field(min_length=1, max_length=100)
    prompt: str = Field(min_length=1, max_length=MAX_PROMPT)


class TemplateOut(BaseModel):
    id: int
    name: str
    prompt: str
    created_by: str
    created_at: datetime


def _title_of(prompt: str, title: str) -> str:
    title = title.strip()
    if title:
        return title[:200]
    one_line = " ".join(prompt.split())
    return one_line[:40] + ("…" if len(one_line) > 40 else "")


async def _counts(session: AsyncSession, instruction_ids: list[int]) -> dict[int, dict[str, int]]:
    out: dict[int, dict[str, int]] = {i: {} for i in instruction_ids}
    if not instruction_ids:
        return out
    rows = await session.execute(
        select(InstructionRun.instruction_id, InstructionRun.status, func.count())
        .where(InstructionRun.instruction_id.in_(instruction_ids))
        .group_by(InstructionRun.instruction_id, InstructionRun.status)
    )
    for ins_id, st, n in rows:
        out[ins_id][st] = n
    return out


def _out(ins: RemoteInstruction, counts: dict[str, int]) -> dict:
    return {
        "id": ins.id,
        "prompt": ins.prompt,
        "title": ins.title,
        "created_by": ins.created_by,
        "created_at": ins.created_at,
        "cancelled_by": ins.cancelled_by,
        "total": sum(counts.values()),
        "counts": counts,
    }


@router.post("/admin/instructions", response_model=InstructionOut, status_code=201)
async def create_instruction(
    data: InstructionIn,
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    ids = list(dict.fromkeys(data.device_ids))
    devices = list(await session.scalars(select(Device).where(Device.id.in_(ids), Device.disabled.is_(False))))
    if not devices:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "所选设备都不可用（可能已停用或不存在）")

    ins = RemoteInstruction(
        prompt=data.prompt.strip(),
        title=_title_of(data.prompt, data.title),
        created_by=user.username,
    )
    session.add(ins)
    await session.flush()
    for d in devices:
        session.add(InstructionRun(instruction_id=ins.id, device_id=d.id))
    await session.commit()
    log.info("管理员 %s 下发指令 ins=%s「%s」到 %d 台电脑", user.username, ins.id, ins.title, len(devices))
    return _out(ins, {"pending": len(devices)})


@router.get("/admin/instructions", response_model=list[InstructionOut], dependencies=[Depends(require_admin)])
async def list_instructions(
    limit: int = Query(30, ge=1, le=200),
    offset: int = Query(0, ge=0),
    session: AsyncSession = Depends(get_session),
):
    items = list(await session.scalars(select(RemoteInstruction).order_by(RemoteInstruction.id.desc()).limit(limit).offset(offset)))
    counts = await _counts(session, [i.id for i in items])
    return [_out(i, counts[i.id]) for i in items]


@router.get("/admin/instructions/{instruction_id}", response_model=InstructionDetailOut, dependencies=[Depends(require_admin)])
async def get_instruction(instruction_id: int, session: AsyncSession = Depends(get_session)):
    ins = await session.get(RemoteInstruction, instruction_id)
    if ins is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "指令不存在")
    rows = (
        await session.execute(
            select(InstructionRun, Device.machine_name, Device.user_name)
            .join(Device, Device.id == InstructionRun.device_id)
            .where(InstructionRun.instruction_id == instruction_id)
            .order_by(Device.machine_name)
        )
    ).all()
    counts = (await _counts(session, [instruction_id]))[instruction_id]
    runs = [
        InstructionRunOut(
            id=r.id,
            device_id=r.device_id,
            machine_name=name,
            user_name=user_name,
            status=r.status,
            created_at=r.created_at,
            started_at=r.started_at,
            finished_at=r.finished_at,
            answer=r.answer,
            error=r.error,
            conversation_id=r.conversation_id,
        )
        for r, name, user_name in rows
    ]
    return {**_out(ins, counts), "runs": runs}


@router.post("/admin/instructions/{instruction_id}/cancel", response_model=InstructionOut)
async def cancel_instruction(
    instruction_id: int,
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    """只能取消还没开始的；已经在跑的不中断。"""
    ins = await session.get(RemoteInstruction, instruction_id)
    if ins is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "指令不存在")
    await session.execute(
        update(InstructionRun)
        .where(InstructionRun.instruction_id == instruction_id, InstructionRun.status == "pending")
        .values(status="cancelled", finished_at=_now())
    )
    ins.cancelled_by = user.username
    await session.commit()
    log.info("管理员 %s 取消了指令 ins=%s", user.username, instruction_id)
    return _out(ins, (await _counts(session, [instruction_id]))[instruction_id])


# ---------- 指令模板 ----------

@router.get("/admin/instruction-templates", response_model=list[TemplateOut], dependencies=[Depends(require_admin)])
async def list_templates(session: AsyncSession = Depends(get_session)):
    return list(await session.scalars(select(InstructionTemplate).order_by(InstructionTemplate.id.desc())))


@router.post("/admin/instruction-templates", response_model=TemplateOut, status_code=201)
async def create_template(
    data: TemplateIn,
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    tpl = InstructionTemplate(name=data.name.strip(), prompt=data.prompt.strip(), created_by=user.username)
    session.add(tpl)
    await session.commit()
    return tpl


@router.delete("/admin/instruction-templates/{template_id}", status_code=204)
async def delete_template(
    template_id: int,
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    tpl = await session.get(InstructionTemplate, template_id)
    if tpl is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "模板不存在")
    await session.delete(tpl)
    await session.commit()
