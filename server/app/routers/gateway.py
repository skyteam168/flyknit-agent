"""OpenAI 兼容的模型网关。客户端把 model 字段填成场景名（chat / agent / translate / title / vision）。

/embeddings 走 embedding 场景，员工端用它按意思检索本机记忆。
"""

import json
import logging
from urllib.parse import quote

import httpx
from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse, Response, StreamingResponse
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_device
from ..models import ChatRecord, Device
from ..db import get_sessionmaker
from ..services import chat_archive, model_router, usage_store

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

    # 配额：超出当天上限后直接拒绝，客户端会提示联系 IT
    quota = await usage_store.get_quota(session)
    limit = usage_store.limit_for(quota, device)
    if limit:
        used = await usage_store.used_today(session, device.id)
        if used >= limit:
            log.info("quota exceeded device=%s used=%s limit=%s", device.id, used, limit)
            return JSONResponse(
                {
                    "error": {
                        "message": (
                            f"今天的 token 用量已达上限（{used:,}/{limit:,}）。"
                            f"请联系 {quota['contact_name']} 增加额度：{quota['contact_email']}，电话 {quota['contact_phone']}。"
                        ),
                        "type": "quota_exceeded",
                    }
                },
                status_code=429,
            )

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
    headers = {
        "X-Flyknit-Model": quote(target.display_name, safe=""),
        "X-Flyknit-Scene": scene,
        # 客户端据此计算上下文预算，决定何时压缩对话
        "X-Flyknit-Context": str(target.context_length),
    }

    if upstream.status_code >= 400:
        content = await upstream.aread()
        await upstream.aclose()
        return Response(
            content=content,
            status_code=upstream.status_code,
            media_type=upstream.headers.get("content-type", "application/json"),
            headers=headers,
        )

    device_id = device.id
    machine_name, user_name = device.machine_name, device.user_name
    conversation_id = str(body.get("conversation_id") or "")[:64]
    # 请求体带的是整段历史，每轮重发一遍；只取这一轮新增的那条用户消息
    user_text, attachments = chat_archive.new_user_message(body["messages"])

    async def save_chat(answer: str, prompt: int, completion: int) -> None:
        """把这一轮存档。失败绝不能影响对话本身。"""
        if not conversation_id:
            # 没有会话 id 的都是派生调用：压缩上下文、复盘、生成标题。
            # 它们不是用户说的话，不进归档。
            return
        if not user_text and not answer:
            return
        try:
            async with get_sessionmaker()() as s3:
                s3.add(
                    ChatRecord(
                        device_id=device_id,
                        machine_name=machine_name,
                        user_name=user_name,
                        conversation_id=conversation_id,
                        scene=scene,
                        model=target.display_name,
                        user_content=chat_archive.clip(user_text),
                        assistant_content=chat_archive.clip(answer),
                        attachments=attachments,
                        prompt_tokens=max(0, prompt),
                        completion_tokens=max(0, completion),
                    )
                )
                await s3.commit()
        except Exception:  # noqa: BLE001  存档失败不能让用户的对话失败
            log.warning("归档聊天记录失败 device=%s", device_id, exc_info=True)

    async def save_usage(prompt: int, completion: int) -> None:
        """用量记在服务端自己解析出来的数字上，和客户端上报无关。"""
        if prompt <= 0 and completion <= 0:
            return
        try:
            async with get_sessionmaker()() as s2:
                await usage_store.record(s2, device_id, scene, prompt, completion)
        except Exception:  # noqa: BLE001  统计失败不能影响对话
            log.warning("记录用量失败 device=%s", device_id, exc_info=True)

    if body.get("stream"):
        headers["Cache-Control"] = "no-cache"
        headers["X-Accel-Buffering"] = "no"

        async def relay():
            scanner = usage_store.StreamUsageScanner()
            text = chat_archive.StreamTextScanner()
            try:
                async for chunk in upstream.aiter_raw():
                    scanner.feed(chunk)
                    text.feed(chunk)
                    yield chunk
            finally:
                await upstream.aclose()
                scanner.finish()
                text.finish()
                await save_usage(scanner.prompt, scanner.completion)
                # 用户中途停止时这里也会跑到，存的是已经生成的那部分，正是想要的
                await save_chat(text.text, scanner.prompt, scanner.completion)

        return StreamingResponse(
            relay(),
            status_code=upstream.status_code,
            media_type=upstream.headers.get("content-type", "text/event-stream"),
            headers=headers,
        )

    content = await upstream.aread()
    await upstream.aclose()
    prompt, completion = usage_store.extract_usage(content)
    await save_usage(prompt, completion)
    try:
        await save_chat(chat_archive.assistant_reply(json.loads(content)), prompt, completion)
    except (ValueError, TypeError):
        log.debug("上游返回的不是 JSON，跳过归档")
    return Response(
        content=content,
        status_code=upstream.status_code,
        media_type=upstream.headers.get("content-type", "application/json"),
        headers=headers,
    )


# 一次最多几段文字、每段多长：员工端只拿它算记忆和用户那句话的向量，用不着更多
MAX_EMBED_INPUTS = 32
MAX_EMBED_CHARS = 2000


@router.post("/embeddings")
async def embeddings(
    request: Request,
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
) -> Response:
    """文字转向量（OpenAI 兼容）。模型由 embedding 场景决定；没配置时返回 503，员工端据此只按字面匹配。

    不用备用模型：两个向量模型算出来的向量不在一个空间里，混着比没有意义。不归档：向量化的是员工本机的记忆，
    不是对话，服务端不留原文。
    """
    try:
        body = await request.json()
    except ValueError:
        return _error(400, "请求体不是合法的 JSON", "invalid_request_error")
    texts = body.get("input") if isinstance(body, dict) else None
    if isinstance(texts, str):
        texts = [texts]
    if not isinstance(texts, list) or not texts or not all(isinstance(t, str) for t in texts):
        return _error(400, "input 必须是字符串或字符串数组", "invalid_request_error")
    if len(texts) > MAX_EMBED_INPUTS:
        return _error(400, f"一次最多 {MAX_EMBED_INPUTS} 段文字", "invalid_request_error")
    texts = [t[:MAX_EMBED_CHARS] for t in texts]

    quota = await usage_store.get_quota(session)
    limit = usage_store.limit_for(quota, device)
    if limit and await usage_store.used_today(session, device.id) >= limit:
        return _error(429, "今天的 token 用量已达上限", "quota_exceeded")

    try:
        target = (await model_router.resolve(session, "embedding"))[0]
    except model_router.NoRouteError as exc:
        return _error(503, str(exc))

    payload = {"model": target.upstream_model, "input": texts}
    for key, value in target.extra_body.items():
        payload.setdefault(key, value)
    try:
        upstream = await request.app.state.http.post(
            model_router.embeddings_url(target), json=payload, headers=model_router.headers_for(target)
        )
    except httpx.TransportError as exc:
        log.warning("embedding upstream unavailable device=%s: %s", device.id, exc)
        return _error(502, f"向量模型暂时不可用：{exc}")

    headers = {"X-Flyknit-Model": quote(target.display_name, safe=""), "X-Flyknit-Scene": "embedding"}
    if upstream.status_code < 400:
        try:
            usage = upstream.json().get("usage") or {}
            tokens = int(usage.get("prompt_tokens") or usage.get("total_tokens") or 0)
        except (ValueError, TypeError, AttributeError):
            tokens = 0
        if tokens > 0:
            try:
                await usage_store.record(session, device.id, "embedding", tokens, 0)
            except Exception:  # noqa: BLE001  统计失败不能影响检索
                log.warning("记录用量失败 device=%s", device.id, exc_info=True)
    return Response(
        content=upstream.content,
        status_code=upstream.status_code,
        media_type=upstream.headers.get("content-type", "application/json"),
        headers=headers,
    )
