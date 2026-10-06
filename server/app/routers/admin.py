import logging
"""管理后台接口。所有请求需带 Authorization: Bearer <FLYKNIT_ADMIN_TOKEN>。"""

import csv
import io
import re
import time
from datetime import date, datetime, time as dtime, timedelta, timezone
from pathlib import Path

import httpx
from fastapi import APIRouter, Depends, File, HTTPException, Query, Request, UploadFile, status
from fastapi.responses import StreamingResponse
from sqlalchemy import desc, select
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy.orm import joinedload

from ..config import get_settings
from ..crypto import decrypt, encrypt, mask
from ..db import get_session, get_sessionmaker
from ..deps import require_admin
from ..models import AuditLog, Device, DevicePolicy, ModelConfig, Provider, RouteRule, SkillPackage
from ..schemas import (
    SCENES,
    AuditOut,
    DeviceOut,
    DevicePatch,
    DevicePolicyIn,
    ModelIn,
    ModelOut,
    ModelPatch,
    ProviderIn,
    ProviderOut,
    ProviderPatch,
    RouteIn,
    SecurityDefaultsIn,
    RouteOut,
    DeviceUsageOut,
    QuotaIn,
    QuotaOut,
    SkillImportIn,
    SkillImportResult,
    SkillOut,
    SkillPatch,
    SmbIn,
    SmbOut,
    SyncResult,
)
from ..services import config_events, security_settings, settings_store, skill_library, usage_store
from ..services.model_router import NON_CHAT_MODEL, Target, build_body, headers_for

log = logging.getLogger("flyknit.admin")
router = APIRouter(prefix="/api/v1/admin", tags=["admin"], dependencies=[Depends(require_admin)])


def _provider_out(p: Provider) -> ProviderOut:
    return ProviderOut(
        id=p.id,
        name=p.name,
        base_url=p.base_url,
        api_key_masked=mask(decrypt(p.api_key_enc)),
        enabled=p.enabled,
        created_at=p.created_at,
    )


async def _get_or_404(session: AsyncSession, model, key):
    obj = await session.get(model, key)
    if obj is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "记录不存在")
    return obj


# ---------- 模型提供方 ----------
@router.get("/providers", response_model=list[ProviderOut])
async def list_providers(session: AsyncSession = Depends(get_session)):
    rows = (await session.scalars(select(Provider).order_by(Provider.id))).all()
    return [_provider_out(p) for p in rows]


@router.post("/providers", response_model=ProviderOut, status_code=201)
async def create_provider(data: ProviderIn, session: AsyncSession = Depends(get_session)):
    p = Provider(
        name=data.name,
        base_url=str(data.base_url).rstrip("/"),
        api_key_enc=encrypt(data.api_key) if data.api_key else "",
        enabled=data.enabled,
    )
    session.add(p)
    await session.commit()
    return _provider_out(p)


@router.patch("/providers/{provider_id}", response_model=ProviderOut)
async def update_provider(provider_id: int, data: ProviderPatch, session: AsyncSession = Depends(get_session)):
    p = await _get_or_404(session, Provider, provider_id)
    if data.name is not None:
        p.name = data.name
    if data.base_url is not None:
        p.base_url = str(data.base_url).rstrip("/")
    if data.api_key is not None:
        p.api_key_enc = encrypt(data.api_key) if data.api_key else ""
    if data.enabled is not None:
        p.enabled = data.enabled
    await session.commit()
    return _provider_out(p)


@router.delete("/providers/{provider_id}", status_code=204)
async def delete_provider(provider_id: int, session: AsyncSession = Depends(get_session)):
    p = await _get_or_404(session, Provider, provider_id)
    await session.delete(p)
    await session.commit()


# 同步模型时默认只排除带日期的快照版本（如 qwen3.8-max-0902、qwen-plus-2025-12-01），只保留主版本
DEFAULT_SYNC_EXCLUDE = r"-\d{4}-\d{2}-\d{2}$|-\d{4}$"


@router.post("/providers/{provider_id}/sync-models", response_model=SyncResult)
async def sync_models(
    provider_id: int,
    request: Request,
    include: str = "",
    exclude: str = DEFAULT_SYNC_EXCLUDE,
    session: AsyncSession = Depends(get_session),
):
    """从提供方的 /models 接口拉取模型列表，新增的模型默认启用，已存在的保持不变。"""
    p = await _get_or_404(session, Provider, provider_id)
    key = decrypt(p.api_key_enc)
    headers = {"Authorization": f"Bearer {key}"} if key else {}
    try:
        resp = await request.app.state.http.get(f"{p.base_url.rstrip('/')}/models", headers=headers, timeout=30)
    except httpx.HTTPError as exc:
        raise HTTPException(status.HTTP_502_BAD_GATEWAY, f"无法访问模型列表：{exc}") from exc
    if resp.status_code >= 400:
        raise HTTPException(status.HTTP_502_BAD_GATEWAY, f"模型列表接口返回 {resp.status_code}：{resp.text[:300]}")
    data = resp.json()
    ids = sorted({str(item.get("id")) for item in data.get("data", []) if isinstance(item, dict) and item.get("id")})

    inc = re.compile(include, re.I) if include else None
    exc_re = re.compile(exclude, re.I) if exclude else None
    existing = set((await session.scalars(select(ModelConfig.model).where(ModelConfig.provider_id == p.id))).all())
    added, skipped = [], 0
    for model_id in ids:
        if (inc and not inc.search(model_id)) or (exc_re and exc_re.search(model_id)):
            skipped += 1
            continue
        if model_id in existing:
            continue
        lowered = model_id.lower()
        session.add(
            ModelConfig(
                provider_id=p.id,
                name=model_id,
                model=model_id,
                supports_tools=not NON_CHAT_MODEL.search(model_id),
                supports_vision="vl" in lowered or "omni" in lowered or "qvq" in lowered,
            )
        )
        added.append(model_id)
    await session.commit()
    return SyncResult(total=len(ids), added=added, skipped=skipped)


# ---------- 模型 ----------
@router.get("/models", response_model=list[ModelOut])
async def list_models(session: AsyncSession = Depends(get_session)):
    return (await session.scalars(select(ModelConfig).order_by(ModelConfig.id))).unique().all()


@router.post("/models", response_model=ModelOut, status_code=201)
async def create_model(data: ModelIn, session: AsyncSession = Depends(get_session)):
    await _get_or_404(session, Provider, data.provider_id)
    m = ModelConfig(**data.model_dump())
    session.add(m)
    await session.commit()
    return m


@router.patch("/models/{model_id}", response_model=ModelOut)
async def update_model(model_id: int, data: ModelPatch, session: AsyncSession = Depends(get_session)):
    m = await _get_or_404(session, ModelConfig, model_id)
    for k, v in data.model_dump(exclude_none=True).items():
        setattr(m, k, v)
    await session.commit()
    return m


@router.delete("/models/{model_id}", status_code=204)
async def delete_model(model_id: int, session: AsyncSession = Depends(get_session)):
    m = await _get_or_404(session, ModelConfig, model_id)
    await session.delete(m)
    await session.commit()


@router.post("/models/{model_id}/test")
async def test_model(model_id: int, request: Request, session: AsyncSession = Depends(get_session)):
    """向模型发一条简短请求，检查连通性和耗时。"""
    m = await session.scalar(
        select(ModelConfig).options(joinedload(ModelConfig.provider)).where(ModelConfig.id == model_id)
    )
    if m is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "记录不存在")
    target = Target(
        model_id=m.id,
        display_name=m.name,
        upstream_model=m.model,
        url=f"{m.provider.base_url.rstrip('/')}/chat/completions",
        api_key=decrypt(m.provider.api_key_enc),
        extra_body=dict(m.extra_body or {}),
        supports_vision=m.supports_vision,
    )
    body = build_body({"messages": [{"role": "user", "content": "ping"}], "max_tokens": 8}, target)
    started = time.perf_counter()
    try:
        resp = await request.app.state.http.post(target.url, json=body, headers=headers_for(target), timeout=60)
    except httpx.HTTPError as exc:
        return {"ok": False, "error": str(exc)}
    elapsed = round((time.perf_counter() - started) * 1000)
    if resp.status_code >= 400:
        return {"ok": False, "status": resp.status_code, "error": resp.text[:500], "elapsed_ms": elapsed}
    return {"ok": True, "status": resp.status_code, "elapsed_ms": elapsed}


# ---------- 场景路由 ----------
@router.get("/routes", response_model=list[RouteOut])
async def list_routes(session: AsyncSession = Depends(get_session)):
    out = []
    for scene in SCENES:
        r = await session.get(RouteRule, scene)
        out.append(
            RouteOut(
                scene=scene,
                model_id=r.model_id if r else None,
                fallback_model_id=r.fallback_model_id if r else None,
            )
        )
    return out


@router.put("/routes/{scene}", response_model=RouteOut)
async def set_route(scene: str, data: RouteIn, session: AsyncSession = Depends(get_session)):
    if scene not in SCENES:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, f"场景必须是 {', '.join(SCENES)} 之一")
    for mid in (data.model_id, data.fallback_model_id):
        if mid is not None:
            await _get_or_404(session, ModelConfig, mid)
    r = await session.get(RouteRule, scene)
    if r is None:
        r = RouteRule(scene=scene)
        session.add(r)
    r.model_id = data.model_id
    r.fallback_model_id = data.fallback_model_id
    await session.commit()
    return RouteOut(scene=scene, model_id=r.model_id, fallback_model_id=r.fallback_model_id)


# ---------- 命令策略 ----------
@router.get("/policy")
async def get_policy(session: AsyncSession = Depends(get_session)):
    return await settings_store.get_policy(session)


_DOMAIN = re.compile(r"^(\*|(\*\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*)$")


def _normalize_domain(raw: str) -> str:
    """管理员多半会直接粘网址进来，把协议、路径、端口剥掉只留主机名。"""
    text = str(raw).strip().lower()
    text = re.sub(r"^[a-z][a-z0-9+.-]*://", "", text)
    text = re.split(r"[/?#]", text, maxsplit=1)[0]
    text = text.rsplit("@", 1)[-1].split(":", 1)[0]
    return text.rstrip(".")


@router.put("/policy")
async def put_policy(policy: dict, session: AsyncSession = Depends(get_session)):
    for pattern in policy.get("blocked_patterns", []):
        try:
            re.compile(pattern)
        except re.error as exc:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, f"正则无效：{pattern}（{exc}）") from exc
    if "allowed_domains" in policy:
        if not isinstance(policy["allowed_domains"], list):
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "allowed_domains 必须是域名列表")
        domains: list[str] = []
        for raw in policy["allowed_domains"]:
            domain = _normalize_domain(raw)
            if not _DOMAIN.match(domain):
                raise HTTPException(status.HTTP_400_BAD_REQUEST, f"域名无效：{raw}")
            if domain not in domains:
                domains.append(domain)
        policy["allowed_domains"] = domains
    if "allow_private_network" in policy:
        policy["allow_private_network"] = bool(policy["allow_private_network"])
    policy["version"] = int(policy.get("version", 0)) + 1
    await settings_store.set_value(session, settings_store.POLICY_KEY, policy)
    return policy


# ---------- SMB 服务账号 ----------
@router.get("/smb", response_model=SmbOut)
async def get_smb(session: AsyncSession = Depends(get_session)):
    v = await settings_store.get_value(session, settings_store.SMB_KEY, {})
    return SmbOut(
        domain=v.get("domain", ""),
        username=v.get("username", ""),
        password_set=bool(v.get("password_enc")),
        share_root=v.get("share_root", ""),
    )


@router.put("/smb", response_model=SmbOut)
async def put_smb(data: SmbIn, session: AsyncSession = Depends(get_session)):
    current = await settings_store.get_value(session, settings_store.SMB_KEY, {})
    value = {
        "domain": data.domain,
        "username": data.username,
        "share_root": data.share_root,
        "password_enc": encrypt(data.password) if data.password is not None else current.get("password_enc", ""),
    }
    await settings_store.set_value(session, settings_store.SMB_KEY, value)
    return SmbOut(
        domain=value["domain"],
        username=value["username"],
        password_set=bool(value["password_enc"]),
        share_root=value["share_root"],
    )


# ---------- 用量与配额 ----------
@router.get("/quota", response_model=QuotaOut)
async def get_quota(session: AsyncSession = Depends(get_session)):
    return await usage_store.get_quota(session)


@router.put("/quota", response_model=QuotaOut)
async def put_quota(data: QuotaIn, session: AsyncSession = Depends(get_session)):
    return await usage_store.set_quota(session, data.model_dump(exclude_none=True))


@router.get("/usage", response_model=list[DeviceUsageOut])
async def list_usage(days: int = Query(7, ge=1, le=90), session: AsyncSession = Depends(get_session)):
    return await usage_store.all_stats(session, days)


# ---------- 技能库 ----------
def _skill_dir() -> Path:
    return skill_library.storage_dir(Path(get_settings().data_dir))


def _save_packages(skills: list[skill_library.ParsedSkill], session: AsyncSession, origin: str, required: bool) -> list[str]:
    saved = []
    for s in skills:
        (_skill_dir() / f"{s.name}.zip").write_bytes(s.data)
        session.add(
            SkillPackage(
                name=s.name,
                description=s.description,
                version=s.version,
                author=s.author,
                origin=origin[:500],
                size=len(s.data),
                file_count=len(s.files),
                required=required,
            )
        )
        saved.append(s.name)
    return saved


async def _replace(session: AsyncSession, name: str) -> None:
    existing = await session.get(SkillPackage, name)
    if existing is not None:
        await session.delete(existing)
        await session.flush()


@router.get("/skills", response_model=list[SkillOut])
async def list_skills(session: AsyncSession = Depends(get_session)):
    return (await session.scalars(select(SkillPackage).order_by(SkillPackage.name))).all()


@router.post("/skills/import", response_model=SkillImportResult)
async def import_skill(data: SkillImportIn, request: Request, session: AsyncSession = Depends(get_session)):
    """从 GitHub 页面链接或任意技能包 zip 的下载链接导入。一个仓库里有多个技能时全部导入。"""
    try:
        content = await skill_library.download(request.app.state.http, data.url)
        skills = skill_library.filter_subdir(skill_library.parse_packages(content), skill_library.github_subdir(data.url))
    except skill_library.SkillError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc
    for s in skills:
        await _replace(session, s.name)
    saved = _save_packages(skills, session, data.url, data.required)
    await session.commit()
    return SkillImportResult(imported=saved)


@router.post("/skills/upload", response_model=SkillImportResult)
async def upload_skill(
    file: UploadFile = File(...),
    required: bool = False,
    session: AsyncSession = Depends(get_session),
):
    """上传技能包 zip（离线环境用）。"""
    content = await file.read()
    if len(content) > skill_library.MAX_TOTAL_BYTES:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "文件过大")
    try:
        skills = skill_library.parse_packages(content)
    except skill_library.SkillError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc
    for s in skills:
        await _replace(session, s.name)
    saved = _save_packages(skills, session, file.filename or "上传", required)
    await session.commit()
    return SkillImportResult(imported=saved)


@router.patch("/skills/{name}", response_model=SkillOut)
async def update_skill(name: str, data: SkillPatch, session: AsyncSession = Depends(get_session)):
    skill = await _get_or_404(session, SkillPackage, name)
    if data.required is not None:
        skill.required = data.required
    if data.enabled is not None:
        skill.enabled = data.enabled
    await session.commit()
    return skill


@router.delete("/skills/{name}", status_code=204)
async def delete_skill(name: str, session: AsyncSession = Depends(get_session)):
    skill = await _get_or_404(session, SkillPackage, name)
    await session.delete(skill)
    await session.commit()
    (_skill_dir() / f"{name}.zip").unlink(missing_ok=True)


# ---------- 设备 ----------
@router.get("/devices", response_model=list[DeviceOut])
async def list_devices(session: AsyncSession = Depends(get_session)):
    return (await session.scalars(select(Device).order_by(desc(Device.last_seen)))).all()


@router.patch("/devices/{device_id}", response_model=DeviceOut)
async def update_device(device_id: int, data: DevicePatch, session: AsyncSession = Depends(get_session)):
    d = await _get_or_404(session, Device, device_id)
    if data.disabled is not None:
        d.disabled = data.disabled
    if data.owner is not None:
        d.owner = data.owner.strip()[:200]
    if data.department is not None:
        d.department = data.department.strip()[:200]
    if data.note is not None:
        d.note = data.note.strip()[:500]
    await session.commit()
    return d


# ---------- 审计 ----------
@router.get("/audit", response_model=list[AuditOut])
async def list_audit(
    decision: str | None = None,
    device_id: int | None = None,
    conversation_id: str | None = None,
    limit: int = Query(100, le=1000),
    offset: int = 0,
    session: AsyncSession = Depends(get_session),
):
    q = select(AuditLog).order_by(desc(AuditLog.id)).limit(limit).offset(offset)
    if decision:
        q = q.where(AuditLog.decision == decision)
    if device_id:
        q = q.where(AuditLog.device_id == device_id)
    if conversation_id:
        q = q.where(AuditLog.conversation_id == conversation_id)
    return (await session.scalars(q)).all()


_AUDIT_COLUMNS = (
    ("id", "编号"),
    ("occurred_at", "发生时间(UTC)"),
    ("device_id", "设备编号"),
    ("machine_name", "计算机名"),
    ("user_name", "Windows 用户"),
    ("scene", "模式"),
    ("tool_name", "工具"),
    ("risk", "风险级别"),
    ("decision", "判定"),
    ("status", "执行结果"),
    ("arguments", "参数"),
    ("summary", "摘要"),
    ("conversation_id", "会话"),
)
_EXPORT_MAX_ROWS = 200_000
_EXPORT_BATCH = 1000


def _csv_cell(value) -> str:
    if value is None:
        return ""
    if isinstance(value, datetime):
        # SQLite 读回来不带时区，存进去的时候就是 UTC
        utc = value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value.astimezone(timezone.utc)
        return utc.strftime("%Y-%m-%d %H:%M:%S")
    text = str(value)
    # 以 = + - @ 开头的单元格 Excel 会当公式执行，命令原文里正好可能出现
    if text[:1] in ("=", "+", "-", "@", "\t", "\r"):
        return "'" + text
    return text


@router.get("/audit/export")
async def export_audit(
    decision: str | None = None,
    device_id: int | None = None,
    since: date | None = Query(None, description="起始日期（含），按 UTC"),
    until: date | None = Query(None, description="结束日期（含），按 UTC"),
):
    """
    把审计记录导出成 CSV，给合规检查和 Excel 用。

    带 BOM 的 UTF-8，Excel 双击打开中文不乱码。数据按批次边查边写，
    几十万条也不会一次性压进内存；单次最多导出 20 万条，再多请按日期分段。
    """
    if since and until and since > until:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "起始日期不能晚于结束日期")

    def filtered(q):
        if decision:
            q = q.where(AuditLog.decision == decision)
        if device_id:
            q = q.where(AuditLog.device_id == device_id)
        if since:
            q = q.where(AuditLog.occurred_at >= datetime.combine(since, dtime.min, tzinfo=timezone.utc))
        if until:
            q = q.where(AuditLog.occurred_at < datetime.combine(until + timedelta(days=1), dtime.min, tzinfo=timezone.utc))
        return q

    async def rows():
        buffer = io.StringIO()
        writer = csv.writer(buffer)
        buffer.write("\ufeff")
        writer.writerow([title for _, title in _AUDIT_COLUMNS])
        yield buffer.getvalue()

        # 请求的会话在开始流式响应后就可能被关掉，这里自己开一个
        sent = 0
        last_id: int | None = None
        async with get_sessionmaker()() as session:
            while sent < _EXPORT_MAX_ROWS:
                q = filtered(select(AuditLog)).order_by(desc(AuditLog.id)).limit(_EXPORT_BATCH)
                if last_id is not None:
                    q = q.where(AuditLog.id < last_id)
                batch = (await session.scalars(q)).all()
                if not batch:
                    break
                buffer.seek(0)
                buffer.truncate()
                for row in batch[: _EXPORT_MAX_ROWS - sent]:
                    writer.writerow([_csv_cell(getattr(row, field)) for field, _ in _AUDIT_COLUMNS])
                sent += len(batch)
                last_id = batch[-1].id
                yield buffer.getvalue()
        log.info("导出审计记录 %d 条（decision=%s device=%s %s~%s）", min(sent, _EXPORT_MAX_ROWS), decision, device_id, since, until)

    name = f"audit-{datetime.now(timezone.utc):%Y%m%d-%H%M%S}.csv"
    return StreamingResponse(
        rows(),
        media_type="text/csv; charset=utf-8",
        headers={"Content-Disposition": f'attachment; filename="{name}"'},
    )


# ---------- 安全中心 ----------

@router.get("/security/catalog")
async def security_catalog():
    """有哪些可配项、默认值、默认锁不锁。管理端界面照这个渲染。"""
    return security_settings.describe()


@router.get("/security")
async def get_security_defaults(session: AsyncSession = Depends(get_session)):
    """全厂默认。"""
    shared = await settings_store.get_security(session)
    return {
        "values": shared.get("values", {}),
        "locks": shared.get("locks", {}),
        "effective": security_settings.effective(shared.get("values"), None, shared.get("locks"), None),
    }


@router.put("/security")
async def set_security_defaults(data: SecurityDefaultsIn, session: AsyncSession = Depends(get_session)):
    saved = await settings_store.set_security(session, data.values, data.locks)
    return {
        "values": saved["values"],
        "locks": saved["locks"],
        "effective": security_settings.effective(saved["values"], None, saved["locks"], None),
    }


@router.get("/devices/{device_id}/policy")
async def get_device_policy(device_id: int, session: AsyncSession = Depends(get_session)):
    device = await _get_or_404(session, Device, device_id)
    shared = await settings_store.get_security(session)
    mine = await session.get(DevicePolicy, device.id)
    return {
        "device_id": device.id,
        "machine_name": device.machine_name,
        "overrides": mine.overrides if mine else {},
        "locks": mine.locks if mine else {},
        "note": mine.note if mine else "",
        # 这台机器最终生效的样子，省得管理端自己再算一遍
        "effective": security_settings.effective(
            shared.get("values"), mine.overrides if mine else None,
            shared.get("locks"), mine.locks if mine else None,
        ),
    }


@router.put("/devices/{device_id}/policy")
async def set_device_policy(
    device_id: int, data: DevicePolicyIn, session: AsyncSession = Depends(get_session)
):
    """
    给某台机器单独放开或锁死几项。

    只存和全厂不一样的那几项——改全厂默认值时，没被单独设过的机器会自动跟着变。
    """
    device = await _get_or_404(session, Device, device_id)
    row = await session.get(DevicePolicy, device.id)
    if row is None:
        row = DevicePolicy(device_id=device.id)
        session.add(row)
    row.overrides = security_settings.sanitize(data.overrides)
    row.locks = security_settings.sanitize_locks(data.locks)
    row.note = data.note[:300]
    await session.commit()
    config_events.bump()
    log.info("设备 %s(%s) 的安全设置已更新：%s", device.id, device.machine_name, row.locks)
    return await get_device_policy(device_id, session)


@router.delete("/devices/{device_id}/policy", status_code=204)
async def clear_device_policy(device_id: int, session: AsyncSession = Depends(get_session)):
    """取消单独设置，这台机器回到全厂默认。"""
    row = await session.get(DevicePolicy, device_id)
    if row is not None:
        await session.delete(row)
        await session.commit()
        config_events.bump()
