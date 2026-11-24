"""
无人值守任务的后台接口（docs/unattended-jobs.md）。

- 看：管理员都能看任务、执行机在不在线、最近的执行记录；
- 建、改、删、立即运行：具名且能下发指令的管理员（和「指令中心」一样，等于让一台电脑去做事）；
- 总开关：超级管理员（默认关）。
执行机领任务、回报结果走通道消息的现有接口（channel=unattended），授权核对见 grants.py。
"""

from __future__ import annotations

from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, Field
from sqlalchemy import desc, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_admin, require_dispatcher, require_owner
from ..models import AdminUser, ChannelMessage, Device, UnattendedJob
from ..services import settings_store, unattended_service
from ..services.channel_hub import hub

router = APIRouter(prefix="/api/v1/admin/unattended", tags=["unattended"])

MAX_RECIPIENTS = 200


class JobIn(BaseModel):
    name: str = Field(min_length=1, max_length=100)
    prompt: str = Field(min_length=1, max_length=unattended_service.MAX_PROMPT)
    device_id: int
    permission: str = Field(default="readonly", pattern="^(readonly|workspace)$")
    rehearsal: bool = True
    enabled: bool = True
    schedule: dict = Field(default_factory=lambda: {"kind": "daily", "time": "08:00"})
    utc_offset: int = Field(default=420, ge=-720, le=840)
    notify_departments: list[str] = Field(default_factory=list, max_length=MAX_RECIPIENTS)
    notify_devices: list[int] = Field(default_factory=list, max_length=MAX_RECIPIENTS)


class JobPatch(BaseModel):
    name: str | None = Field(default=None, min_length=1, max_length=100)
    prompt: str | None = Field(default=None, min_length=1, max_length=unattended_service.MAX_PROMPT)
    device_id: int | None = None
    permission: str | None = Field(default=None, pattern="^(readonly|workspace)$")
    rehearsal: bool | None = None
    enabled: bool | None = None
    schedule: dict | None = None
    utc_offset: int | None = Field(default=None, ge=-720, le=840)
    notify_departments: list[str] | None = Field(default=None, max_length=MAX_RECIPIENTS)
    notify_devices: list[int] | None = Field(default=None, max_length=MAX_RECIPIENTS)


class SwitchIn(BaseModel):
    enabled: bool


def _schedule(raw: dict) -> dict:
    try:
        return unattended_service.normalize_schedule(raw)
    except (ValueError, TypeError) as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc


async def _device(session: AsyncSession, device_id: int) -> Device:
    device = await session.get(Device, device_id)
    if device is None:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "没有这台执行机")
    if device.disabled:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "这台电脑已停用，不能当执行机")
    return device


def _out(job: UnattendedJob, device: Device | None) -> dict:
    try:
        when = unattended_service.describe(job.schedule or {})
    except ValueError:
        when = "时间表有误"
    return {
        "id": job.id, "name": job.name, "prompt": job.prompt, "device_id": job.device_id,
        "device_name": (device.owner or device.machine_name) if device else "",
        "device_online": bool(device and hub.online(device.id)),
        "permission": job.permission, "rehearsal": job.rehearsal, "enabled": job.enabled,
        "schedule": job.schedule or {}, "schedule_text": when, "utc_offset": job.utc_offset,
        "notify_departments": job.notify_departments or [], "notify_devices": job.notify_devices or [],
        "next_run_at": job.next_run_at, "last_run_at": job.last_run_at, "run_count": job.run_count,
        "skipped_at": job.skipped_at, "created_by": job.created_by, "created_at": job.created_at,
    }


@router.get("", dependencies=[Depends(require_admin)])
async def list_jobs(session: AsyncSession = Depends(get_session)):
    jobs = (await session.scalars(select(UnattendedJob).order_by(UnattendedJob.id))).all()
    items = [_out(j, await session.get(Device, j.device_id) if j.device_id else None) for j in jobs]
    recent = (await session.scalars(
        select(ChannelMessage).where(ChannelMessage.channel == unattended_service.CHANNEL)
        .order_by(desc(ChannelMessage.id)).limit(50))).all()
    runs = [{"id": m.id, "job_id": int(m.external_id) if m.external_id.isdigit() else None, "job_name": m.external_name,
             "status": m.status, "error": m.error, "answer": m.answer[:2000], "rehearsal": m.rehearsal,
             "delivered": m.replied, "created_at": m.created_at, "finished_at": m.finished_at} for m in recent]
    return {"enabled": await unattended_service.master_on(session), "items": items, "runs": runs}


@router.put("/switch")
async def set_switch(data: SwitchIn, user: AdminUser | None = Depends(require_owner), session: AsyncSession = Depends(get_session)):
    # None = 共享的 admin_token（部署和救急用）
    who = user.username if user else "admin_token"
    await settings_store.set_value(session, unattended_service.KEY, {"enabled": data.enabled, "updated_by": who})
    return {"enabled": data.enabled}


@router.post("", status_code=201)
async def create_job(data: JobIn, user: AdminUser = Depends(require_dispatcher), session: AsyncSession = Depends(get_session)):
    device = await _device(session, data.device_id)
    schedule = _schedule(data.schedule)
    job = UnattendedJob(
        name=data.name.strip(), prompt=data.prompt.strip(), device_id=device.id, permission=data.permission,
        rehearsal=data.rehearsal, enabled=data.enabled, schedule=schedule, utc_offset=data.utc_offset,
        notify_departments=[d.strip() for d in data.notify_departments if d.strip()],
        notify_devices=sorted(set(data.notify_devices)), created_by=user.username,
        next_run_at=unattended_service.next_after(schedule, data.utc_offset, datetime.now(timezone.utc)),
    )
    session.add(job)
    await session.commit()
    return _out(job, device)


@router.patch("/{job_id}")
async def update_job(job_id: int, data: JobPatch, _: AdminUser = Depends(require_dispatcher),
                     session: AsyncSession = Depends(get_session)):
    job = await session.get(UnattendedJob, job_id)
    if job is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个任务")
    values = data.model_dump(exclude_none=True)
    if "device_id" in values:
        await _device(session, values["device_id"])
    if "schedule" in values:
        values["schedule"] = _schedule(values["schedule"])
    if "notify_departments" in values:
        values["notify_departments"] = [d.strip() for d in values["notify_departments"] if d.strip()]
    if "notify_devices" in values:
        values["notify_devices"] = sorted(set(values["notify_devices"]))
    for key, value in values.items():
        setattr(job, key, value)
    if {"schedule", "utc_offset", "enabled"} & values.keys():
        job.next_run_at = unattended_service.next_after(job.schedule or {}, job.utc_offset, datetime.now(timezone.utc))
    await session.commit()
    return _out(job, await session.get(Device, job.device_id) if job.device_id else None)


@router.delete("/{job_id}", status_code=204)
async def delete_job(job_id: int, _: AdminUser = Depends(require_dispatcher), session: AsyncSession = Depends(get_session)):
    job = await session.get(UnattendedJob, job_id)
    if job is not None:
        await session.delete(job)
        await session.commit()


@router.post("/{job_id}/run")
async def run_now(job_id: int, _: AdminUser = Depends(require_dispatcher), session: AsyncSession = Depends(get_session)):
    """立即跑一次（不影响时间表）。总开关关着、执行机不可用、上一次还没做完时不派。"""
    job = await session.get(UnattendedJob, job_id)
    if job is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个任务")
    if not await unattended_service.master_on(session):
        raise HTTPException(status.HTTP_409_CONFLICT, "无人值守任务的总开关没有打开")
    row = await unattended_service.dispatch(session, job)
    await session.commit()
    if row is None:
        raise HTTPException(status.HTTP_409_CONFLICT, "没有派出去：执行机不存在 / 已停用，或者上一次还没做完")
    hub.notify(row.device_id)
    return {"message_id": row.id}
