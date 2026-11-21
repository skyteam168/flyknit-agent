"""
数据脱敏（docs/data-protection.md）的管理端接口：看统计、试一段文字。
开关在安全中心（dlp_enabled / dlp_contacts），按全厂 → 部门 → 单台电脑生效。
统计只有数量，没有原值。
"""

from collections import Counter
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, Depends, Query
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_admin
from ..models import Device, DlpEvent
from ..services import dlp

router = APIRouter(prefix="/api/v1/admin/dlp", tags=["dlp"], dependencies=[Depends(require_admin)])


class PreviewIn(BaseModel):
    text: str = Field(max_length=20_000)
    contacts: bool = True


@router.post("/preview")
async def preview(body: PreviewIn):
    """IT 贴一段文字，看看会被换成什么（不发给任何模型，不记录）。"""
    vault = dlp.Vault()
    masked = dlp.mask_text(body.text, vault, dlp.Policy(enabled=True, contacts=body.contacts))
    return {"masked": masked, "counts": dict(vault.counts), "labels": {**dlp.LABELS, dlp.SECRET_LABEL: "密码 / 密钥"}}


@router.get("/summary")
async def summary(days: int = Query(30, ge=1, le=365), session: AsyncSession = Depends(get_session)):
    since = datetime.now(timezone.utc) - timedelta(days=days)
    rows = (await session.scalars(select(DlpEvent).where(DlpEvent.created_at >= since).order_by(DlpEvent.created_at.desc()))).all()
    totals: dict[str, Counter] = {"masked": Counter(), "blocked": Counter()}
    per_device: dict[int, Counter] = {}
    requests = Counter()
    for r in rows:
        totals.setdefault(r.action, Counter()).update(r.counts or {})
        requests[r.action] += 1
        per_device.setdefault(r.device_id, Counter()).update(r.counts or {})
    devices = {d.id: d for d in (await session.scalars(select(Device).where(Device.id.in_(list(per_device))))).all()} if per_device else {}
    top = sorted(per_device.items(), key=lambda kv: -sum(kv[1].values()))[:20]
    return {
        "days": days,
        "labels": {**dlp.LABELS, dlp.SECRET_LABEL: "密码 / 密钥"},
        "requests": dict(requests),
        "totals": {k: dict(v) for k, v in totals.items()},
        "devices": [
            {"device_id": did, "machine_name": devices[did].machine_name if did in devices else str(did),
             "user_name": devices[did].user_name if did in devices else "", "department": devices[did].department if did in devices else "",
             "counts": dict(c), "total": sum(c.values())}
            for did, c in top
        ],
        "recent": [
            {"device_id": r.device_id, "machine_name": devices[r.device_id].machine_name if r.device_id in devices else str(r.device_id),
             "scene": r.scene, "action": r.action, "counts": r.counts, "created_at": r.created_at}
            for r in rows[:50]
        ],
    }
