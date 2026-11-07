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

import io
import zipfile
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
from ..services import config_events, expert_registry, mcp_catalog, plugin_package, plugin_scope, skill_library
from . import commands as commands_router
from . import tool_rules as tool_rules_router

admin_router = APIRouter(prefix="/api/v1/admin/plugins", tags=["plugins"], dependencies=[Depends(require_admin)])
client_router = APIRouter(prefix="/api/v1/client/plugins", tags=["plugins"])


class PluginImportIn(BaseModel):
    url: str = Field(min_length=1, max_length=1000)
    departments: list[str] = Field(default_factory=list, max_length=100)
    required: bool = False
    #: 同名冲突时管理员选的改名：插件里的服务名 → 新的连接器标识
    renames: dict[str, str] = Field(default_factory=dict)


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
    return found


# ---------- 插件里的 MCP 服务 → 连接器 ----------
NEW, UPDATE, REUSE, CONFLICT, RENAMED = "NEW", "UPDATE", "REUSE_EXISTING", "CONFLICT", "RENAMED"


def _vendor_config(v: McpVendor) -> dict:
    """已有连接器的连接方式，和插件定义同一个形状（不含管理员预填的值、介绍文字）。"""
    return {"transport": v.transport, "url": (v.url or "").strip(), "command": (v.command or "").strip(),
            "args": list(v.args or []), "env": dict(sorted((v.env or {}).items())),
            "headers": dict(sorted((v.headers or {}).items()))}


def _def_config(d: dict) -> dict:
    return {"transport": d["transport_type"], "url": d["url"].strip(), "command": d["command"].strip(),
            "args": list(d["args"]), "env": dict(sorted(d["env"].items())),
            "headers": dict(sorted(d["headers"].items()))}


async def _suggest(session: AsyncSession, plugin_id: str, name: str) -> str:
    base = mcp_catalog.slugify(f"{plugin_id}-{name}")[:37]
    candidate, n = base, 2
    while await session.get(McpVendor, candidate) is not None:
        candidate, n = f"{base}-{n}", n + 1
    return candidate


def _parse_renames(items: list[str]) -> dict[str, str]:
    """查询参数 rename=原名=新标识。"""
    out = {}
    for item in items:
        old, sep, new = item.partition("=")
        if sep and old.strip() and new.strip():
            out[old.strip()] = new.strip()
    return out


async def plan_connectors(session: AsyncSession, parsed: plugin_package.ParsedPlugin,
                          renames: dict[str, str]) -> list[dict]:
    """
    每个 MCP 服务怎么落成连接器。同名绝不静默覆盖：
    - NEW：没有同名，直接建，工具名 mcp__<服务名>__*；
    - UPDATE：同名的就是这个插件上次装的（升级）；
    - REUSE_EXISTING：管理员手工建的同名连接器、连接方式完全一样：不再建一个，插件直接用它（卸载插件也不删它）；
    - CONFLICT：同名但配置不同（或属于别的插件）：默认拒绝，给出建议的新标识；
    - RENAMED：管理员选了改名，按新标识建，工具名随之变成 mcp__<新标识>__*。
    """
    plan: list[dict] = []
    used: set[str] = set()
    for d in parsed.connectors:
        name = d["name"]
        item = {
            "server": d["source_server_name"], "id": name, "transport": d["transport_type"],
            "command": d["command"], "args": d["args"], "url": d["url"],
            "source_manifest": d["source_manifest"], "source_mcp_config": d["source_mcp_config"],
            "vendor_id": name, "result": NEW, "reason": "", "suggested_rename": "",
        }
        target = renames.get(d["source_server_name"]) or renames.get(name)
        if target:
            item["vendor_id"] = target
            row = await session.get(McpVendor, target)
            if not mcp_catalog.VENDOR_ID.match(target):
                item.update(result=CONFLICT, reason="新标识只能用小写字母、数字、- 和 _，2~40 个字符")
            elif row is not None and (row.plugin or "") != parsed.id:
                item.update(result=CONFLICT, reason=f"新标识 {target} 也已经被占用了",
                            suggested_rename=await _suggest(session, parsed.id, name))
            else:
                item["result"] = RENAMED
        else:
            row = await session.get(McpVendor, name)
            if row is None:
                item["result"] = NEW
            elif (row.plugin or "") == parsed.id:
                item["result"] = UPDATE
            elif not row.plugin and _vendor_config(row) == _def_config(d):
                item.update(result=REUSE, reason=f"复用现有连接器 {row.id}（连接方式完全一样）")
            else:
                owner = f"插件 {row.plugin}" if row.plugin else "管理员在「连接器」里建的"
                item.update(result=CONFLICT, reason=f"已经有同名连接器 {row.id}（{owner}），连接方式不一样",
                            suggested_rename=await _suggest(session, parsed.id, name))
        if item["result"] != REUSE:
            if item["vendor_id"] in used:
                item.update(result=CONFLICT, reason=f"插件里有两个服务都要用标识 {item['vendor_id']}")
            used.add(item["vendor_id"])
        if item["result"] == RENAMED and (refs := _references(parsed, name)):
            parsed.warnings.append(f"MCP 服务 {name} 改名为 {item['vendor_id']} 后，工具名变成 mcp__{item['vendor_id']}__*；"
                                   f"这些地方还写着 mcp__{name}__，需要自己改：{'、'.join(refs)}")
        plan.append(item)
    return plan


def _references(parsed: plugin_package.ParsedPlugin, name: str) -> list[str]:
    """插件自己的快捷指令、专家、技能里写死的 mcp__<名字>__（改名后会失效）。"""
    needle = f"mcp__{name}__"
    refs = [f"/{c['name']}" for c in parsed.commands if needle in c.get("template", "")]
    refs += [f"专家 {e['id']}" for e in parsed.experts if needle in e["fields"].get("instructions", "")]
    for sk in parsed.skills:
        try:
            with zipfile.ZipFile(io.BytesIO(sk.data)) as zf:
                if any(needle in zf.read(n).decode("utf-8", "replace") for n in zf.namelist() if n.lower().endswith(".md")):
                    refs.append(f"技能 {sk.name}")
        except zipfile.BadZipFile:
            continue
    return refs



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
                  departments: list[str], required: bool, renames: dict[str, str] | None = None) -> Plugin:
    conflicts = await _conflicts(session, parsed)
    if conflicts:
        raise HTTPException(status.HTTP_409_CONFLICT,
                            f"这些东西已经存在、不是这个插件装的：{'、'.join(conflicts)}。先改名或删掉再装")
    plan = await plan_connectors(session, parsed, renames or {})
    bad = [p for p in plan if p["result"] == CONFLICT]
    if bad:
        raise HTTPException(status.HTTP_409_CONFLICT, "MCP server name already exists with different configuration："
                            + "；".join(f"{p['server']}：{p['reason']}"
                                       + (f"，可以改名为 {p['suggested_rename']}" if p["suggested_rename"] else "")
                                       for p in bad))
    owned = {p["vendor_id"]: p for p in plan if p["result"] != REUSE}
    rules = _check_rules(parsed)
    commands = _check_commands(parsed)

    existing = await session.get(Plugin, parsed.id)
    await _remove_parts(session, parsed.id, keep={
        "skills": {s.name for s in parsed.skills},
        "experts": {e["id"] for e in parsed.experts},
        "connectors": set(owned),
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

    defs = {d["name"]: d for d in parsed.connectors}
    for vendor_id, p in owned.items():
        c = defs[p["id"]]
        row = await session.get(McpVendor, vendor_id)
        if row is None:
            row = McpVendor(id=vendor_id, name=c["source_server_name"][:100], enabled=True)
            session.add(row)
        row.name = (c["source_server_name"] if p["result"] != RENAMED else f"{c['source_server_name']}（{parsed.name}）")[:100]
        row.transport, row.url, row.command = c["transport_type"], c["url"], c["command"]
        row.args, row.env, row.headers = c["args"], c["env"], c["headers"]
        row.auth, row.fields = c["auth"], c["fields"]
        row.publisher, row.category = parsed.author[:100], parsed.name[:50]
        row.description = row.description or parsed.description[:300]
        # 来源：插件、版本、清单、配置文件、原名。员工端照原样拿到，审计按它记 mcp_source
        row.plugin, row.source_type = parsed.id, "plugin"
        row.source_plugin_version = (parsed.version or "unknown")[:50]
        row.source_manifest, row.source_mcp_config = c["source_manifest"][:200], c["source_mcp_config"][:200]
        row.source_server_name = c["source_server_name"][:100]
        # 管理员已经填好的预填值（preset_enc）保留：字段对不上时员工连接会提示要填

    manifest = parsed.summary()
    manifest["commands"], manifest["rules"] = commands, rules
    manifest["connectors"] = plan  # 每个服务的处理结果（NEW / UPDATE / REUSE_EXISTING / RENAMED）和落到哪个连接器
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
async def preview(file: UploadFile = File(...), rename: list[str] = Query(default=[]),
                  session: AsyncSession = Depends(get_session)):
    """只解析不安装：看看包里有什么、每个 MCP 服务会新建 / 复用 / 冲突、会不会和已有的东西冲突。"""
    parsed = _parse(await file.read())
    rules = _check_rules(parsed)
    commands = _check_commands(parsed)
    plan = await plan_connectors(session, parsed, _parse_renames(rename))
    summary = parsed.summary()
    summary["commands"], summary["rules"], summary["connectors"] = commands, rules, plan
    existing = await session.get(Plugin, parsed.id)
    return {
        "id": parsed.id, "name": parsed.name, "version": parsed.version, "description": parsed.description,
        "author": parsed.author, "manifest": summary, "counts": _counts(summary),
        "conflicts": await _conflicts(session, parsed)
        + [f"连接器 {p['server']}：{p['reason']}" for p in plan if p["result"] == CONFLICT],
        "upgrade_from": existing.version if existing else None,
    }


@admin_router.post("/upload")
async def upload(file: UploadFile = File(...), departments: list[str] = Query(default=[]),
                 required: bool = False, rename: list[str] = Query(default=[]),
                 who: str = Depends(admin_name), session: AsyncSession = Depends(get_session)):
    """上传插件 zip 安装（再传同一个插件就是升级）。rename=原名=新标识：同名冲突时改名导入。"""
    parsed = _parse(await file.read())
    return _out(await install(session, parsed, file.filename or "上传", who, departments, required,
                              _parse_renames(rename)))


@admin_router.post("/import")
async def import_url(data: PluginImportIn, request: Request, who: str = Depends(admin_name),
                     session: AsyncSession = Depends(get_session)):
    """从 GitHub 链接（可以指到 marketplace 仓库里的某个插件目录）或 zip 下载链接导入。"""
    try:
        content = await skill_library.download(request.app.state.http, data.url)
    except skill_library.SkillError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc
    parsed = _parse(content, skill_library.github_subdir(data.url))
    return _out(await install(session, parsed, data.url, who, data.departments, data.required, data.renames))


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
            "connectors": [c.get("server") or c.get("name", "") for c in m.get("connectors", [])],
            "updated_at": p.updated_at,
        })
    return {"items": out}
