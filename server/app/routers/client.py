"""客户端使用的接口：设备注册、拉取配置、上报审计。"""

import hmac
import logging

from fastapi import APIRouter, Depends, HTTPException, status
from sqlalchemy.ext.asyncio import AsyncSession

from .. import __version__
from ..config import get_settings
from ..crypto import hash_token, new_token
from ..db import get_session
from ..deps import require_device
from ..models import AuditLog, Device
from ..schemas import (
    SCENES,
    AuditBatchIn,
    ClientConfigOut,
    DeviceRegisterIn,
    DeviceRegisterOut,
    SceneInfo,
)
from ..services import model_router
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
    return ClientConfigOut(server_version=__version__, scenes=scenes, policy=await get_policy(session))


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
