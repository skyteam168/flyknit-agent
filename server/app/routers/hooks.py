"""
钩子（docs/hooks.md）。

后台：
- 看：管理员都能看钩子、总开关、最近的调用记录（不含密钥）；
- 建、改、删、测试、总开关：超级管理员（钩子能收到每个员工调用工具的参数，和改安全策略同一档）。

员工端：
- POST /api/v1/client/hooks/check：执行一个工具前问一下（pre_tool），服务端按自己的配置决定问哪些钩子；
- POST /api/v1/client/hooks/notify：工具做完（post_tool）、一轮结束（stop），后台发，马上返回。
"""

from __future__ import annotations

import asyncio
import re

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, Field, field_validator
from sqlalchemy import desc, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..crypto import encrypt
from ..db import get_session, get_sessionmaker
from ..deps import require_admin, require_device, require_owner
from ..models import AdminUser, Device, Hook, HookCall
from ..services import config_events, hook_service, settings_store

admin_router = APIRouter(prefix="/api/v1/admin/hooks", tags=["hooks"])
client_router = APIRouter(prefix="/api/v1/client/hooks", tags=["hooks"])

MAX_HOOKS = 50
MAX_RECIPIENTS = 200
_background: set[asyncio.Task] = set()


def _events(v: list[str]) -> list[str]:
    out = [e for e in hook_service.EVENTS if e in set(v)]
    if not out:
        raise ValueError("至少选一个事件（调用工具前 / 工具做完 / 任务结束）")
    return out


def _url(v: str) -> str:
    v = v.strip()
    if not re.match(r"^https?://[^\s/]+", v):
        raise ValueError("地址要以 http:// 或 https:// 开头")
    return v


def _regex(v: str) -> str:
    v = v.strip()
    if v:
        try:
            re.compile(v)
        except re.error as e:
            raise ValueError(f"参数匹配不是合法的正则表达式：{e}") from e
    return v


class HookIn(BaseModel):
    name: str = Field(min_length=1, max_length=100)
    events: list[str] = Field(default_factory=lambda: ["pre_tool"])
    tools: str = Field(default="*", max_length=500)
    match: str = Field(default="", max_length=500)
    url: str = Field(min_length=8, max_length=1000)
    #: 空 = 不签名
    secret: str = Field(default="", max_length=200)
    timeout_ms: int = Field(default=3000, ge=500, le=10000)
    on_error: str = Field(default="ask", pattern="^(ask|deny|allow)$")
    skip_tls_verify: bool = False
    enabled: bool = True
    scope_departments: list[str] = Field(default_factory=list, max_length=MAX_RECIPIENTS)
    scope_devices: list[int] = Field(default_factory=list, max_length=MAX_RECIPIENTS)

    @field_validator("events")
    @classmethod
    def _v_events(cls, v: list[str]) -> list[str]:
        return _events(v)

    @field_validator("url")
    @classmethod
    def _v_url(cls, v: str) -> str:
        return _url(v)

    @field_validator("match")
    @classmethod
    def _v_match(cls, v: str) -> str:
        return _regex(v)


class HookPatch(BaseModel):
    name: str | None = Field(default=None, min_length=1, max_length=100)
    events: list[str] | None = None
    tools: str | None = Field(default=None, max_length=500)
    match: str | None = Field(default=None, max_length=500)
    url: str | None = Field(default=None, min_length=8, max_length=1000)
    #: None = 不改；"" = 去掉签名
    secret: str | None = Field(default=None, max_length=200)
    timeout_ms: int | None = Field(default=None, ge=500, le=10000)
    on_error: str | None = Field(default=None, pattern="^(ask|deny|allow)$")
    skip_tls_verify: bool | None = None
    enabled: bool | None = None
    scope_departments: list[str] | None = Field(default=None, max_length=MAX_RECIPIENTS)
    scope_devices: list[int] | None = Field(default=None, max_length=MAX_RECIPIENTS)

    @field_validator("events")
    @classmethod
    def _v_events(cls, v: list[str] | None) -> list[str] | None:
        return v if v is None else _events(v)

    @field_validator("url")
    @classmethod
    def _v_url(cls, v: str | None) -> str | None:
        return v if v is None else _url(v)

    @field_validator("match")
    @classmethod
    def _v_match(cls, v: str | None) -> str | None:
        return v if v is None else _regex(v)


class SwitchIn(BaseModel):
    enabled: bool


class CheckIn(BaseModel):
    tool: str = Field(min_length=1, max_length=100)
    arguments: str = Field(default="", max_length=200_000)
    conversation_id: str = Field(default="", max_length=64)
    run_id: str = Field(default="", max_length=64)
    task_id: str = Field(default="", max_length=64)
    grant_id: str = Field(default="", max_length=64)


class NotifyIn(CheckIn):
    event: str = Field(pattern="^(post_tool|stop)$")
    tool: str = Field(default="", max_length=100)
    ok: bool = True
    summary: str = Field(default="", max_length=5000)
    status: str = Field(default="", max_length=20)


def _out(h: Hook) -> dict:
    return {
        "id": h.id, "name": h.name, "events": list(h.events or []), "tools": h.tools, "match": h.match,
        "url": h.url, "has_secret": bool(h.secret_enc), "timeout_ms": h.timeout_ms, "on_error": h.on_error,
        "skip_tls_verify": h.skip_tls_verify, "enabled": h.enabled,
        "scope_departments": list(h.scope_departments or []), "scope_devices": list(h.scope_devices or []),
        "plugin": h.plugin or "", "created_by": h.created_by, "created_at": h.created_at, "updated_at": h.updated_at,
    }


@admin_router.get("", dependencies=[Depends(require_admin)])
async def list_hooks(session: AsyncSession = Depends(get_session)):
    hooks = (await session.scalars(select(Hook).order_by(Hook.id))).all()
    names = {h.id: h.name for h in hooks}
    calls = (await session.scalars(select(HookCall).order_by(desc(HookCall.id)).limit(100))).all()
    return {
        "enabled": await hook_service.master_on(session),
        "items": [_out(h) for h in hooks],
        "calls": [{"id": c.id, "hook_id": c.hook_id, "hook_name": names.get(c.hook_id or 0, ""), "event": c.event,
                   "device_id": c.device_id, "tool_name": c.tool_name, "decision": c.decision, "reason": c.reason,
                   "error": c.error, "latency_ms": c.latency_ms, "created_at": c.created_at} for c in calls],
    }


@admin_router.put("/switch")
async def set_switch(data: SwitchIn, user: AdminUser | None = Depends(require_owner), session: AsyncSession = Depends(get_session)):
    who = user.username if user else "admin_token"
    await settings_store.set_value(session, hook_service.KEY, {"enabled": data.enabled, "updated_by": who})
    return {"enabled": data.enabled}


@admin_router.post("", status_code=201)
async def create_hook(data: HookIn, user: AdminUser | None = Depends(require_owner), session: AsyncSession = Depends(get_session)):
    if len((await session.scalars(select(Hook.id))).all()) >= MAX_HOOKS:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, f"钩子最多 {MAX_HOOKS} 个")
    values = data.model_dump(exclude={"secret"})
    values["scope_departments"] = [d.strip() for d in data.scope_departments if d.strip()]
    values["scope_devices"] = sorted(set(data.scope_devices))
    hook = Hook(**values, secret_enc=encrypt(data.secret) if data.secret else "", created_by=user.username if user else "admin_token")
    session.add(hook)
    await session.commit()
    config_events.bump()
    return _out(hook)


@admin_router.patch("/{hook_id}")
async def update_hook(hook_id: int, data: HookPatch, _: AdminUser | None = Depends(require_owner),
                      session: AsyncSession = Depends(get_session)):
    hook = await session.get(Hook, hook_id)
    if hook is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个钩子")
    values = data.model_dump(exclude_none=True)
    if "secret" in values:
        secret = values.pop("secret")
        hook.secret_enc = encrypt(secret) if secret else ""
    if "scope_departments" in values:
        values["scope_departments"] = [d.strip() for d in values["scope_departments"] if d.strip()]
    if "scope_devices" in values:
        values["scope_devices"] = sorted(set(values["scope_devices"]))
    for key, value in values.items():
        setattr(hook, key, value)
    await session.commit()
    config_events.bump()
    return _out(hook)


@admin_router.delete("/{hook_id}", status_code=204)
async def delete_hook(hook_id: int, _: AdminUser | None = Depends(require_owner), session: AsyncSession = Depends(get_session)):
    hook = await session.get(Hook, hook_id)
    if hook is not None:
        await session.delete(hook)
        await session.commit()
        config_events.bump()


@admin_router.post("/{hook_id}/test")
async def test_hook(hook_id: int, _: AdminUser | None = Depends(require_owner), session: AsyncSession = Depends(get_session)):
    """发一条示例 pre_tool 请求（带 "test": true），看钩子能不能收到、回答对不对。不算进调用记录。"""
    hook = await session.get(Hook, hook_id)
    if hook is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个钩子")
    sample = {"test": True, "tool": "read_file", "arguments": '{"path": "D:\\\\报表\\\\示例.xlsx"}', "conversation_id": "test"}
    decision, reason, error, latency = await hook_service.send(hook, "pre_tool", hook_service.payload("pre_tool", hook, None, sample))
    return {"ok": decision is not None, "decision": decision, "reason": reason, "error": error, "latency_ms": latency}


@client_router.post("/check")
async def check(data: CheckIn, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    return await hook_service.check(session, device, data.model_dump())


@client_router.post("/notify", status_code=202)
async def notify(data: NotifyIn, device: Device = Depends(require_device)):
    task = asyncio.create_task(hook_service.notify(get_sessionmaker(), device.id, data.event, data.model_dump()))
    _background.add(task)
    task.add_done_callback(_background.discard)
    return {"accepted": True}
