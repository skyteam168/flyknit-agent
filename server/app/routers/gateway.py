"""OpenAI 兼容的模型网关。客户端把 model 字段填成场景名（chat / agent / translate / title / vision）。"""

import logging
from urllib.parse import quote

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse, Response, StreamingResponse
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_device
from ..models import Device
from ..services import model_router

log = logging.getLogger("flyknit.gateway")
router = APIRouter(prefix="/api/v1", tags=["gateway"])


def _error(status: int, message: str, kind: str = "flyknit_error") -> JSONResponse:
    return JSONResponse({"error": {"message": message, "type": kind}}, status_code=status)


@router.post("/chat/completions")
async def chat_completions(
    request: Request,
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
) -> Response:
    try:
        body = await request.json()
    except ValueError:
        return _error(400, "请求体不是合法的 JSON", "invalid_request_error")
    if not isinstance(body, dict) or not isinstance(body.get("messages"), list):
        return _error(400, "缺少 messages", "invalid_request_error")

    scene = str(body.get("scene") or body.get("model") or "chat")
    model_id = body.get("flyknit_model_id")
    model_id = model_id if isinstance(model_id, int) and scene != "title" else None
    if scene not in ("vision",) and model_router.has_image(body["messages"]):
        # 带图片的请求：若选中的模型不支持图片，则改走 vision 场景
        try:
            primary = (await model_router.resolve(session, scene, model_id))[0]
            if not primary.supports_vision:
                scene, model_id = "vision", None
        except model_router.NoRouteError:
            scene, model_id = "vision", None

    try:
        targets = await model_router.resolve(session, scene, model_id)
    except model_router.NoRouteError as exc:
        return _error(503, str(exc))

    client = request.app.state.http
    try:
        upstream, target = await model_router.open_upstream(client, targets, body)
    except model_router.UpstreamUnavailable as exc:
        log.warning("upstream unavailable scene=%s device=%s: %s", scene, device.id, exc)
        return _error(502, f"模型服务暂时不可用：{exc}")

    # HTTP 头只能是 latin-1，模型显示名可能是中文，统一做 URL 编码（客户端解码）
    headers = {"X-Flyknit-Model": quote(target.display_name, safe=""), "X-Flyknit-Scene": scene}

    if upstream.status_code >= 400:
        content = await upstream.aread()
        await upstream.aclose()
        return Response(
            content=content,
            status_code=upstream.status_code,
            media_type=upstream.headers.get("content-type", "application/json"),
            headers=headers,
        )

    if body.get("stream"):
        headers["Cache-Control"] = "no-cache"
        headers["X-Accel-Buffering"] = "no"

        async def relay():
            try:
                async for chunk in upstream.aiter_raw():
                    yield chunk
            finally:
                await upstream.aclose()

        return StreamingResponse(
            relay(),
            status_code=upstream.status_code,
            media_type=upstream.headers.get("content-type", "text/event-stream"),
            headers=headers,
        )

    content = await upstream.aread()
    await upstream.aclose()
    return Response(
        content=content,
        status_code=upstream.status_code,
        media_type=upstream.headers.get("content-type", "application/json"),
        headers=headers,
    )
