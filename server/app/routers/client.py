"""客户端使用的接口：设备注册、拉取配置、上报审计。"""

import hmac
import logging
from pathlib import Path

from fastapi import APIRouter, Depends, HTTPException, Request, status
from fastapi.responses import FileResponse
from sqlalchemy.ext.asyncio import AsyncSession

from .. import __version__
from ..config import get_settings
from ..crypto import hash_token, new_token
from ..db import get_session
from ..deps import require_device
from sqlalchemy import select
from sqlalchemy.orm import joinedload

from ..models import AuditLog, Device, DevicePolicy, ModelConfig, SkillPackage
from ..schemas import (
    SCENES,
    SkillOut,
    UsageOut,
    AuditBatchIn,
    ClientConfigOut,
    ClientModelOut,
    DeviceRegisterIn,
    DeviceRegisterOut,
    MachineInfoIn,
    SceneInfo,
)
from ..services import config_events, model_router, security_settings, settings_store, skill_library, usage_store
from ..services.settings_store import get_policy

log = logging.getLogger("flyknit.audit")
router = APIRouter(prefix="/api/v1", tags=["client"])


@router.post("/devices/register", response_model=DeviceRegisterOut)
async def register_device(data: DeviceRegisterIn, session: AsyncSession = Depends(get_session)):
    if not hmac.compare_digest(data.enrollment_key, get_settings().enrollment_key):
        raise HTTPException(status.HTTP_403_FORBIDDEN, "注册密钥错误")
    token = new_token()
    device = Device(
        token_hash=hash_token(token),
        machine_name=data.machine_name,
        user_name=data.user_name,
        os_version=data.os_version,
        client_version=data.client_version,
        ui_language=data.ui_language,
    )
    session.add(device)
    await session.commit()
    return DeviceRegisterOut(device_id=device.id, token=token)


def _client_ip(request: Request) -> str:
    """看到的来源地址。经过反向代理时取 X-Forwarded-For 的第一跳。"""
    forwarded = request.headers.get("x-forwarded-for", "")
    if forwarded:
        return forwarded.split(",")[0].strip()[:64]
    return (request.client.host if request.client else "")[:64]


@router.post("/devices/heartbeat", status_code=204)
async def heartbeat(
    data: MachineInfoIn,
    request: Request,
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    """客户端每次拉完配置顺带上报一次，让台账跟着 DHCP、换人、升级一起动。"""
    # 空字符串表示这次没采到，不要用它覆盖掉上次采到的好数据
    for field in ("machine_name", "user_name", "domain", "os_version", "client_version", "ui_language"):
        value = getattr(data, field, "")
        if value:
            setattr(device, field, value[:200])
    if data.ip_addresses:
        device.ip_addresses = ",".join(data.ip_addresses)[:300]
    if data.mac_address:
        device.mac_address = data.mac_address[:64]
    if data.machine_guid:
        device.machine_guid = data.machine_guid[:64].lower()
    # 这个不听客户端的，以服务端看到的为准
    device.observed_ip = _client_ip(request)
    await session.commit()


@router.get("/client/config", response_model=ClientConfigOut)
async def client_config(
    device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)
):
    scenes: list[SceneInfo] = []
    for scene in SCENES:
        try:
            primary = (await model_router.resolve(session, scene))[0]
            m = await model_router.load_model(session, primary.model_id)
            scenes.append(
                SceneInfo(
                    scene=scene,
                    available=True,
                    model_name=primary.display_name,
                    supports_tools=m.supports_tools,
                    supports_vision=m.supports_vision,
                    context_length=m.context_length,
                )
            )
        except model_router.NoRouteError:
            scenes.append(SceneInfo(scene=scene, available=False))
    # 安全中心：全厂默认叠加这台机器的单独设置
    shared = await settings_store.get_security(session)
    mine = await session.get(DevicePolicy, device.id)
    security = security_settings.effective(
        global_values=shared.get("values"),
        device_values=mine.overrides if mine else None,
        global_locks=shared.get("locks"),
        device_locks=mine.locks if mine else None,
    )
    return ClientConfigOut(
        server_version=__version__, scenes=scenes, policy=await get_policy(session), security=security,
        revision=config_events.current(), owner=device.owner, department=device.department,
    )


@router.get("/client/config/wait")
async def client_config_wait(
    rev: int = 0, _: Device = Depends(require_device)
):
    """
    长轮询：挂起到配置发生变化、或 ~25 秒超时为止，返回最新版本号。

    客户端拿着上次的版本号来问，一旦 IT 在后台改了安全中心/命令策略/设备设置就立刻放行，
    员工端随即去拉新配置，做到近乎即时生效。
    """
    new_rev = await config_events.wait_for_change(rev, timeout=25.0)
    return {"revision": new_rev}


@router.post("/audit", status_code=204)
async def report_audit(
    data: AuditBatchIn,
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    for item in data.items:
        row = AuditLog(
            device_id=device.id,
            machine_name=device.machine_name,
            user_name=device.user_name,
            conversation_id=item.conversation_id,
            tool_name=item.tool_name,
            arguments=item.arguments[:8000],
            scene=item.scene,
            risk=item.risk,
            decision=item.decision,
            status=item.status,
            summary=item.summary[:4000],
        )
        if item.occurred_at is not None:
            row.occurred_at = item.occurred_at
        session.add(row)
        if item.decision == "blocked":
            # 被阻止的危险命令：写入告警日志，后续可接入邮件 / 企业微信通知
            log.warning(
                "BLOCKED command device=%s machine=%s user=%s tool=%s args=%s",
                device.id,
                device.machine_name,
                device.user_name,
                item.tool_name,
                item.arguments[:500],
            )
    await session.commit()


@router.get("/client/models", response_model=list[ClientModelOut])
async def client_models(
    device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)
):
    """客户端输入框里可选择的模型（管理员启用的模型）。"""
    rows = (
        await session.scalars(
            select(ModelConfig).options(joinedload(ModelConfig.provider)).order_by(ModelConfig.provider_id, ModelConfig.name)
        )
    ).unique().all()
    return [
        ClientModelOut(
            id=m.id,
            name=m.name,
            model=m.model,
            provider=m.provider.name,
            supports_tools=m.supports_tools,
            supports_vision=m.supports_vision,
        )
        for m in rows
        if m.enabled and m.provider.enabled and not model_router.NON_CHAT_MODEL.search(m.model)
    ]


# ---------- 用量 ----------
@router.get("/client/usage", response_model=UsageOut)
async def client_usage(
    days: int = 7,
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    """这台电脑的 token 用量与配额，界面上的用量面板用它。"""
    return await usage_store.device_stats(session, device.id, max(1, min(days, 90)))


# ---------- 公司技能库 ----------
@router.get("/client/skills", response_model=list[SkillOut])
async def client_skills(_: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    """客户端可以安装的技能；required 的会被客户端自动安装。"""
    rows = await session.scalars(select(SkillPackage).where(SkillPackage.enabled.is_(True)).order_by(SkillPackage.name))
    return rows.all()


@router.get("/client/skills/{name}/download")
async def download_skill(name: str, _: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    skill = await session.get(SkillPackage, name)
    if skill is None or not skill.enabled:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "技能不存在")
    path = skill_library.storage_dir(Path(get_settings().data_dir)) / f"{name}.zip"
    if not path.exists():
        raise HTTPException(status.HTTP_404_NOT_FOUND, "技能包文件丢失，请在管理后台重新导入")
    return FileResponse(path, media_type="application/zip", filename=f"{name}.zip")
