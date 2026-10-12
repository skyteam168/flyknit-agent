"""
专家派遣：主助手把子任务交给专家，员工端执行，服务端在这里登记、计数、收尾。

一次登记就是一次派遣：失败、超时、取消的都算，同一次主任务最多 3 次。被拒绝的（开关关着、专家不可用、
次数用完）不登记、不算次数。模型网关按这里的记录拦请求、记用量（见 gateway.py）。
"""

import logging
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy import desc, func, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import chat_reader_or_none, require_admin, require_device
from ..models import AdminAccess, AdminUser, Device, Expert, Subtask
from ..schemas import (
    SubtaskAdminOut,
    SubtaskCancelIn,
    SubtaskCreatedOut,
    SubtaskCreateIn,
    SubtaskExpertOut,
    SubtaskFinishIn,
    SubtaskStateOut,
    SubtaskTokens,
)
from ..services import expert_registry, subtask_store
from ..services.subtask_store import utc

log = logging.getLogger("flyknit.subtasks")
client_router = APIRouter(prefix="/api/v1/client/subtasks", tags=["subtasks"])
admin_router = APIRouter(prefix="/api/v1/admin/subtasks", tags=["subtasks"], dependencies=[Depends(require_admin)])


def _reject(code: int, kind: str, message: str) -> HTTPException:
    return HTTPException(code, {"code": kind, "message": message})


def _state(st: Subtask, already_final: bool = False) -> SubtaskStateOut:
    return SubtaskStateOut(
        id=st.id,
        status=st.status,
        final=st.status in subtask_store.FINAL,
        already_final=already_final,
        tokens=SubtaskTokens(prompt=st.prompt_tokens or 0, completion=st.completion_tokens or 0,
                             estimated_calls=st.estimated_calls or 0),
        error_code=st.error_code or "",
    )


async def _mine(session: AsyncSession, subtask_id: str, device: Device) -> Subtask:
    """只认这台电脑自己的子任务。别人的和不存在的一样回 404，不透露有没有这个 ID。"""
    st = await session.get(Subtask, subtask_id)
    if st is None or st.device_id != device.id:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "子任务不存在")
    return await subtask_store.expire_if_due(session, st)


# ---------- 员工端 ----------
@client_router.post("", response_model=SubtaskCreatedOut, status_code=201)
async def create_subtask(data: SubtaskCreateIn, device: Device = Depends(require_device),
                         session: AsyncSession = Depends(get_session)):
    if not await subtask_store.delegation_enabled(session, device.id):
        raise _reject(403, "delegation_disabled", "IT 没有开启「派遣专家」")
    e = await session.get(Expert, data.expert_id)
    snap = expert_registry.from_snapshot(e.published) if e is not None and e.status == "published" else None
    # 看的是已发布的快照：草稿里改了 allow_delegate 没发布，员工端不受影响
    if snap is None or not snap.allow_delegate:
        raise _reject(403, "expert_unavailable", "这位专家不存在、未发布或不接受派遣")

    # 回滚会让会话里的对象全部过期，后面再碰就要去库里懒加载（异步下直接报错），先取出来
    device_id, machine_name, user_name = device.id, device.machine_name, device.user_name
    expert_id, expert_name, published_version = e.id, snap.name, e.version
    token_cap = subtask_store.clamp(data.token_cap, subtask_store.TOKEN_CAP_RANGE)
    timeout = subtask_store.clamp(data.timeout_seconds, subtask_store.TIMEOUT_RANGE)

    # 并发登记：两边都算出同一个 seq 时唯一约束挡住后到的那个，回滚后重算。每撞一次都说明别人登记成功了一次，
    # 所以最多撞 MAX_PER_RUN 次就会看到次数用完
    for _ in range(subtask_store.MAX_PER_RUN + 2):
        top = await session.scalar(
            select(func.max(Subtask.seq)).where(Subtask.device_id == device_id, Subtask.run_id == data.run_id))
        seq = (top or 0) + 1
        if seq > subtask_store.MAX_PER_RUN:
            break
        now = datetime.now(timezone.utc)
        st = Subtask(
            id=subtask_store.new_id(), device_id=device_id, machine_name=machine_name, user_name=user_name,
            conversation_id=data.conversation_id, run_id=data.run_id, seq=seq,
            expert_id=expert_id, expert_name=expert_name[:100], expert_version=data.expert_version,
            published_version=published_version, task=data.task, status="running", token_cap=token_cap,
            deadline_at=now + timedelta(seconds=timeout), created_at=now,
        )
        session.add(st)
        try:
            await session.commit()
        except IntegrityError:
            await session.rollback()
            continue
        log.info("派遣专家 device=%s run=%s seq=%s expert=%s subtask=%s", device_id, data.run_id, seq, expert_id, st.id)
        return SubtaskCreatedOut(
            id=st.id, seq=seq, status="running",
            expert=SubtaskExpertOut(id=expert_id, version=data.expert_version,
                                    published_version=published_version, name=expert_name),
            token_cap=token_cap, deadline_at=st.deadline_at,
        )
    raise _reject(409, "delegation_limit", f"这一轮已经派遣过 {subtask_store.MAX_PER_RUN} 次专家")


@client_router.post("/{subtask_id}/finish", response_model=SubtaskStateOut)
async def finish_subtask(subtask_id: str, data: SubtaskFinishIn, device: Device = Depends(require_device),
                         session: AsyncSession = Depends(get_session)):
    """交卷。只有 running 的能结束；已经是终态（含服务端判的超时、取消）时不覆盖，原样返回。"""
    st = await _mine(session, subtask_id, device)
    result, truncated = subtask_store.clip_result(data.result)
    changed = await subtask_store.close(
        session, st, data.status, summary=data.summary, result=result, result_truncated=truncated,
        error_code=data.error_code, error=data.error, tool_calls=data.tool_calls, local_tokens=data.local_tokens,
    )
    return _state(st, already_final=not changed)


@client_router.post("/{subtask_id}/cancel", response_model=SubtaskStateOut)
async def cancel_subtask(subtask_id: str, data: SubtaskCancelIn, device: Device = Depends(require_device),
                         session: AsyncSession = Depends(get_session)):
    st = await _mine(session, subtask_id, device)
    final = "timed_out" if data.reason == "timeout" else "cancelled"
    changed = await subtask_store.close(
        session, st, final, cancel_reason=data.reason, cancel_requested_at=datetime.now(timezone.utc),
        error_code=data.reason,
    )
    return _state(st, already_final=not changed)


@client_router.get("/{subtask_id}", response_model=SubtaskStateOut)
async def get_subtask(subtask_id: str, device: Device = Depends(require_device),
                      session: AsyncSession = Depends(get_session)):
    return _state(await _mine(session, subtask_id, device))


# ---------- 管理端 ----------
#: 派遣记录里属于「聊天内容」的字段，和审计记录的参数、摘要一个规矩：没有查看聊天内容的权限就不给
_CONTENT_FIELDS = ("task", "summary", "error")


@admin_router.get("", response_model=list[SubtaskAdminOut])
async def list_subtasks(
    limit: int = Query(100, ge=1, le=500),
    expert_id: str | None = None,
    status_: str | None = Query(None, alias="status"),
    reader: AdminUser | None = Depends(chat_reader_or_none),
    session: AsyncSession = Depends(get_session),
):
    """最近的派遣记录。过了 deadline 还挂着 running 的先判成超时，列表上看到的就是真实状态。"""
    await subtask_store.expire_all_due(session)
    q = (select(Subtask, Device).outerjoin(Device, Device.id == Subtask.device_id)
         .order_by(desc(Subtask.created_at), desc(Subtask.id)).limit(limit))
    if expert_id:
        q = q.where(Subtask.expert_id == expert_id)
    if status_:
        q = q.where(Subtask.status == status_)
    rows = (await session.execute(q)).all()

    out = []
    for st, d in rows:
        started, finished = utc(st.created_at), utc(st.finished_at)
        item = SubtaskAdminOut(
            id=st.id, device_id=st.device_id,
            machine_name=(d.machine_name if d else "") or st.machine_name,
            user_name=(d.user_name if d else "") or st.user_name, owner=d.owner if d else "",
            conversation_id=st.conversation_id, run_id=st.run_id, seq=st.seq,
            expert_id=st.expert_id, expert_name=st.expert_name, expert_version=st.expert_version or 0,
            published_version=st.published_version or 0, task=st.task, status=st.status,
            token_cap=st.token_cap or 0, prompt_tokens=st.prompt_tokens or 0,
            completion_tokens=st.completion_tokens or 0, estimated_calls=st.estimated_calls or 0,
            tool_calls=st.tool_calls or 0, local_tokens=st.local_tokens or 0, summary=st.summary,
            error_code=st.error_code, error=st.error, cancel_reason=st.cancel_reason,
            result_truncated=bool(st.result_truncated), created_at=started, finished_at=finished,
            deadline_at=utc(st.deadline_at),
            duration_ms=int((finished - started).total_seconds() * 1000) if finished and started else None,
        )
        if reader is None:
            item = item.model_copy(update={f: "" for f in _CONTENT_FIELDS})
        out.append(item)
    if reader is not None and out:
        # 看了聊天内容就留一条，和审计记录的参数同一张表
        session.add(AdminAccess(user_id=reader.id, username=reader.username, action="read_subtasks",
                                target=f"subtasks:{len(out)}条"[:200]))
        await session.commit()
    return out
