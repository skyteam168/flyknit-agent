"""
手机端（安卓 App）的接口。

  /api/v1/mobile/…          手机：扫码配对、看绑定的电脑、提交办事任务、审批、WebSocket（聊天流、实时事件）
  /api/v1/client/mobile/…   员工电脑：生成配对码（放进二维码）、看和解绑自己的手机
  /api/v1/admin/mobile…     后台：开关、审批策略、所有手机、吊销

总开关默认关闭；关闭时手机的所有接口都拒绝，已绑定的手机也用不了。
"""

from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, File, Form, Header, HTTPException, Query, Request, UploadFile, WebSocket, WebSocketDisconnect, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..crypto import hash_token, new_token
from ..db import get_session, get_sessionmaker
from ..deps import require_admin, require_device, require_owner
from ..models import AdminUser, ChannelBinding, ChannelMessage, Device, MobileDevice
from ..schemas import ClientModelOut
from ..services import asr, mobile_hub, model_router, settings_store, usage_store, web_search
from ..services.channel_hub import hub as channels
from ..services.mobile_hub import CHANNEL, external_id, get_config, hub, save_config, task_out
from .speech import ALLOWED_FORMATS, build_target

log = logging.getLogger("flyknit.mobile")

router = APIRouter(prefix="/api/v1/mobile", tags=["mobile"])
client_router = APIRouter(prefix="/api/v1/client/mobile", tags=["mobile"])
admin_router = APIRouter(prefix="/api/v1/admin/mobile", tags=["mobile"], dependencies=[Depends(require_admin)])

MAX_TASK_TEXT = 4000


def _bearer(authorization: str | None) -> str:
    if not authorization or not authorization.lower().startswith("bearer "):
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "缺少手机令牌")
    return authorization[7:].strip()


async def _resolve(session: AsyncSession, token: str) -> tuple[MobileDevice, Device]:
    """手机令牌 → 手机 + 绑定的电脑。总开关关着、电脑被停用都不放行。"""
    mobile = await session.scalar(select(MobileDevice).where(MobileDevice.token_hash == hash_token(token)))
    if mobile is None:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "手机未绑定或已被解绑，请重新扫码绑定")
    if not (await get_config(session))["enabled"]:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "管理员已关闭手机端")
    device = await session.get(Device, mobile.device_id)
    if device is None or device.disabled:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "绑定的电脑已被管理员停用或删除")
    mobile.last_seen = datetime.now(timezone.utc)
    await session.commit()
    return mobile, device


async def require_mobile(
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
) -> tuple[MobileDevice, Device]:
    return await _resolve(session, _bearer(authorization))


# =====================================================================
# 手机
# =====================================================================

class PairIn(BaseModel):
    code: str = Field(min_length=4, max_length=16)
    name: str = Field(default="", max_length=100)
    platform: str = Field(default="android", max_length=20)
    app_version: str = Field(default="", max_length=50)


class ComputerOut(BaseModel):
    machine_name: str
    user_name: str
    online: bool


class MeOut(BaseModel):
    id: int
    name: str
    computer: ComputerOut
    #: 电脑上执行到需要确认的操作时，能不能在手机上批准
    approvals: bool
    #: 管理员已开启联网搜索且配好了密钥；App 据此显示「联网」开关
    web_search_enabled: bool = False


class PairOut(MeOut):
    token: str


def _me(mobile: MobileDevice, device: Device, cfg: dict, web_search_enabled: bool = False) -> dict:
    return {
        "id": mobile.id, "name": mobile.name,
        "computer": {"machine_name": device.machine_name, "user_name": device.user_name,
                     "online": channels.online(device.id)},
        "approvals": bool(cfg["allow_approval"]),
        "web_search_enabled": bool(web_search_enabled),
    }


@router.post("/pair", response_model=PairOut)
async def pair(data: PairIn, request: Request, session: AsyncSession = Depends(get_session)):
    cfg = await get_config(session)
    if not cfg["enabled"]:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "管理员还没有开启手机端")
    source = request.client.host if request.client else "?"
    if hub.pair_blocked(source):
        raise HTTPException(status.HTTP_429_TOO_MANY_REQUESTS, "配对码输错次数太多，请 10 分钟后再试")
    token = "fkm_" + new_token()[3:]
    result = await hub.pair(session, data.code, data.name, data.platform, data.app_version, hash_token(token))
    if result is None:
        hub.pair_failed(source)
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "配对码不对或已过期，请在电脑上重新生成二维码")
    mobile, device = result
    search_cfg = await web_search.get_config(session)
    return PairOut(token=token, **_me(mobile, device, cfg, web_search.is_ready(search_cfg)))


@router.get("/me", response_model=MeOut)
async def me(ctx: tuple[MobileDevice, Device] = Depends(require_mobile), session: AsyncSession = Depends(get_session)):
    mobile, device = ctx
    search_cfg = await web_search.get_config(session)
    return _me(mobile, device, await get_config(session), web_search.is_ready(search_cfg))


@router.get("/models", response_model=list[ClientModelOut])
async def mobile_models(ctx: tuple[MobileDevice, Device] = Depends(require_mobile), session: AsyncSession = Depends(get_session)):
    """手机可选的对话模型（和管理后台启用的列表一致）。默认会优先 deepseek flash。"""
    _ = ctx
    return [
        ClientModelOut(
            id=m.id,
            name=m.name,
            model=m.model,
            provider=m.provider.name,
            supports_tools=m.supports_tools,
            supports_vision=m.supports_vision,
        )
        for m in await model_router.list_chat_models(session)
    ]


@router.post("/speech/transcribe")
async def mobile_transcribe(
    request: Request,
    audio: UploadFile = File(...),
    audio_format: str = Form("wav"),
    language: str = Form(""),
    ctx: tuple[MobileDevice, Device] = Depends(require_mobile),
    session: AsyncSession = Depends(get_session),
):
    """手机录音转文字：用量记在绑定的那台电脑上。"""
    _, device = ctx
    fmt = (audio_format or "").lower().lstrip(".")
    if fmt not in ALLOWED_FORMATS:
        return JSONResponse({"error": {"message": f"不支持的音频格式：{audio_format}"}}, status_code=400)
    data = await audio.read()
    if not data:
        return JSONResponse({"error": {"message": "没有收到录音内容"}}, status_code=400)
    if len(data) > asr.MAX_AUDIO_BYTES:
        return JSONResponse({"error": {"message": "录音太长了，请分段说"}}, status_code=413)
    try:
        target = await build_target(session)
    except model_router.NoRouteError:
        return JSONResponse(
            {"error": {"message": "还没有配置语音识别模型，请联系 IT"}},
            status_code=503,
        )
    if language:
        target.language = language
    shared = (await settings_store.get_value(session, "asr", {})).get("hotwords") or []
    if isinstance(shared, str):
        shared = [w for w in (p.strip() for p in shared.replace("，", ",").split(",")) if w]
    if shared:
        target.hotwords = list(dict.fromkeys([*target.hotwords, *shared]))
    try:
        result = await asr.transcribe(request.app.state.http, target, data, fmt)
    except asr.AsrError as exc:
        return JSONResponse({"error": {"message": str(exc), "retryable": exc.retryable}}, status_code=502)
    except Exception:  # noqa: BLE001
        log.exception("手机语音识别异常 device=%s", device.id)
        return JSONResponse({"error": {"message": "语音识别出错了，请再说一次", "retryable": True}}, status_code=502)
    try:
        await usage_store.record_request(session, device.id, "asr")
    except Exception:  # noqa: BLE001
        log.warning("记录手机语音用量失败 device=%s", device.id, exc_info=True)
    return {
        "text": result.text,
        "language": result.language,
        "duration_seconds": result.duration_seconds,
        "transport": result.transport,
        "model": target.display_name,
    }


@router.delete("/me")
async def unpair_self(ctx: tuple[MobileDevice, Device] = Depends(require_mobile), session: AsyncSession = Depends(get_session)):
    mobile, _ = ctx
    await hub.unpair(session, mobile)
    return {"ok": True}


class TaskIn(BaseModel):
    text: str = Field(min_length=1, max_length=MAX_TASK_TEXT)
    #: 新开一个对话（不接着上一件事的上下文）
    new_conversation: bool = False


class TaskOut(BaseModel):
    id: int
    text: str
    status: str
    answer: str
    error: str
    created_at: str | None
    finished_at: str | None


async def _binding(session: AsyncSession, mobile: MobileDevice) -> ChannelBinding:
    binding = await session.scalar(select(ChannelBinding).where(
        ChannelBinding.channel == CHANNEL, ChannelBinding.external_id == external_id(mobile.id)))
    if binding is None:
        # 电脑上把通道绑定删了之类：按手机现在的绑定补一条
        binding = ChannelBinding(channel=CHANNEL, external_id=external_id(mobile.id), external_name=mobile.name,
                                 device_id=mobile.device_id)
        session.add(binding)
        await session.commit()
    return binding


@router.post("/tasks", response_model=TaskOut)
async def submit_task(data: TaskIn, ctx: tuple[MobileDevice, Device] = Depends(require_mobile),
                      session: AsyncSession = Depends(get_session)):
    """办事：交给绑定的电脑执行。电脑不在线也先排着（30 分钟内上线会接着办）。"""
    mobile, _ = ctx
    binding = await _binding(session, mobile)
    if data.new_conversation:
        binding.conversation_id = ""
    row = await channels.enqueue(session, binding, data.text.strip(), mobile.name)
    return task_out(row)


class InputIn(BaseModel):
    text: str = Field(min_length=1, max_length=MAX_TASK_TEXT)
    #: steer 插话（AI 下一步就看到）/ queue 排到这件事之后 / interrupt 停掉这件事、优先办这句
    mode: str = Field(default="steer", pattern="^(steer|queue|interrupt)$")


class InputOut(BaseModel):
    #: steer：插话已交给正在办的事；merged：那件事电脑还没开始，并进了它的内容；task：作为新的一件事排上了
    delivered: str
    task: TaskOut | None = None


@router.post("/tasks/{task_id}/input", response_model=InputOut)
async def task_input(task_id: int, data: InputIn, ctx: tuple[MobileDevice, Device] = Depends(require_mobile),
                     session: AsyncSession = Depends(get_session)):
    """
    电脑正在办事时手机上又说了一句。插话：交给正在办的那件事，AI 下一步就能看到；
    排队：这件事办完再办；打断：停掉这件事，马上办这句。这件事已经结束了，就照常作为新的一件事。
    """
    mobile, _ = ctx
    row = await session.get(ChannelMessage, task_id)
    if row is None or row.channel != CHANNEL or row.external_id != external_id(mobile.id):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这件事")
    text = data.text.strip()
    if row.status == "pending" and data.mode == "steer":
        # 电脑还没开始办：直接并进这件事的内容里
        row.text = (row.text + "\n\n补充：" + text)[:MAX_TASK_TEXT * 2]
        await session.commit()
        return InputOut(delivered="merged", task=TaskOut(**task_out(row)))
    if row.status == "running" and data.mode == "steer":
        if not hub.push_input(row.id, text):
            raise HTTPException(status.HTTP_429_TOO_MANY_REQUESTS, "补充的话太多了，等电脑处理一下再说")
        return InputOut(delivered="steer")
    if row.status == "running" and data.mode == "interrupt":
        hub.request_stop(row.id)
        log.info("手机 #%s 打断了任务 #%s", mobile.id, row.id)
    # 排队、打断、或者这件事已经结束：作为新的一件事排上（同一台电脑一次只办一件，按顺序来）
    binding = await _binding(session, mobile)
    new = await channels.enqueue(session, binding, text, mobile.name)
    return InputOut(delivered="task", task=TaskOut(**task_out(new)))


@router.get("/tasks", response_model=list[TaskOut])
async def my_tasks(limit: int = Query(default=30, ge=1, le=100), ctx: tuple[MobileDevice, Device] = Depends(require_mobile),
                   session: AsyncSession = Depends(get_session)):
    mobile, _ = ctx
    rows = (await session.scalars(
        select(ChannelMessage).where(ChannelMessage.channel == CHANNEL, ChannelMessage.external_id == external_id(mobile.id))
        .order_by(ChannelMessage.id.desc()).limit(limit)
    )).all()
    return [task_out(r) for r in rows]


@router.get("/approvals")
async def my_approvals(ctx: tuple[MobileDevice, Device] = Depends(require_mobile)):
    mobile, _ = ctx
    return [a.out() for a in hub.pending_for(mobile.id)]


class AnswerIn(BaseModel):
    approve: bool


@router.post("/approvals/{approval_id}")
async def answer_approval(approval_id: str, data: AnswerIn, ctx: tuple[MobileDevice, Device] = Depends(require_mobile),
                          session: AsyncSession = Depends(get_session)):
    mobile, _ = ctx
    if data.approve and not (await get_config(session))["allow_approval"]:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "管理员不允许在手机上批准操作，请回到电脑上处理")
    a = await hub.answer(approval_id, mobile.id, data.approve)
    if a is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个待确认的操作")
    return a.out()


@router.websocket("/ws")
async def socket(ws: WebSocket, token: str = Query(default="")):
    """
    一条长连接干两件事：
    - 手机发 {"type":"chat", id, mode, target, conversation_id, messages}，服务端逐字推回 chat.reasoning / chat.delta / chat.done
    - 服务端推办事进度（task.event / task.updated）和审批请求（approval.request / approval.closed）
    """
    auth = ws.headers.get("authorization") or ""
    raw = auth[7:].strip() if auth.lower().startswith("bearer ") else token
    sessionmaker = get_sessionmaker()
    try:
        async with sessionmaker() as s:
            mobile, device = await _resolve(s, raw)
            cfg = await get_config(s)
            search_ready = web_search.is_ready(await web_search.get_config(s))
    except HTTPException as exc:
        await ws.close(code=4401 if exc.status_code == 401 else 4403, reason="")
        return
    await ws.accept()
    hub.attach(mobile.id, ws)
    chats: dict[str, asyncio.Task] = {}
    try:
        await ws.send_json({"type": "hello", **_me(mobile, device, cfg, search_ready),
                            "approvals_pending": [a.out() for a in hub.pending_for(mobile.id)]})
        # 后台预热上游，别让员工第一句聊天去付 TLS/模型冷启的账
        asyncio.create_task(hub.warm_upstream(ws.app.state.http, sessionmaker))
        while True:
            msg = await ws.receive_json()
            kind = msg.get("type") if isinstance(msg, dict) else None
            if kind == "ping":
                await ws.send_json({"type": "pong", "online": channels.online(device.id)})
            elif kind == "chat":
                rid = str(msg.get("id") or "")[:64]
                if rid in chats and not chats[rid].done():
                    continue
                chats[rid] = asyncio.create_task(hub.chat(ws, ws.app.state.http, sessionmaker, mobile, device, msg))
            elif kind == "chat.stop":
                if (t := chats.get(str(msg.get("id") or ""))) is not None:
                    t.cancel()
    except (WebSocketDisconnect, RuntimeError):
        pass
    except ValueError:
        pass  # 不是 JSON：当作断开
    finally:
        hub.detach(mobile.id, ws)
        for t in chats.values():
            t.cancel()


# =====================================================================
# 员工电脑
# =====================================================================

class PairCodeOut(BaseModel):
    code: str
    expires_at: datetime


class PhoneOut(BaseModel):
    id: int
    name: str
    platform: str
    app_version: str
    created_at: datetime
    last_seen: datetime | None
    connected: bool


def _phone(m: MobileDevice) -> PhoneOut:
    return PhoneOut(id=m.id, name=m.name, platform=m.platform, app_version=m.app_version, created_at=m.created_at,
                    last_seen=m.last_seen, connected=hub.connected(m.id))


class AppDownloadOut(BaseModel):
    version_name: str
    #: 相对服务器地址的下载路径（电脑上拼成二维码给还没装 App 的手机扫）
    download_path: str


class ClientMobileOut(BaseModel):
    enabled: bool
    phones: list[PhoneOut]
    #: 后台发布过的最新 App；没有就是 null（员工找 IT 要安装包）
    app: AppDownloadOut | None = None


@client_router.get("", response_model=ClientMobileOut)
async def my_phones(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    from .mobile_releases import download_path, latest

    rows = (await session.scalars(select(MobileDevice).where(MobileDevice.device_id == device.id).order_by(MobileDevice.id))).all()
    enabled = bool((await get_config(session))["enabled"])
    release = await latest(session) if enabled else None
    app = AppDownloadOut(version_name=release.version_name, download_path=download_path(release)) if release else None
    return ClientMobileOut(enabled=enabled, phones=[_phone(m) for m in rows], app=app)


@client_router.post("/pair-code", response_model=PairCodeOut)
async def pair_code(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    if not (await get_config(session))["enabled"]:
        raise HTTPException(status.HTTP_409_CONFLICT, "管理员还没有开启手机端")
    row = await hub.new_pair_code(session, device.id)
    return PairCodeOut(code=row.code, expires_at=row.expires_at)


@client_router.delete("/phones/{mobile_id}")
async def unpair_phone(mobile_id: int, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    mobile = await session.get(MobileDevice, mobile_id)
    if mobile is None or mobile.device_id != device.id:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这部手机")
    await hub.unpair(session, mobile)
    return {"ok": True}


# =====================================================================
# 后台
# =====================================================================

class MobileConfigOut(BaseModel):
    enabled: bool
    allow_approval: bool
    approval_timeout_min: int
    phones: int
    connected: int


class MobileConfigIn(BaseModel):
    enabled: bool = False
    allow_approval: bool = True
    approval_timeout_min: int = Field(default=5, ge=1, le=30)


async def _config_out(session: AsyncSession) -> MobileConfigOut:
    cfg = await get_config(session)
    ids = (await session.scalars(select(MobileDevice.id))).all()
    return MobileConfigOut(enabled=bool(cfg["enabled"]), allow_approval=bool(cfg["allow_approval"]),
                           approval_timeout_min=int(cfg["approval_timeout_min"]), phones=len(ids),
                           connected=sum(1 for i in ids if hub.connected(i)))


@admin_router.get("", response_model=MobileConfigOut)
async def get_mobile(session: AsyncSession = Depends(get_session)):
    return await _config_out(session)


@admin_router.put("", response_model=MobileConfigOut)
async def put_mobile(
    data: MobileConfigIn,
    # 开了它，手机上就能让员工电脑执行操作、批准确认：和改安全策略同一档权限
    _owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    await save_config(session, data.enabled, data.allow_approval, data.approval_timeout_min)
    if not data.enabled:
        for mid in list(hub._sockets):
            await hub.close_all(mid)
    return await _config_out(session)


class WebSearchOut(BaseModel):
    enabled: bool
    provider: str
    count: int
    api_key_masked: str
    api_key_set: bool
    ready: bool


class WebSearchIn(BaseModel):
    enabled: bool = False
    provider: str = "bocha"
    count: int = Field(default=5, ge=1, le=10)
    #: 不传表示不改；传空字符串清空
    api_key: str | None = None


@admin_router.get("/web-search", response_model=WebSearchOut)
async def get_web_search(session: AsyncSession = Depends(get_session)):
    return WebSearchOut(**web_search.public_out(await web_search.get_config(session)))


@admin_router.put("/web-search", response_model=WebSearchOut)
async def put_web_search(data: WebSearchIn, session: AsyncSession = Depends(get_session)):
    try:
        cfg = await web_search.save_config(
            session,
            enabled=data.enabled,
            provider=data.provider,
            count=data.count,
            api_key=data.api_key,
        )
    except web_search.SearchError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, str(exc)) from exc
    return WebSearchOut(**web_search.public_out(cfg))


class AdminPhoneOut(PhoneOut):
    device_id: int
    machine_name: str
    user_name: str


@admin_router.get("/phones", response_model=list[AdminPhoneOut])
async def all_phones(session: AsyncSession = Depends(get_session)):
    rows = (await session.execute(
        select(MobileDevice, Device).join(Device, Device.id == MobileDevice.device_id).order_by(MobileDevice.id.desc())
    )).all()
    return [AdminPhoneOut(**_phone(m).model_dump(), device_id=d.id, machine_name=d.machine_name, user_name=d.user_name)
            for m, d in rows]


@admin_router.delete("/phones/{mobile_id}")
async def revoke_phone(mobile_id: int, session: AsyncSession = Depends(get_session)):
    mobile = await session.get(MobileDevice, mobile_id)
    if mobile is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这部手机")
    await hub.unpair(session, mobile)
    log.warning("后台吊销了手机 %s（#%s）", mobile.name, mobile.id)
    return {"ok": True}


__all__ = ["router", "client_router", "admin_router", "mobile_hub"]
