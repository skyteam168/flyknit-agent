"""语音转文字。客户端录好音直接把音频字节 POST 上来，由服务端去调上游。

放在服务端而不是客户端的理由：
  - 上游密钥不下发到每台工厂机；
  - 用量和配额沿用现有那套；
  - 热词（机台号、工序名）集中维护，改一次全厂生效。
"""

import logging

from fastapi import APIRouter, Depends, File, Form, Request, UploadFile
from fastapi.responses import JSONResponse
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_device
from ..models import Device
from ..services import asr, model_router, settings_store, usage_store

log = logging.getLogger("flyknit.speech")
router = APIRouter(prefix="/api/v1", tags=["speech"])

# 客户端录的是 16kHz 单声道 WAV，别的格式也收，交给上游判断
ALLOWED_FORMATS = {"wav", "mp3", "m4a", "aac", "ogg", "opus", "flac", "amr", "webm"}


def _error(status: int, message: str, retryable: bool = False) -> JSONResponse:
    return JSONResponse({"error": {"message": message, "retryable": retryable}}, status_code=status)


async def build_target(session: AsyncSession) -> asr.AsrTarget:
    """从数据库里的模型配置拼出一个 ASR 上游。地址、模型名、密钥都不在代码里。"""
    targets = await model_router.resolve(session, "asr")
    t = targets[0]
    extra = t.extra_body or {}
    hotwords = extra.get("hotwords")
    if isinstance(hotwords, str):
        hotwords = [w for w in (p.strip() for p in hotwords.replace("，", ",").split(",")) if w]
    return asr.AsrTarget(
        model=t.upstream_model,
        # model_router 给的是 /chat/completions，这里要的是服务根地址
        base_url=t.url[: -len("/chat/completions")] if t.url.endswith("/chat/completions") else t.url,
        api_key=t.api_key,
        transport=str(extra.get("asr_transport") or "auto"),
        language=extra.get("asr_language") or None,
        vocabulary_id=extra.get("asr_vocabulary_id") or None,
        hotwords=list(hotwords or []),
        display_name=t.display_name,
    )


@router.post("/speech/transcribe")
async def transcribe(
    request: Request,
    audio: UploadFile = File(...),
    audio_format: str = Form("wav"),
    language: str = Form(""),
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    fmt = (audio_format or "").lower().lstrip(".")
    if fmt not in ALLOWED_FORMATS:
        return _error(400, f"不支持的音频格式：{audio_format}")

    data = await audio.read()
    if not data:
        return _error(400, "没有收到录音内容")
    if len(data) > asr.MAX_AUDIO_BYTES:
        return _error(413, "录音太长了，请分段说")

    try:
        target = await build_target(session)
    except model_router.NoRouteError:
        return _error(503, "还没有配置语音识别模型，请联系 IT 在后台的「语音转文字」场景里指定一个")

    # 单次请求里的语言优先于全局配置（界面语言是越南语时就按越南语识别）
    if language:
        target.language = language
    # 后台维护的全厂热词表（机台号、工序名…），和模型配置里的合并
    shared = (await settings_store.get_value(session, "asr", {})).get("hotwords") or []
    if isinstance(shared, str):
        shared = [w for w in (p.strip() for p in shared.replace("，", ",").split(",")) if w]
    if shared:
        target.hotwords = list(dict.fromkeys([*target.hotwords, *shared]))

    try:
        result = await asr.transcribe(request.app.state.http, target, data, fmt)
    except asr.AsrError as exc:
        return _error(502, str(exc), exc.retryable)
    except Exception:  # noqa: BLE001
        log.exception("语音识别异常 device=%s", device.id)
        return _error(502, "语音识别出错了，请再说一次", True)

    # 语音按时长计费而不是 token，所以只记调用次数，不往 token 统计里灌数
    try:
        await usage_store.record_request(session, device.id, "asr")
    except Exception:  # noqa: BLE001
        log.warning("记录语音用量失败 device=%s", device.id, exc_info=True)

    log.info(
        "转写完成 device=%s 字节=%s 时长=%ss 通道=%s",
        device.id, len(data), result.duration_seconds, result.transport,
    )
    return {
        "text": result.text,
        "language": result.language,
        "duration_seconds": result.duration_seconds,
        "transport": result.transport,
        "model": target.display_name,
    }
