"""
运维代理：给员工电脑下发任务（采集信息、清理、提速、装软件、系统修复、重启）。

两头：
  /api/v1/agent/*        代理自己调：注册、领任务、回报结果、下载安装包
  /api/v1/admin/agents…  后台调：看代理、下发、查进度、取消、管安装包

下发只认具名且有 can_dispatch 权限的管理员，每次下发和取消都记名。
"""

from __future__ import annotations

import hashlib
import hmac
import logging
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Literal

from fastapi import APIRouter, Depends, File, Form, HTTPException, Query, UploadFile, status
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field
from sqlalchemy import func, select, update
from sqlalchemy.ext.asyncio import AsyncSession

from ..config import get_settings
from ..crypto import hash_token, new_token
from ..db import get_session
from ..deps import require_admin, require_agent, require_dispatcher
from ..models import AdminUser, AgentJob, AgentRun, MachineAgent, SoftwarePackage
from ..services import agent_tasks

log = logging.getLogger("flyknit.agents")
router = APIRouter(prefix="/api/v1", tags=["agents"])

#: 下发后多久没被领走就作废（电脑一直关机、代理被卸载……）
PENDING_TTL = timedelta(days=7)
MAX_OUTPUT = 20_000
MAX_PACKAGE_BYTES = 2 * 1024 * 1024 * 1024
ACTIVE = ("pending", "running")


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _package_dir() -> Path:
    path = Path(get_settings().data_dir) / "packages"
    path.mkdir(parents=True, exist_ok=True)
    return path


def _package_path(pkg: SoftwarePackage) -> Path:
    return _package_dir() / f"{pkg.sha256}.{pkg.kind}"


# =====================================================================
# 代理端
# =====================================================================

class AgentRegisterIn(BaseModel):
    enrollment_key: str
    machine_guid: str = Field(min_length=8, max_length=64)
    machine_name: str = Field(default="", max_length=200)
    os_version: str = Field(default="", max_length=200)
    agent_version: str = Field(default="", max_length=50)


class AgentRegisterOut(BaseModel):
    agent_id: int
    token: str


class AgentPollIn(BaseModel):
    machine_name: str = Field(default="", max_length=200)
    os_version: str = Field(default="", max_length=200)
    agent_version: str = Field(default="", max_length=50)


class AgentRunOut(BaseModel):
    run_id: int
    job_id: int
    kind: str
    title: str
    params: dict


class AgentPollOut(BaseModel):
    runs: list[AgentRunOut]
    #: 下次多久后再来领（秒）
    poll_after: int = 30


class AgentFinishIn(BaseModel):
    status: Literal["succeeded", "failed"]
    exit_code: int | None = None
    output: str = ""
    result: dict = Field(default_factory=dict)


@router.post("/agent/register", response_model=AgentRegisterOut)
async def register_agent(data: AgentRegisterIn, session: AsyncSession = Depends(get_session)):
    """同一台电脑重装代理时沿用原来的记录，换一个新令牌。"""
    if not hmac.compare_digest(data.enrollment_key, get_settings().enrollment_key):
        raise HTTPException(status.HTTP_403_FORBIDDEN, "注册密钥错误")
    guid = data.machine_guid.strip().lower()
    token = new_token()
    agent = await session.scalar(select(MachineAgent).where(MachineAgent.machine_guid == guid))
    if agent is None:
        agent = MachineAgent(machine_guid=guid, token_hash=hash_token(token))
        session.add(agent)
    else:
        agent.token_hash = hash_token(token)
    agent.machine_name = data.machine_name or agent.machine_name
    agent.os_version = data.os_version or agent.os_version
    agent.agent_version = data.agent_version or agent.agent_version
    agent.last_seen = _now()
    await session.commit()
    log.info("运维代理注册 agent=%s machine=%s", agent.id, agent.machine_name)
    return AgentRegisterOut(agent_id=agent.id, token=token)


@router.post("/agent/poll", response_model=AgentPollOut)
async def poll(
    data: AgentPollIn,
    agent: MachineAgent = Depends(require_agent),
    session: AsyncSession = Depends(get_session),
):
    """领任务。顺带更新一下电脑名、系统版本、代理版本。"""
    for field in ("machine_name", "os_version", "agent_version"):
        value = getattr(data, field)
        if value:
            setattr(agent, field, value)
    await session.execute(
        update(AgentRun)
        .where(AgentRun.agent_id == agent.id, AgentRun.status == "pending", AgentRun.created_at < _now() - PENDING_TTL)
        .values(status="expired", finished_at=_now())
    )
    rows = (
        await session.execute(
            select(AgentRun, AgentJob)
            .join(AgentJob, AgentJob.id == AgentRun.job_id)
            .where(AgentRun.agent_id == agent.id, AgentRun.status == "pending")
            .order_by(AgentRun.id)
            .limit(10)
        )
    ).all()
    await session.commit()
    return AgentPollOut(
        runs=[AgentRunOut(run_id=r.id, job_id=j.id, kind=j.kind, title=j.title, params=j.params) for r, j in rows]
    )


async def _own_run(session: AsyncSession, agent: MachineAgent, run_id: int) -> AgentRun:
    run = await session.get(AgentRun, run_id)
    if run is None or run.agent_id != agent.id:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "任务不存在")
    return run


@router.post("/agent/runs/{run_id}/start")
async def start_run(
    run_id: int, agent: MachineAgent = Depends(require_agent), session: AsyncSession = Depends(get_session)
):
    """开始执行前先报一声。后台已经取消的，这里返回 ok=false，代理就不做了。"""
    run = await _own_run(session, agent, run_id)
    if run.status != "pending":
        return {"ok": False, "status": run.status}
    run.status = "running"
    run.started_at = _now()
    await session.commit()
    return {"ok": True, "status": run.status}


@router.post("/agent/runs/{run_id}/finish", status_code=204)
async def finish_run(
    run_id: int,
    data: AgentFinishIn,
    agent: MachineAgent = Depends(require_agent),
    session: AsyncSession = Depends(get_session),
):
    run = await _own_run(session, agent, run_id)
    if run.status not in ACTIVE:
        return
    run.status = data.status
    run.exit_code = data.exit_code
    run.output = data.output[:MAX_OUTPUT]
    run.finished_at = _now()
    job = await session.get(AgentJob, run.job_id)
    if job is not None and job.kind == "collect_info" and data.status == "succeeded":
        agent.inventory = data.result
        agent.inventory_at = _now()
        run.result = {"collected": True}
    else:
        run.result = data.result
    await session.commit()
    log.info("运维任务完成 run=%s agent=%s status=%s", run.id, agent.id, data.status)


@router.get("/agent/packages/{package_id}/download")
async def download_package(
    package_id: int, agent: MachineAgent = Depends(require_agent), session: AsyncSession = Depends(get_session)
):
    """只有手上正有这个安装任务的代理才能下载，安装包不对外随便发。"""
    pkg = await session.get(SoftwarePackage, package_id)
    if pkg is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "安装包不存在")
    runs = (
        await session.execute(
            select(AgentJob.params)
            .join(AgentRun, AgentRun.job_id == AgentJob.id)
            .where(AgentRun.agent_id == agent.id, AgentRun.status.in_(ACTIVE), AgentJob.kind == "install")
        )
    ).scalars()
    if not any(p.get("package_id") == package_id for p in runs):
        raise HTTPException(status.HTTP_403_FORBIDDEN, "没有安装这个软件的任务")
    path = _package_path(pkg)
    if not path.is_file():
        raise HTTPException(status.HTTP_410_GONE, "安装包文件已丢失，请在后台重新上传")
    return FileResponse(path, filename=pkg.filename, media_type="application/octet-stream")


# =====================================================================
# 后台
# =====================================================================

class AgentOut(BaseModel):
    id: int
    machine_guid: str
    machine_name: str
    os_version: str
    agent_version: str
    disabled: bool
    created_at: datetime
    last_seen: datetime | None
    inventory_at: datetime | None


class AgentDetailOut(AgentOut):
    inventory: dict


class AgentPatch(BaseModel):
    disabled: bool


class JobIn(BaseModel):
    kind: str
    params: dict = Field(default_factory=dict)
    agent_ids: list[int] = Field(min_length=1, max_length=1000)


class JobOut(BaseModel):
    id: int
    kind: str
    title: str
    params: dict
    created_by: str
    created_at: datetime
    cancelled_by: str
    total: int = 0
    counts: dict[str, int] = Field(default_factory=dict)


class RunOut(BaseModel):
    id: int
    job_id: int
    agent_id: int
    machine_name: str
    status: str
    created_at: datetime
    started_at: datetime | None
    finished_at: datetime | None
    exit_code: int | None
    output: str
    result: dict
    kind: str = ""
    title: str = ""


class JobDetailOut(JobOut):
    runs: list[RunOut]


class PackageOut(BaseModel):
    id: int
    name: str
    version: str
    filename: str
    kind: str
    size: int
    sha256: str
    silent_args: str
    uploaded_by: str
    created_at: datetime


def _agent_out(a: MachineAgent, detail: bool = False) -> AgentOut:
    data = {
        "id": a.id,
        "machine_guid": a.machine_guid,
        "machine_name": a.machine_name,
        "os_version": a.os_version,
        "agent_version": a.agent_version,
        "disabled": a.disabled,
        "created_at": a.created_at,
        "last_seen": a.last_seen,
        "inventory_at": a.inventory_at,
    }
    return AgentDetailOut(**data, inventory=a.inventory or {}) if detail else AgentOut(**data)


@router.get("/admin/agents", response_model=list[AgentOut], dependencies=[Depends(require_admin)])
async def list_agents(session: AsyncSession = Depends(get_session)):
    rows = await session.scalars(select(MachineAgent).order_by(MachineAgent.machine_name))
    return [_agent_out(a) for a in rows]


@router.get("/admin/agents/{agent_id}", response_model=AgentDetailOut, dependencies=[Depends(require_admin)])
async def get_agent(agent_id: int, session: AsyncSession = Depends(get_session)):
    agent = await session.get(MachineAgent, agent_id)
    if agent is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "运维代理不存在")
    return _agent_out(agent, detail=True)


@router.patch("/admin/agents/{agent_id}", response_model=AgentOut)
async def patch_agent(
    agent_id: int,
    data: AgentPatch,
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    agent = await session.get(MachineAgent, agent_id)
    if agent is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "运维代理不存在")
    agent.disabled = data.disabled
    if data.disabled:
        await session.execute(
            update(AgentRun)
            .where(AgentRun.agent_id == agent.id, AgentRun.status == "pending")
            .values(status="cancelled", finished_at=_now())
        )
    await session.commit()
    log.info("管理员 %s %s了运维代理 %s", user.username, "停用" if data.disabled else "启用", agent.machine_name)
    return _agent_out(agent)


@router.get("/admin/agents/{agent_id}/runs", response_model=list[RunOut], dependencies=[Depends(require_admin)])
async def agent_runs(agent_id: int, limit: int = Query(20, ge=1, le=200), session: AsyncSession = Depends(get_session)):
    rows = (
        await session.execute(
            select(AgentRun, AgentJob, MachineAgent.machine_name)
            .join(AgentJob, AgentJob.id == AgentRun.job_id)
            .join(MachineAgent, MachineAgent.id == AgentRun.agent_id)
            .where(AgentRun.agent_id == agent_id)
            .order_by(AgentRun.id.desc())
            .limit(limit)
        )
    ).all()
    return [_run_out(r, name, j) for r, j, name in rows]


def _run_out(r: AgentRun, machine_name: str, job: AgentJob | None = None) -> RunOut:
    return RunOut(
        id=r.id,
        job_id=r.job_id,
        agent_id=r.agent_id,
        machine_name=machine_name,
        status=r.status,
        created_at=r.created_at,
        started_at=r.started_at,
        finished_at=r.finished_at,
        exit_code=r.exit_code,
        output=r.output,
        result=r.result or {},
        kind=job.kind if job else "",
        title=job.title if job else "",
    )


async def _counts(session: AsyncSession, job_ids: list[int]) -> dict[int, dict[str, int]]:
    out: dict[int, dict[str, int]] = {i: {} for i in job_ids}
    if not job_ids:
        return out
    rows = await session.execute(
        select(AgentRun.job_id, AgentRun.status, func.count())
        .where(AgentRun.job_id.in_(job_ids))
        .group_by(AgentRun.job_id, AgentRun.status)
    )
    for job_id, st, n in rows:
        out[job_id][st] = n
    return out


def _job_out(job: AgentJob, counts: dict[str, int]) -> dict:
    return {
        "id": job.id,
        "kind": job.kind,
        "title": job.title,
        "params": job.params,
        "created_by": job.created_by,
        "created_at": job.created_at,
        "cancelled_by": job.cancelled_by,
        "total": sum(counts.values()),
        "counts": counts,
    }


@router.post("/admin/jobs", response_model=JobOut, status_code=201)
async def create_job(
    data: JobIn,
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    try:
        params = agent_tasks.validate(data.kind, data.params)
    except agent_tasks.TaskError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc

    package_name = ""
    if data.kind == "install":
        pkg = await session.get(SoftwarePackage, params["package_id"])
        if pkg is None:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "安装包不存在")
        package_name = f"{pkg.name} {pkg.version}".strip()
        # 下发时把安装包信息定下来，之后改了安装包也不影响这批任务
        params.update(name=package_name, kind=pkg.kind, sha256=pkg.sha256, size=pkg.size,
                      filename=pkg.filename, silent_args=pkg.silent_args)

    ids = list(dict.fromkeys(data.agent_ids))
    agents = list(await session.scalars(select(MachineAgent).where(MachineAgent.id.in_(ids), MachineAgent.disabled.is_(False))))
    if not agents:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "所选电脑都没有可用的运维代理")

    job = AgentJob(kind=data.kind, params=params, title=agent_tasks.title_of(data.kind, params, package_name),
                   created_by=user.username)
    session.add(job)
    await session.flush()
    for a in agents:
        session.add(AgentRun(job_id=job.id, agent_id=a.id))
    await session.commit()
    log.info("管理员 %s 下发运维任务 job=%s「%s」到 %d 台电脑", user.username, job.id, job.title, len(agents))
    return _job_out(job, {"pending": len(agents)})


@router.get("/admin/jobs", response_model=list[JobOut], dependencies=[Depends(require_admin)])
async def list_jobs(
    limit: int = Query(30, ge=1, le=200),
    offset: int = Query(0, ge=0),
    session: AsyncSession = Depends(get_session),
):
    jobs = list(await session.scalars(select(AgentJob).order_by(AgentJob.id.desc()).limit(limit).offset(offset)))
    counts = await _counts(session, [j.id for j in jobs])
    return [_job_out(j, counts[j.id]) for j in jobs]


@router.get("/admin/jobs/{job_id}", response_model=JobDetailOut, dependencies=[Depends(require_admin)])
async def get_job(job_id: int, session: AsyncSession = Depends(get_session)):
    job = await session.get(AgentJob, job_id)
    if job is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "任务不存在")
    rows = (
        await session.execute(
            select(AgentRun, MachineAgent.machine_name)
            .join(MachineAgent, MachineAgent.id == AgentRun.agent_id)
            .where(AgentRun.job_id == job_id)
            .order_by(MachineAgent.machine_name)
        )
    ).all()
    counts = (await _counts(session, [job_id]))[job_id]
    return {**_job_out(job, counts), "runs": [_run_out(r, name, job) for r, name in rows]}


@router.post("/admin/jobs/{job_id}/cancel", response_model=JobOut)
async def cancel_job(
    job_id: int,
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    """只能取消还没开始的。已经在跑的（比如装到一半）不中断，免得把电脑弄成半截状态。"""
    job = await session.get(AgentJob, job_id)
    if job is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "任务不存在")
    await session.execute(
        update(AgentRun)
        .where(AgentRun.job_id == job_id, AgentRun.status == "pending")
        .values(status="cancelled", finished_at=_now())
    )
    job.cancelled_by = user.username
    await session.commit()
    log.info("管理员 %s 取消了运维任务 job=%s", user.username, job_id)
    return _job_out(job, (await _counts(session, [job_id]))[job_id])


# ---------- 安装包 ----------

_SAFE_NAME = re.compile(r"[^\w.\-() \u4e00-\u9fff]")


@router.get("/admin/packages", response_model=list[PackageOut], dependencies=[Depends(require_admin)])
async def list_packages(session: AsyncSession = Depends(get_session)):
    return list(await session.scalars(select(SoftwarePackage).order_by(SoftwarePackage.id.desc())))


@router.post("/admin/packages", response_model=PackageOut, status_code=201)
async def upload_package(
    file: UploadFile = File(...),
    name: str = Form(..., min_length=1, max_length=100),
    version: str = Form("", max_length=50),
    silent_args: str = Form("", max_length=300),
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    filename = _SAFE_NAME.sub("_", Path(file.filename or "setup").name)[:255]
    kind = filename.rsplit(".", 1)[-1].lower() if "." in filename else ""
    if kind not in ("msi", "exe"):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "只支持 .msi 或 .exe 安装包")
    if kind == "exe" and not silent_args.strip():
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "exe 安装包必须填写静默安装参数（如 /S、/silent、/quiet），否则会卡在安装界面")

    tmp = _package_dir() / f".upload-{new_token()[:16]}"
    digest = hashlib.sha256()
    size = 0
    try:
        with tmp.open("wb") as out:
            while chunk := await file.read(1024 * 1024):
                size += len(chunk)
                if size > MAX_PACKAGE_BYTES:
                    raise HTTPException(status.HTTP_400_BAD_REQUEST, "安装包超过 2 GB")
                digest.update(chunk)
                out.write(chunk)
        if size == 0:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "文件是空的")
        pkg = SoftwarePackage(
            name=name.strip(),
            version=version.strip(),
            filename=filename,
            kind=kind,
            size=size,
            sha256=digest.hexdigest(),
            silent_args=silent_args.strip() if kind == "exe" else "",
            uploaded_by=user.username,
        )
        target = _package_path(pkg)
        if target.exists():
            tmp.unlink()
        else:
            tmp.replace(target)
    finally:
        tmp.unlink(missing_ok=True)
    session.add(pkg)
    await session.commit()
    log.info("管理员 %s 上传安装包 %s %s（%d 字节）", user.username, pkg.name, pkg.version, size)
    return pkg


@router.delete("/admin/packages/{package_id}", status_code=204)
async def delete_package(
    package_id: int,
    user: AdminUser = Depends(require_dispatcher),
    session: AsyncSession = Depends(get_session),
):
    pkg = await session.get(SoftwarePackage, package_id)
    if pkg is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "安装包不存在")
    path = _package_path(pkg)
    await session.delete(pkg)
    await session.commit()
    others = await session.scalar(select(func.count()).select_from(SoftwarePackage).where(SoftwarePackage.sha256 == pkg.sha256))
    if not others:
        path.unlink(missing_ok=True)
    log.info("管理员 %s 删除了安装包 %s", user.username, pkg.name)
