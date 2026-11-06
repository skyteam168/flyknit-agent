"""插件包：IT 上传 / 从链接导入一个插件（目录结构同 Claude Code plugins），按部门一键下发。

安装 = 把插件里的东西落进已有的几套机制，来源列（plugin）记上插件 id：
- skills/   → 公司技能库（skill_packages）
- agents/   → 专家（experts，直接发布）
- .mcp.json → 连接器（mcp_vendors；密钥不随包走，装好后管理员在「连接器」里填）
- commands/ → 快捷指令（存在插件清单里，/client/commands 合并下发）
- rules.json → 工具规则（存在插件清单里，/client/config 的 tool_rules 合并下发）

再上传同一个插件 = 升级：新版里没有了的东西删掉，同名的更新（连接器的预填值保留）。
不会覆盖管理员手工建的、或别的插件带来的同名东西——有冲突直接拒绝，列出是哪几个。
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path

from fastapi import APIRouter, Depends, File, HTTPException, Query, Request, UploadFile, status
from pydantic import BaseModel, Field, ValidationError
from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..config import get_settings
from ..db import get_session
from ..deps import admin_name, require_admin, require_device
from ..models import Device, Expert, ExpertVersion, McpVendor, Plugin, SkillPackage
from ..schemas import ExpertFields
from ..services import config_events, expert_registry, plugin_package, plugin_scope, skill_library
from . import commands as commands_router
from . import tool_rules as tool_rules_router

admin_router = APIRouter(prefix="/api/v1/admin/plugins", tags=["plugins"], dependencies=[Depends(require_admin)])
client_router = APIRouter(prefix="/api/v1/client/plugins", tags=["plugins"])


class PluginImportIn(BaseModel):
    url: str = Field(min_length=1, max_length=1000)
    departments: list[str] = Field(default_factory=list, max_length=100)
    required: bool = False


class PluginPatch(BaseModel):
    enabled: bool | None = None
    required: bool | None = None
    departments: list[str] | None = Field(default=None, max_length=100)


def _skill_dir() -> Path:
    return skill_library.storage_dir(Path(get_settings().data_dir))


def _depts(items: list[str]) -> list[str]:
    out: list[str] = []
    for d in items:
        d = d.strip()[:200]
        if d and d not in out:
            out.append(d)
    return out


def _counts(manifest: dict) -> dict:
    return {k: len(manifest.get(k) or []) for k in ("skills", "commands", "experts", "connectors", "rules")}


def _out(p: Plugin) -> dict:
    return {
        "id": p.id, "name": p.name, "version": p.version, "description": p.description, "author": p.author,
        "homepage": p.homepage, "origin": p.origin, "manifest": p.manifest or {}, "counts": _counts(p.manifest or {}),
        "departments": p.departments or [], "required": p.required, "enabled": p.enabled,
        "installed_by": p.installed_by, "updated_at": p.updated_at,
    }


def _check_rules(parsed: plugin_package.ParsedPlugin) -> list[dict]:
    """插件里的工具规则和后台手写的走同一套校验（正则不合法、动作不认识都拒收）。"""
    out = []
    for i, r in enumerate(parsed.rules[: tool_rules_router.MAX_RULES], 1):
        try:
            out.append(tool_rules_router.ToolRule(**r).model_dump())
        except ValidationError as exc:
            msg = exc.errors()[0].get("msg", "") if exc.errors() else str(exc)
            raise HTTPException(status.HTTP_400_BAD_REQUEST, f"rules.json 第 {i} 条规则不合法：{msg}") from exc
    return out


def _check_commands(parsed: plugin_package.ParsedPlugin) -> list[dict]:
    out, seen = [], set()
    for c in parsed.commands[: commands_router.MAX_COMMANDS]:
        try:
            item = commands_router.CommandItem(**c).model_dump()
        except ValidationError as exc:
            msg = exc.errors()[0].get("msg", "") if exc.errors() else str(exc)
            parsed.warnings.append(f"忽略了快捷指令 /{c.get('name')}：{msg}")
            continue
        if item["name"].lower() in seen:
            continue
        seen.add(item["name"].lower())
        out.append(item)
    return out


async def _conflicts(session: AsyncSession, parsed: plugin_package.ParsedPlugin) -> list[str]:
    """同名的东西已经存在、又不是这个插件自己装的。"""
    found = []
    for s in parsed.skills:
        row = await session.get(SkillPackage, s.name)
        if row is not None and (row.plugin or "") != parsed.id:
            found.append(f"技能 {s.name}")
    for e in parsed.experts:
        row = await session.get(Expert, e["id"])
        if row is not None and (row.plugin or "") != parsed.id:
            found.append(f"专家 {e['id']}")
    for c in parsed.connectors:
        row = await session.get(McpVendor, c["id"])
        if row is not None and (row.plugin or "") != parsed.id:
            found.append(f"连接器 {c['id']}")
    return found


def _parse(content: bytes, subdir: str = "") -> plugin_package.ParsedPlugin:
    if len(content) > plugin_package.MAX_ZIP_BYTES:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "文件过大")
    try:
        return plugin_package.parse(content, subdir)
    except plugin_package.PluginError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc


async def _remove_parts(session: AsyncSession, plugin_id: str, keep: dict[str, set[str]] | None = None) -> None:
    """删掉这个插件装进来的东西（keep 里列的保留：升级时用）。"""
    keep = keep or {}
    for row in (await session.scalars(select(SkillPackage).where(SkillPackage.plugin == plugin_id))).all():
        if row.name not in keep.get("skills", set()):
            await session.delete(row)
            (_skill_dir() / f"{row.name}.zip").unlink(missing_ok=True)
    for row in (await session.scalars(select(Expert).where(Expert.plugin == plugin_id))).all():
        if row.id not in keep.get("experts", set()):
            await session.execute(delete(ExpertVersion).where(ExpertVersion.expert_id == row.id))
            await session.delete(row)
    for row in (await session.scalars(select(McpVendor).where(McpVendor.plugin == plugin_id))).all():
        if row.id not in keep.get("connectors", set()):
            await session.delete(row)
    await session.flush()


async def install(session: AsyncSession, parsed: plugin_package.ParsedPlugin, origin: str, who: str,
                  departments: list[str], required: bool) -> Plugin:
    conflicts = await _conflicts(session, parsed)
    if conflicts:
        raise HTTPException(status.HTTP_409_CONFLICT,
                            f"这些东西已经存在、不是这个插件装的：{'、'.join(conflicts)}。先改名或删掉再装")
    rules = _check_rules(parsed)
    commands = _check_commands(parsed)

    existing = await session.get(Plugin, parsed.id)
    await _remove_parts(session, parsed.id, keep={
        "skills": {s.name for s in parsed.skills},
        "experts": {e["id"] for e in parsed.experts},
        "connectors": {c["id"] for c in parsed.connectors},
    })
    plugin = existing or Plugin(id=parsed.id)
    plugin.name, plugin.version, plugin.description = parsed.name, parsed.version, parsed.description
    plugin.author, plugin.homepage, plugin.origin = parsed.author, parsed.homepage, origin[:500]
    plugin.departments = _depts(departments)
    plugin.required = required
    plugin.installed_by = who
    if existing is None:
        plugin.enabled = True
        session.add(plugin)

    for s in parsed.skills:
        (_skill_dir() / f"{s.name}.zip").write_bytes(s.data)
        row = await session.get(SkillPackage, s.name) or SkillPackage(name=s.name)
        row.description, row.version, row.author = s.description, s.version or parsed.version, s.author or parsed.author
        row.origin = f"插件 {parsed.name}"[:500]
        row.size, row.file_count = len(s.data), len(s.files)
        row.required, row.enabled, row.plugin = required, True, parsed.id
        session.add(row)

    now = datetime.now(timezone.utc)
    for e in parsed.experts:
        row = await session.get(Expert, e["id"])
        if row is None:
            row = Expert(id=e["id"], name=e["fields"]["name"], version=0)
            session.add(row)
        expert_registry.apply(row, ExpertFields(**e["fields"]))
        await session.flush()
        snap = expert_registry.snapshot(row)
        # 插件里的专家装好就发布：内容来自插件包，后台不会单独改草稿再发布
        row.version = (row.version or 0) + 1
        row.published, row.published_at, row.published_by = snap, now, who
        row.status, row.plugin, row.updated_by = "published", parsed.id, who
        session.add(ExpertVersion(expert_id=row.id, version=row.version, snapshot=snap,
                                  note=f"插件 {parsed.name} {parsed.version}".strip()[:300], published_by=who,
                                  published_at=now))

    for c in parsed.connectors:
        row = await session.get(McpVendor, c["id"])
        if row is None:
            row = McpVendor(id=c["id"], name=c["name"], enabled=True)
            session.add(row)
        row.name, row.transport, row.url, row.command = c["name"][:100], c["transport"], c["url"], c["command"]
        row.args, row.env, row.headers = c["args"], c["env"], c["headers"]
        row.auth, row.fields = c["auth"], c["fields"]
        row.publisher, row.category = parsed.author[:100], parsed.name[:50]
        row.description = row.description or parsed.description[:300]
        row.plugin = parsed.id
        # 管理员已经填好的预填值保留；连接器自己的 _apply 也是这么处理删掉的填写项（留下还在的）
        # preset_enc 是加密的 JSON，字段对不上时员工连接会提示要填，不需要在这里解开

    manifest = parsed.summary()
    manifest["commands"], manifest["rules"] = commands, rules
    plugin.manifest = manifest
    await session.commit()
    config_events.bump()
    await session.refresh(plugin)
    return plugin


# ---------- 管理端 ----------
@admin_router.get("")
async def list_plugins(session: AsyncSession = Depends(get_session)):
    rows = (await session.scalars(select(Plugin).order_by(Plugin.name))).all()
    depts = sorted({d.strip() for d in (await session.scalars(select(Device.department))).all() if d and d.strip()})
    return {"items": [_out(p) for p in rows], "departments": depts}


@admin_router.post("/preview")
async def preview(file: UploadFile = File(...), session: AsyncSession = Depends(get_session)):
    """只解析不安装：看看包里有什么、会不会和已有的东西冲突。"""
    parsed = _parse(await file.read())
    rules = _check_rules(parsed)
    commands = _check_commands(parsed)
    summary = parsed.summary()
    summary["commands"], summary["rules"] = commands, rules
    existing = await session.get(Plugin, parsed.id)
    return {
        "id": parsed.id, "name": parsed.name, "version": parsed.version, "description": parsed.description,
        "author": parsed.author, "manifest": summary, "counts": _counts(summary),
        "conflicts": await _conflicts(session, parsed),
        "upgrade_from": existing.version if existing else None,
    }


@admin_router.post("/upload")
async def upload(file: UploadFile = File(...), departments: list[str] = Query(default=[]),
                 required: bool = False, who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    """上传插件 zip 安装（再传同一个插件就是升级）。"""
    parsed = _parse(await file.read())
    return _out(await install(session, parsed, file.filename or "上传", who, departments, required))


@admin_router.post("/import")
async def import_url(data: PluginImportIn, request: Request, who: str = Depends(admin_name),
                     session: AsyncSession = Depends(get_session)):
    """从 GitHub 链接（可以指到 marketplace 仓库里的某个插件目录）或 zip 下载链接导入。"""
    try:
        content = await skill_library.download(request.app.state.http, data.url)
    except skill_library.SkillError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc
    parsed = _parse(content, skill_library.github_subdir(data.url))
    return _out(await install(session, parsed, data.url, who, data.departments, data.required))


@admin_router.patch("/{plugin_id}")
async def patch(plugin_id: str, data: PluginPatch, session: AsyncSession = Depends(get_session)):
    p = await session.get(Plugin, plugin_id)
    if p is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "插件不存在")
    if data.enabled is not None:
        p.enabled = data.enabled
    if data.departments is not None:
        p.departments = _depts(data.departments)
    if data.required is not None:
        p.required = data.required
        for row in (await session.scalars(select(SkillPackage).where(SkillPackage.plugin == plugin_id))).all():
            row.required = data.required
    await session.commit()
    config_events.bump()
    await session.refresh(p)
    return _out(p)


@admin_router.delete("/{plugin_id}", status_code=204)
async def uninstall(plugin_id: str, session: AsyncSession = Depends(get_session)):
    """卸载：插件带来的技能、专家、连接器一起删掉；员工端下次拉配置时自己收起来。"""
    p = await session.get(Plugin, plugin_id)
    if p is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "插件不存在")
    await _remove_parts(session, plugin_id)
    await session.delete(p)
    await session.commit()
    config_events.bump()


# ---------- 员工端 ----------
@client_router.get("")
async def client_plugins(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """这台电脑范围内的插件（只读展示用；内容本身走技能 / 专家 / 连接器 / 快捷指令 / 配置各自的接口）。"""
    out = []
    for p in await plugin_scope.visible(session, device):
        m = p.manifest or {}
        out.append({
            "id": p.id, "name": p.name, "version": p.version, "description": p.description, "author": p.author,
            "required": p.required, "counts": _counts(m),
            "skills": [s["name"] for s in m.get("skills", [])],
            "commands": [c["name"] for c in m.get("commands", [])],
            "experts": [e["name"] for e in m.get("experts", [])],
            "connectors": [c["name"] for c in m.get("connectors", [])],
            "updated_at": p.updated_at,
        })
    return {"items": out}
