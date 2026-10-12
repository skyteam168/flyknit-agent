"""
专家：管理员在后台建好、发布，员工端在「专家」里看到、点一下就能呼叫。

改的是草稿，员工端不受影响；点「发布」才生效（版本号 +1，历史可回滚）；「下线」后员工端不再显示。
"""

import logging
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, Request, status
from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import admin_name, require_admin, require_device, require_owner
from ..models import AdminUser, Device, Expert, ExpertVersion
from ..schemas import (
    ExpertClientOut,
    ExpertDelegationIn,
    ExpertDelegationOut,
    ExpertDraftIn,
    ExpertDraftOut,
    ExpertFields,
    ExpertIn,
    ExpertOptionsOut,
    ExpertOut,
    ExpertPublishIn,
    ExpertVersionOut,
)
from ..services import config_events, expert_registry, security_settings, settings_store

log = logging.getLogger("flyknit.experts")
admin_router = APIRouter(prefix="/api/v1/admin/experts", tags=["experts"], dependencies=[Depends(require_admin)])
client_router = APIRouter(prefix="/api/v1/client/experts", tags=["experts"])


def _out(e: Expert, usage: dict[str, tuple[int, int]] | None = None) -> ExpertOut:
    uses, users = (usage or {}).get(e.id, (0, 0))
    return ExpertOut(
        id=e.id, status=e.status, version=e.version, dirty=expert_registry.is_dirty(e), uses=uses, users=users,
        published_at=e.published_at, published_by=e.published_by, updated_by=e.updated_by, updated_at=e.updated_at,
        **expert_registry.snapshot(e),
    )


async def _get(session: AsyncSession, expert_id: str) -> Expert:
    e = await session.get(Expert, expert_id)
    if e is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "专家不存在")
    return e


# ---------- 管理端 ----------
@admin_router.get("", response_model=list[ExpertOut])
async def list_experts(session: AsyncSession = Depends(get_session)):
    rows = await session.scalars(select(Expert).order_by(Expert.sort_order, Expert.created_at))
    usage = await expert_registry.usage_totals(session)
    return [_out(e, usage) for e in rows.all()]


@admin_router.get("/options", response_model=ExpertOptionsOut)
async def expert_options(session: AsyncSession = Depends(get_session)):
    return await expert_registry.options(session)


# 「派遣专家」开关放在专家页上，存的是安全中心里的 expert_delegation（全厂默认）。
# 要写在 /{expert_id} 那几个接口前面，否则 PUT /delegation 会被当成改一位叫 delegation 的专家
@admin_router.get("/delegation", response_model=ExpertDelegationOut)
async def get_delegation(session: AsyncSession = Depends(get_session)):
    shared = await settings_store.get_security(session)
    merged = security_settings.effective(shared.get("values"), None, shared.get("locks"), None)
    return ExpertDelegationOut(enabled=bool(merged["expert_delegation"]["value"]))


@admin_router.put("/delegation", response_model=ExpertDelegationOut)
async def set_delegation(data: ExpertDelegationIn, owner: AdminUser | None = Depends(require_owner),
                         session: AsyncSession = Depends(get_session)):
    """开关派遣。限超级管理员——和安全中心的其它全厂开关一样。写进安全设置，在线的员工端马上来拉。"""
    shared = await settings_store.get_security(session)
    values = {**(shared.get("values") or {}), "expert_delegation": data.enabled}
    await settings_store.set_security(session, values, shared.get("locks") or {})
    log.warning("%s %s了「派遣专家」", owner.username if owner else "admin_token", "开启" if data.enabled else "关闭")
    return await get_delegation(session)


@admin_router.post("/draft", response_model=ExpertDraftOut)
async def draft_expert(data: ExpertDraftIn, request: Request, session: AsyncSession = Depends(get_session)):
    """一句话描述想要什么专家，AI 按现有的技能、连接器起草一份。管理员在表单里确认后再保存。"""
    try:
        result = await expert_registry.draft(session, request.app.state.http, data.text)
    except expert_registry.DraftError as exc:
        raise HTTPException(status.HTTP_502_BAD_GATEWAY, str(exc)) from exc
    if result["id"] and await session.get(Expert, result["id"]) is not None:
        result["notes"].append(f"标识 {result['id']} 已经被占用了，保存前换一个")
    return result


@admin_router.post("", response_model=ExpertOut, status_code=201)
async def create_expert(data: ExpertIn, session: AsyncSession = Depends(get_session), who: str = Depends(admin_name)):
    if await session.get(Expert, data.id) is not None:
        raise HTTPException(status.HTTP_409_CONFLICT, f"标识 {data.id} 已经被占用了，换一个")
    e = Expert(id=data.id, status="draft", version=0, published={}, updated_by=who)
    expert_registry.apply(e, data)
    session.add(e)
    await session.commit()
    return _out(e)


@admin_router.put("/{expert_id}", response_model=ExpertOut)
async def update_expert(expert_id: str, data: ExpertFields, session: AsyncSession = Depends(get_session),
                        who: str = Depends(admin_name)):
    """改草稿。员工端看到的是已发布的版本，要点「发布」才生效。"""
    e = await _get(session, expert_id)
    expert_registry.apply(e, data)
    e.updated_by = who
    e.updated_at = datetime.now(timezone.utc)
    await session.commit()
    return _out(e)


@admin_router.post("/{expert_id}/publish", response_model=ExpertOut)
async def publish_expert(expert_id: str, data: ExpertPublishIn, session: AsyncSession = Depends(get_session),
                         who: str = Depends(admin_name)):
    e = await _get(session, expert_id)
    if not e.instructions.strip():
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "先写好「工作方式」再发布")
    snap = expert_registry.snapshot(e)
    now = datetime.now(timezone.utc)
    e.version += 1
    e.published = snap
    e.published_at = now
    e.published_by = who
    e.status = "published"
    session.add(ExpertVersion(expert_id=e.id, version=e.version, snapshot=snap, note=data.note.strip(),
                              published_by=who, published_at=now))
    await session.commit()
    config_events.bump()
    return _out(e)


@admin_router.post("/{expert_id}/offline", response_model=ExpertOut)
async def offline_expert(expert_id: str, session: AsyncSession = Depends(get_session)):
    """下线：员工端不再显示。草稿和历史版本都还在，重新发布就回来了。"""
    e = await _get(session, expert_id)
    if e.status == "published":
        e.status = "offline"
        await session.commit()
        config_events.bump()
    return _out(e)


@admin_router.post("/{expert_id}/discard", response_model=ExpertOut)
async def discard_changes(expert_id: str, session: AsyncSession = Depends(get_session)):
    """放弃没发布的修改：草稿恢复成已发布的样子。"""
    e = await _get(session, expert_id)
    if not e.version:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "还没有发布过，没有可以恢复的版本")
    expert_registry.apply(e, expert_registry.from_snapshot(e.published))
    await session.commit()
    return _out(e)


@admin_router.get("/{expert_id}/versions", response_model=list[ExpertVersionOut])
async def list_versions(expert_id: str, session: AsyncSession = Depends(get_session)):
    await _get(session, expert_id)
    rows = await session.scalars(
        select(ExpertVersion).where(ExpertVersion.expert_id == expert_id).order_by(ExpertVersion.version.desc()))
    return [ExpertVersionOut(version=v.version, note=v.note, published_by=v.published_by,
                             published_at=v.published_at, snapshot=v.snapshot) for v in rows.all()]


@admin_router.post("/{expert_id}/versions/{version}/restore", response_model=ExpertOut)
async def restore_version(expert_id: str, version: int, session: AsyncSession = Depends(get_session),
                          who: str = Depends(admin_name)):
    """把某个历史版本的内容放回草稿。确认无误后再点「发布」，员工端才会换成它。"""
    e = await _get(session, expert_id)
    v = await session.scalar(
        select(ExpertVersion).where(ExpertVersion.expert_id == expert_id, ExpertVersion.version == version))
    if v is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个版本")
    expert_registry.apply(e, expert_registry.from_snapshot(v.snapshot))
    e.updated_by = who
    e.updated_at = datetime.now(timezone.utc)
    await session.commit()
    return _out(e)


@admin_router.delete("/{expert_id}", status_code=204)
async def delete_expert(expert_id: str, session: AsyncSession = Depends(get_session)):
    e = await _get(session, expert_id)
    was_live = e.status == "published"
    await session.execute(delete(ExpertVersion).where(ExpertVersion.expert_id == expert_id))
    await session.delete(e)
    await session.commit()
    if was_live:
        config_events.bump()


# ---------- 员工端 ----------
@client_router.get("", response_model=list[ExpertClientOut])
async def client_experts(_: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """已发布的专家（发布时的快照）。不在这个列表里的（下线、删除）员工端自己收起来。"""
    rows = await session.scalars(
        select(Expert).where(Expert.status == "published").order_by(Expert.sort_order, Expert.created_at))
    usage = await expert_registry.usage_totals(session)
    out = []
    for e in rows.all():
        snap = expert_registry.from_snapshot(e.published)
        uses, users = usage.get(e.id, (0, 0))
        out.append(ExpertClientOut(id=e.id, version=e.version, published_at=e.published_at, uses=uses, users=users,
                                   **snap.model_dump()))
    return out


@client_router.post("/{expert_id}/use", status_code=204)
async def use_expert(expert_id: str, device: Device = Depends(require_device),
                     session: AsyncSession = Depends(get_session)):
    """员工向专家发了一句话（员工端每轮对话报一次），用来算「使用量」。下线、删掉的专家不记。"""
    e = await session.get(Expert, expert_id)
    if e is None or e.status != "published":
        raise HTTPException(status.HTTP_404_NOT_FOUND, "专家不存在或已下线")
    await expert_registry.record_use(session, expert_id, device.id)
