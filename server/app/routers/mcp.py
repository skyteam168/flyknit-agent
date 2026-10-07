"""
MCP 连接器：管理员在后台引入厂商，员工端列出来、点「连接」就能用。

和技能库完全分开：自己的表（mcp_vendors）、自己的接口前缀（/admin/mcp、/client/mcp）。
"""

import json
import logging

from fastapi import APIRouter, Depends, HTTPException, Request, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..crypto import decrypt, encrypt, mask
from ..db import get_session
from ..deps import require_admin, require_device
from ..models import Device, McpVendor
from ..schemas import (
    McpClientVendorOut,
    McpDraftOut,
    McpImportIn,
    McpTestIn,
    McpTestOut,
    McpVendorIn,
    McpVendorOut,
    McpVendorPatch,
)
from ..services import config_events, mcp_catalog, mcp_link

log = logging.getLogger("flyknit.mcp")

admin_router = APIRouter(prefix="/api/v1/admin/mcp", tags=["mcp"], dependencies=[Depends(require_admin)])
client_router = APIRouter(prefix="/api/v1/client/mcp", tags=["mcp"])


def _preset(v: McpVendor) -> dict[str, str]:
    if not v.preset_enc:
        return {}
    try:
        data = json.loads(decrypt(v.preset_enc))
    except ValueError:
        log.warning("MCP 连接器 %s 的预填值解不开（FLYKNIT_SECRET_KEY 改过？）", v.id)
        return {}
    return {str(k): str(val) for k, val in data.items()} if isinstance(data, dict) else {}


def _admin_out(v: McpVendor) -> McpVendorOut:
    return McpVendorOut(
        id=v.id, name=v.name, description=v.description, detail=v.detail, icon=v.icon,
        publisher=v.publisher, category=v.category, homepage=v.homepage,
        transport=v.transport, url=v.url, command=v.command, args=v.args or [], env=v.env or {},
        headers=v.headers or {}, auth=v.auth, fields=v.fields or [],
        preset_masked={k: mask(val) for k, val in _preset(v).items() if val},
        oauth=v.oauth or {}, examples=v.examples or [], timeout_ms=v.timeout_ms,
        sort_order=v.sort_order, enabled=v.enabled, updated_at=v.updated_at,
    )


def _client_out(v: McpVendor) -> McpClientVendorOut:
    preset = _preset(v)
    fields = v.fields or []
    return McpClientVendorOut(
        id=v.id, name=v.name, description=v.description, detail=v.detail, icon=v.icon,
        publisher=v.publisher, category=v.category, homepage=v.homepage,
        transport=v.transport, url=v.url, command=v.command, args=v.args or [], env=v.env or {},
        headers=v.headers or {}, auth=v.auth, fields=fields, preset=preset,
        needs_input=mcp_catalog.needs_input(fields, preset),
        oauth=v.oauth or {}, examples=v.examples or [], timeout_ms=v.timeout_ms, updated_at=v.updated_at,
    )


def _check(data: McpVendorIn) -> None:
    try:
        mcp_catalog.validate(data.model_dump())
    except mcp_catalog.McpConfigError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc


def _apply(v: McpVendor, data: McpVendorIn) -> None:
    for name in ("name", "description", "detail", "icon", "publisher", "category", "homepage", "transport",
                 "url", "command", "args", "env", "headers", "auth", "oauth", "examples", "timeout_ms",
                 "sort_order", "enabled"):
        setattr(v, name, getattr(data, name))
    v.url = v.url.strip()
    v.command = v.command.strip()
    v.examples = [e.strip() for e in data.examples if e.strip()][:8]
    v.fields = [f.model_dump() for f in data.fields]
    if data.preset is not None:
        merged = _preset(v)
        for key, value in data.preset.items():
            if value:
                merged[key] = value
            else:
                merged.pop(key, None)
        # 删掉了的填写项，对应的预填值也一起扔掉
        keys = {f.key for f in data.fields}
        merged = {k: val for k, val in merged.items() if k in keys}
        v.preset_enc = encrypt(json.dumps(merged, ensure_ascii=False)) if merged else ""


# ---------- 管理端 ----------
@admin_router.get("/vendors", response_model=list[McpVendorOut])
async def list_vendors(session: AsyncSession = Depends(get_session)):
    rows = await session.scalars(select(McpVendor).order_by(McpVendor.sort_order, McpVendor.name))
    return [_admin_out(v) for v in rows.all()]


@admin_router.post("/vendors", response_model=McpVendorOut, status_code=201)
async def create_vendor(data: McpVendorIn, session: AsyncSession = Depends(get_session)):
    _check(data)
    if await session.get(McpVendor, data.id) is not None:
        raise HTTPException(status.HTTP_409_CONFLICT, f"标识 {data.id} 已经被占用了，换一个")
    v = McpVendor(id=data.id, preset_enc="")
    _apply(v, data)
    session.add(v)
    await session.commit()
    config_events.bump()
    return _admin_out(v)


@admin_router.put("/vendors/{vendor_id}", response_model=McpVendorOut)
async def update_vendor(vendor_id: str, data: McpVendorIn, session: AsyncSession = Depends(get_session)):
    v = await session.get(McpVendor, vendor_id)
    if v is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "连接器不存在")
    if data.id != vendor_id:
        # 标识是员工端工具名的一部分，改了等于换了一个连接器，已连上的会全部断掉
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "标识创建后不能修改")
    _check(data)
    _apply(v, data)
    await session.commit()
    config_events.bump()
    return _admin_out(v)


@admin_router.patch("/vendors/{vendor_id}", response_model=McpVendorOut)
async def patch_vendor(vendor_id: str, data: McpVendorPatch, session: AsyncSession = Depends(get_session)):
    v = await session.get(McpVendor, vendor_id)
    if v is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "连接器不存在")
    if data.enabled is not None:
        v.enabled = data.enabled
    if data.sort_order is not None:
        v.sort_order = data.sort_order
    await session.commit()
    config_events.bump()
    return _admin_out(v)


@admin_router.delete("/vendors/{vendor_id}", status_code=204)
async def delete_vendor(vendor_id: str, session: AsyncSession = Depends(get_session)):
    v = await session.get(McpVendor, vendor_id)
    if v is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "连接器不存在")
    await session.delete(v)
    await session.commit()
    config_events.bump()


@admin_router.post("/parse", response_model=list[McpDraftOut])
async def parse_config(data: McpImportIn, request: Request, session: AsyncSession = Depends(get_session)):
    """
    把厂商给的东西整理成草稿：一段配置、一行 claude mcp add 命令，或者一个链接
    （MCP 地址、介绍页、GitHub 仓库、官方注册表里的服务名）。管理员在表单里确认后再保存。
    """
    try:
        found = await mcp_link.resolve(request.app.state.http, data.text)
    except mcp_catalog.McpConfigError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc
    out = []
    for item in found:
        d = item.draft
        out.append(McpDraftOut(
            id=d.id, name=d.name, transport=d.transport, url=d.url, command=d.command, args=d.args,
            env=d.env, headers=d.headers, auth=d.auth, fields=d.fields, preset=d.preset,
            exists=await session.get(McpVendor, d.id) is not None,
            description=item.description, homepage=item.homepage, icon=item.icon, source=item.source, notes=item.notes,
        ))
    return out


@admin_router.post("/vendors/{vendor_id}/test", response_model=McpTestOut)
async def test_vendor(vendor_id: str, data: McpTestIn, request: Request, session: AsyncSession = Depends(get_session)):
    """从服务器上试连一次：能不能连上、有哪些工具。"""
    v = await session.get(McpVendor, vendor_id)
    if v is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "连接器不存在")
    if v.transport == "stdio":
        return McpTestOut(ok=False, error="本地进程类型在员工电脑上运行，服务器上没法测试，请在员工端点「连接」试一下")
    if v.transport == "sse":
        return McpTestOut(ok=False, error="旧版 SSE 连接只能在员工端测试")
    if v.auth == "oauth":
        return McpTestOut(ok=False, error="浏览器授权登录的服务只能在员工端连接时测试")
    values = {**_preset(v), **{k: val for k, val in data.values.items() if val}}
    missing = mcp_catalog.needs_input(v.fields or [], values)
    if missing:
        return McpTestOut(ok=False, error=f"先填上 {', '.join(missing)} 才能测试")
    url = mcp_catalog.expand(v.url, values)
    headers = {k: mcp_catalog.expand(val, values) for k, val in (v.headers or {}).items()}
    result = await mcp_catalog.probe(request.app.state.http, url, headers)
    return McpTestOut(**result.__dict__)


# ---------- 员工端 ----------
@client_router.get("/vendors", response_model=list[McpClientVendorOut])
async def client_vendors(_: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """上架的连接器。员工端据此画卡片；不在这个列表里的连接（被下架或删掉）员工端会自己断开。"""
    rows = await session.scalars(
        select(McpVendor).where(McpVendor.enabled.is_(True)).order_by(McpVendor.sort_order, McpVendor.name)
    )
    return [_client_out(v) for v in rows.all()]
