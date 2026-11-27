"""
OpenTelemetry 导出的后台设置（docs/observability.md）。

- 看：管理员都能看（请求头只显示名字，不显示值）；
- 改、测试：超级管理员（能把每台电脑的调用记录发到外部系统，和改安全策略同一档）。
"""

from __future__ import annotations

import re
import time

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, Field, field_validator
from sqlalchemy.ext.asyncio import AsyncSession

from ..crypto import encrypt
from ..db import get_session
from ..deps import admin_name, require_admin, require_owner
from ..models import AdminUser
from ..services import settings_store, telemetry

router = APIRouter(prefix="/api/v1/admin/observability", tags=["observability"])


class HeaderIn(BaseModel):
    name: str = Field(min_length=1, max_length=100, pattern=r"^[A-Za-z0-9-]+$")
    #: None = 保持原来的值（后台不回显）；"" = 清空
    value: str | None = Field(default=None, max_length=2000)


class ConfigIn(BaseModel):
    enabled: bool = False
    endpoint: str = Field(default="", max_length=500)
    headers: list[HeaderIn] = Field(default_factory=list, max_length=10)
    service_name: str = Field(default="flyknitbuddy", min_length=1, max_length=100)
    include_identity: bool = False
    timeout_ms: int = Field(default=5000, ge=1000, le=30000)

    @field_validator("endpoint")
    @classmethod
    def _endpoint(cls, v: str) -> str:
        v = v.strip()
        if v and not re.match(r"^https?://[^\s/]+", v):
            raise ValueError("地址要以 http:// 或 https:// 开头")
        return v


def _out(config: dict) -> dict:
    return {
        "enabled": bool(config.get("enabled")), "endpoint": config.get("endpoint", ""),
        "headers": [{"name": h.get("name", ""), "has_value": bool(h.get("value_enc"))} for h in config.get("headers") or []],
        "service_name": config.get("service_name") or "flyknitbuddy", "include_identity": bool(config.get("include_identity")),
        "timeout_ms": int(config.get("timeout_ms") or 5000), "traces_url": telemetry.traces_url(config["endpoint"]) if config.get("endpoint") else "",
        "stats": telemetry.stats(),
    }


@router.get("", dependencies=[Depends(require_admin)])
async def get_config(session: AsyncSession = Depends(get_session)):
    return _out(await telemetry.load(session))


@router.put("")
async def save_config(data: ConfigIn, _: AdminUser | None = Depends(require_owner), who: str = Depends(admin_name),
                      session: AsyncSession = Depends(get_session)):
    if data.enabled and not data.endpoint:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "打开之前请先填采集器地址")
    old = {h.get("name", "").lower(): h for h in (await telemetry.load(session)).get("headers") or []}
    headers = []
    for h in data.headers:
        if h.value is None:
            kept = old.get(h.name.lower())
            headers.append({"name": h.name, "value_enc": kept.get("value_enc", "") if kept else ""})
        else:
            headers.append({"name": h.name, "value_enc": encrypt(h.value) if h.value else ""})
    value = {**data.model_dump(exclude={"headers"}), "headers": headers, "updated_by": who}
    await settings_store.set_value(session, telemetry.KEY, value)
    return _out(await telemetry.load(session))


@router.post("/test")
async def test(_: AdminUser | None = Depends(require_owner), session: AsyncSession = Depends(get_session)):
    """按保存的设置发一条测试 span（不管总开关），看采集器收不收得到。"""
    config = await telemetry.load(session)
    if not config.get("endpoint"):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "还没有填采集器地址")
    now = time.time_ns()
    span = {"traceId": telemetry._trace_id(""), "spanId": "0123456789abcdef", "name": "flyknitbuddy test", "kind": 1,
            "startTimeUnixNano": str(now), "endTimeUnixNano": str(now), "attributes": [telemetry._attr("flyknit.test", True)],
            "status": {"code": 1}}
    started = time.monotonic()
    error = await telemetry.send(config, [span])
    return {"ok": not error, "error": error, "latency_ms": int((time.monotonic() - started) * 1000)}
