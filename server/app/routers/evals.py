"""
回归评测的后台接口（docs/evals.md）。

- 看：管理员都能看题集、每次跑的结果（AI 的回答是对测试题的回答，不是员工的对话）；
- 建、改、删题集、跑一次：具名且能下发指令的管理员（跑一次等于让一台电脑去做事，和「指令中心」一样）。
执行机领题、回报结果走通道消息的现有接口（channel=eval），授权核对见 grants.py。
"""

from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, Field
from sqlalchemy import desc, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_admin, require_dispatcher
from ..models import AdminUser, Device, EvalResult, EvalRun, EvalSuite, Expert
from ..services import eval_service
from ..services.channel_hub import hub

router = APIRouter(prefix="/api/v1/admin/evals", tags=["evals"])

MAX_SUITES = 100


class SuiteIn(BaseModel):
    name: str = Field(min_length=1, max_length=100)
    description: str = Field(default="", max_length=500)
    expert_id: str = Field(default="", max_length=40)
    device_id: int | None = None
    permission: str = Field(default="readonly", pattern="^(readonly|workspace)$")
    pass_score: int = Field(default=60, ge=0, le=100)
    cases: list[dict] = Field(default_factory=list, max_length=eval_service.MAX_CASES)


class SuitePatch(BaseModel):
    name: str | None = Field(default=None, min_length=1, max_length=100)
    description: str | None = Field(default=None, max_length=500)
    expert_id: str | None = Field(default=None, max_length=40)
    device_id: int | None = None
    permission: str | None = Field(default=None, pattern="^(readonly|workspace)$")
    pass_score: int | None = Field(default=None, ge=0, le=100)
    cases: list[dict] | None = Field(default=None, max_length=eval_service.MAX_CASES)


class RunIn(BaseModel):
    note: str = Field(default="", max_length=300)


def _cases(raw: list[dict]) -> list[dict]:
    try:
        return eval_service.normalize_cases(raw)
    except ValueError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc


async def _check_refs(session: AsyncSession, expert_id: str, device_id: int | None) -> None:
    if expert_id and await session.get(Expert, expert_id) is None:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "没有这位专家")
    if device_id is not None:
        device = await session.get(Device, device_id)
        if device is None or device.disabled:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "没有这台电脑，或者已停用")


def _run_out(r: EvalRun) -> dict:
    return {"id": r.id, "suite_id": r.suite_id, "status": r.status, "note": r.note, "device_id": r.device_id,
            "expert_id": r.expert_id, "total": r.total, "passed": r.passed, "score": r.score,
            "regressions": list(r.regressions or []), "created_by": r.created_by, "created_at": r.created_at,
            "finished_at": r.finished_at}


def _result_out(r: EvalResult) -> dict:
    return {"id": r.id, "case_id": r.case_id, "prompt": r.prompt, "status": r.status, "answer": r.answer, "error": r.error,
            "checks": list(r.checks or []), "judge_score": r.judge_score, "judge_reason": r.judge_reason,
            "score": r.score, "passed": r.passed, "finished_at": r.finished_at}


async def _suite_out(session: AsyncSession, s: EvalSuite) -> dict:
    device = await session.get(Device, s.device_id) if s.device_id else None
    expert = await session.get(Expert, s.expert_id) if s.expert_id else None
    last = await session.scalar(select(EvalRun).where(EvalRun.suite_id == s.id).order_by(desc(EvalRun.id)).limit(1))
    if last is not None and last.status == "running":
        await eval_service.refresh(session, last)
    return {
        "id": s.id, "name": s.name, "description": s.description, "expert_id": s.expert_id,
        "expert_name": expert.name if expert else "", "device_id": s.device_id,
        "device_name": (device.owner or device.machine_name) if device else "",
        "device_online": bool(device and hub.online(device.id)), "permission": s.permission, "pass_score": s.pass_score,
        "cases": list(s.cases or []), "created_by": s.created_by, "updated_at": s.updated_at,
        "last_run": _run_out(last) if last else None,
    }


@router.get("", dependencies=[Depends(require_admin)])
async def list_suites(session: AsyncSession = Depends(get_session)):
    suites = (await session.scalars(select(EvalSuite).order_by(EvalSuite.id))).all()
    return {"items": [await _suite_out(session, s) for s in suites]}


@router.post("", status_code=201)
async def create_suite(data: SuiteIn, user: AdminUser = Depends(require_dispatcher), session: AsyncSession = Depends(get_session)):
    if len((await session.scalars(select(EvalSuite.id))).all()) >= MAX_SUITES:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, f"题集最多 {MAX_SUITES} 个")
    await _check_refs(session, data.expert_id.strip(), data.device_id)
    suite = EvalSuite(name=data.name.strip(), description=data.description.strip(), expert_id=data.expert_id.strip(),
                      device_id=data.device_id, permission=data.permission, pass_score=data.pass_score,
                      cases=_cases(data.cases), created_by=user.username)
    session.add(suite)
    await session.commit()
    return await _suite_out(session, suite)


@router.patch("/{suite_id}")
async def update_suite(suite_id: int, data: SuitePatch, _: AdminUser = Depends(require_dispatcher),
                       session: AsyncSession = Depends(get_session)):
    suite = await session.get(EvalSuite, suite_id)
    if suite is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个题集")
    values = data.model_dump(exclude_unset=True)
    if "cases" in values:
        values["cases"] = _cases(values["cases"] or [])
    if "expert_id" in values:
        values["expert_id"] = (values["expert_id"] or "").strip()
    await _check_refs(session, values.get("expert_id", ""), values.get("device_id"))
    for key, value in values.items():
        setattr(suite, key, value)
    await session.commit()
    return await _suite_out(session, suite)


@router.delete("/{suite_id}", status_code=204)
async def delete_suite(suite_id: int, _: AdminUser = Depends(require_dispatcher), session: AsyncSession = Depends(get_session)):
    suite = await session.get(EvalSuite, suite_id)
    if suite is not None:
        await session.delete(suite)
        await session.commit()


@router.post("/{suite_id}/run", status_code=201)
async def run_suite(suite_id: int, data: RunIn, user: AdminUser = Depends(require_dispatcher),
                    session: AsyncSession = Depends(get_session)):
    """跑一次：每题派给执行机（只预演）。执行机不可用、上一次还没跑完时不派。"""
    suite = await session.get(EvalSuite, suite_id)
    if suite is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个题集")
    try:
        run = await eval_service.start(session, suite, user.username, data.note)
    except ValueError as exc:
        raise HTTPException(status.HTTP_409_CONFLICT, str(exc)) from exc
    hub.notify(run.device_id)
    return _run_out(run)


@router.get("/{suite_id}/runs", dependencies=[Depends(require_admin)])
async def list_runs(suite_id: int, session: AsyncSession = Depends(get_session)):
    runs = (await session.scalars(select(EvalRun).where(EvalRun.suite_id == suite_id).order_by(desc(EvalRun.id)).limit(30))).all()
    for r in runs:
        if r.status == "running":
            await eval_service.refresh(session, r)
    return {"items": [_run_out(r) for r in runs]}


@router.get("/runs/{run_id}", dependencies=[Depends(require_admin)])
async def get_run(run_id: int, session: AsyncSession = Depends(get_session)):
    run = await session.get(EvalRun, run_id)
    if run is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这次评测")
    results = await eval_service.refresh(session, run) if run.status == "running" else \
        (await session.scalars(select(EvalResult).where(EvalResult.run_id == run.id))).all()
    suite = await session.get(EvalSuite, run.suite_id) if run.suite_id else None
    order = {c.get("id"): i for i, c in enumerate((suite.cases or []) if suite else [])}
    results = sorted(results, key=lambda r: (order.get(r.case_id, 999), r.id))
    return {**_run_out(run), "results": [_result_out(r) for r in results]}
