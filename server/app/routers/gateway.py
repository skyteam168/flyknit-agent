"""OpenAI 兼容的模型网关。客户端把 model 字段填成场景名（chat / agent / translate / title / vision）。

/embeddings 走 embedding 场景，员工端用它按意思检索本机记忆。
"""

import json
import logging
import time
from urllib.parse import quote

import anyio
import httpx
from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse, Response, StreamingResponse
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_device
from ..models import ChatRecord, Device, DlpEvent
from ..db import get_sessionmaker
from ..services import chat_archive, device_security, dlp, model_router, subtask_store, telemetry, usage_store, web_search

log = logging.getLogger("flyknit.gateway")
router = APIRouter(prefix="/api/v1", tags=["gateway"])


#: 员工端标明的发起方。不认识的值忽略，免得随便一个字符串就在统计里多出一个场景
USAGE_SOURCES = {"schedule", "channel", "instruction", "mobile"}


async def _dlp_policy(session: AsyncSession, device: Device) -> dlp.Policy:
    """这台电脑生效的脱敏设置（全厂 → 部门 → 单台电脑）。"""
    return dlp.Policy.from_security(await device_security.effective_for(session, device))


async def _dlp_record(session: AsyncSession, device: Device, vault: dlp.Vault, scene: str, conversation_id: str, action: str) -> None:
    """只记数量，不记原值。失败不能影响对话。"""
    if not vault.total:
        return
    try:
        session.add(DlpEvent(device_id=device.id, conversation_id=conversation_id[:64], scene=scene[:32], action=action,
                             counts=dict(vault.counts)))
        await session.commit()
    except Exception:  # noqa: BLE001
        await session.rollback()
        log.warning("记录脱敏事件失败 device=%s", device.id, exc_info=True)


def _error(status: int, message: str, kind: str = "flyknit_error") -> JSONResponse:
    return JSONResponse({"error": {"message": message, "type": kind}}, status_code=status)


#: 流式回复中途上游断开时补发给客户端的一条错误
STREAM_BROKEN = (
    "data: "
    + json.dumps({"error": {"message": "模型服务中途断开，请重试", "type": "upstream_error"}}, ensure_ascii=False)
    + "\n\n"
).encode("utf-8")


async def finish_stream(upstream, scanner, text, save_usage, save_chat) -> None:
    """流式转发结束后的收尾：关上游、记用量、存档。调用方要在屏蔽取消的范围里调它。"""
    try:
        await upstream.aclose()
    except Exception:  # noqa: BLE001  关不掉也要接着记用量
        log.debug("关闭上游连接失败", exc_info=True)
    scanner.finish()
    text.finish()
    await save_usage(scanner.prompt, scanner.completion, getattr(scanner, "cached", 0))
    await save_chat(text.text, scanner.prompt, scanner.completion)


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
    started_ns = time.time_ns()  # OpenTelemetry：这次模型调用从这里开始算（docs/observability.md）

    # 专家派遣的子任务：先过子任务的几道闸（已结束、超时、开关关了、预算用完），再走下面的配额和转发
    subtask_id = body.pop("flyknit_subtask_id", None)
    subtask_id = str(subtask_id)[:64] if subtask_id not in (None, "") else ""
    if subtask_id:
        try:
            await subtask_store.admit(session, device.id, subtask_id)
        except subtask_store.Rejected as exc:
            log.info("subtask rejected device=%s subtask=%s: %s", device.id, subtask_id, exc.kind)
            return _error(exc.status, exc.message, exc.kind)
        # 专家不能再派遣
        subtask_store.strip_tool(body, subtask_store.DELEGATE_TOOL)
    elif subtask_store.has_tool(body, subtask_store.DELEGATE_TOOL) and not await subtask_store.delegation_enabled(
            session, device.id):
        # 开关关着时主助手也不该有这个工具（员工端没来得及拉新配置、或者老版本）
        subtask_store.strip_tool(body, subtask_store.DELEGATE_TOOL)
    # 上游不报用量时按字符估算，提示部分要在转发前数
    subtask_prompt_chars = subtask_store.prompt_chars(body) if subtask_id else 0

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

    # 谁发起的：定时任务、钉钉/企业微信、IT 指令。只影响用量统计里记成哪个场景，选模型照旧按 scene
    source = body.get("flyknit_source")
    usage_scene = source if source in USAGE_SOURCES else scene
    # 这次调用干什么用：正常对话 / 压缩 / 复盘 / 标题……只用于按用途统计用量
    purpose = usage_store.clean_purpose(body.get("flyknit_purpose"))

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

    # 数据脱敏（services/dlp.py）：发给上游的是换过占位符的副本；body 保留原值（归档、子任务计数照旧用原值）
    want_search = bool(body.pop("flyknit_web_search", False)) and scene == "chat"
    guarded = dlp.protect(body, await _dlp_policy(session, device))
    vault = guarded.vault
    conversation_ref = str(body.get("conversation_id") or "")
    outbound = guarded.body
    await _dlp_record(session, device, vault, scene, conversation_ref, "masked")

    # 桌面「对话」联网：客户端带 flyknit_web_search；流量从服务器出网，与手机共用后台配置
    sources_json = ""
    if want_search:
        search_cfg = await web_search.get_config(session)
        if web_search.is_ready(search_cfg):
            # 搜索词也是出网的：用脱敏后的那份
            query = web_search.last_user_query(outbound["messages"])
            try:
                hits = await web_search.search(request.app.state.http, search_cfg, query)
                outbound = {**outbound, "messages": web_search.inject_into_messages(outbound["messages"], hits)}
                sources_json = json.dumps(web_search.hits_out(hits), ensure_ascii=False)
            except web_search.SearchError as exc:
                log.warning("桌面联网搜索失败 device=%s: %s", device.id, exc)
                sources_json = "[]"

    client = request.app.state.http
    try:
        upstream, target = await model_router.open_upstream(client, targets, outbound)
    except model_router.UpstreamUnavailable as exc:
        log.warning("upstream unavailable scene=%s device=%s: %s", scene, device.id, exc)
        telemetry.model_call(device=device, scene=scene, purpose=purpose, model="", start_ns=started_ns, status=502,
                             conversation_id=conversation_ref[:64], subtask=bool(subtask_id), error="模型服务暂时不可用")
        return _error(502, f"模型服务暂时不可用：{exc}")

    # HTTP 头只能是 latin-1，模型显示名可能是中文，统一做 URL 编码（客户端解码）
    headers = {
        "X-Flyknit-Model": quote(target.display_name, safe=""),
        "X-Flyknit-Scene": scene,
        # 客户端据此计算上下文预算，决定何时压缩对话
        "X-Flyknit-Context": str(target.context_length),
    }
    if sources_json:
        headers["X-Flyknit-Sources"] = quote(sources_json, safe="")
    if vault.total:
        # 这次替换 / 隐藏了几处（员工端可以据此提示「已脱敏」）
        headers["X-Flyknit-Masked"] = str(vault.total)
        # 各类型各几处（只有数量，没有原值）：员工端据此显示「已把邮箱 28 处换成占位符」的提示，任务不停
        headers["X-Flyknit-Masked-Counts"] = quote(json.dumps(dict(vault.counts)), safe="")

    if upstream.status_code >= 400:
        telemetry.model_call(device=device, scene=scene, purpose=purpose, model=target.display_name, start_ns=started_ns,
                             status=upstream.status_code, conversation_id=conversation_ref[:64], subtask=bool(subtask_id))
        try:
            content = await upstream.aread()
        except httpx.HTTPError as exc:
            log.warning("读取上游错误响应失败 scene=%s device=%s: %s", scene, device.id, exc)
            return _error(502, "模型服务读取超时或连接中断，请重试")
        finally:
            await upstream.aclose()
        return Response(
            content=content,
            status_code=upstream.status_code,
            media_type=upstream.headers.get("content-type", "application/json"),
            headers=headers,
        )

    device_id = device.id
    machine_name, user_name = device.machine_name, device.user_name
    telemetry_device = telemetry.snapshot(device)
    conversation_id = str(body.get("conversation_id") or "")[:64]
    # 请求体带的是整段历史，每轮重发一遍；只取这一轮新增的那条用户消息
    user_text, attachments = chat_archive.new_user_message(body["messages"])

    async def save_chat(answer: str, prompt: int, completion: int) -> None:
        """把这一轮存档。失败绝不能影响对话本身。"""
        if not conversation_id or subtask_id:
            # 没有会话 id 的都是派生调用：压缩上下文、复盘、生成标题。
            # 专家子任务的「用户消息」是主助手写的任务书，也不是员工说的话。都不进归档
            # （专家做了什么在派遣记录和审计里）。
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

    async def save_usage(prompt: int, completion: int, cached: int = 0) -> None:
        """用量记在服务端自己解析出来的数字上，和客户端上报无关。"""
        # OpenTelemetry：一次成功的模型调用（只有用量和耗时，没有内容）。流式在流结束时才到这里，耗时含生成时间
        telemetry.model_call(device=telemetry_device, scene=scene, purpose=purpose, model=target.display_name,
                             start_ns=started_ns, status=200, prompt=prompt, completion=completion, cached=cached,
                             conversation_id=conversation_id, subtask=bool(subtask_id))
        if prompt <= 0 and completion <= 0:
            return
        try:
            async with get_sessionmaker()() as s2:
                await usage_store.record(s2, device_id, usage_scene, prompt, completion,
                                         model_id=target.model_id, model_name=target.display_name, cached=cached,
                                         purpose=purpose)
        except Exception:  # noqa: BLE001  统计失败不能影响对话
            log.warning("记录用量失败 device=%s", device_id, exc_info=True)

    async def save_subtask(prompt: int, completion: int, answer_chars: int, cached: int = 0) -> None:
        """用量累加到子任务上，预算靠它。上游没给用量就估算。失败绝不能影响对话本身。

        和员工端的任务预算同一个口径：输入里命中缓存的部分按一成计（每调一次都要重发整段上下文，
        前面没变的部分上游按缓存价收费）。每日额度、用量统计照旧按原始用量记（save_usage）。
        """
        if not subtask_id:
            return
        try:
            prompt = subtask_store.budget_prompt(prompt, cached)
            p, c, estimated = subtask_store.estimate(prompt, completion, subtask_prompt_chars, answer_chars)
            async with get_sessionmaker()() as s4:
                await subtask_store.add_usage(s4, subtask_id, p, c, estimated)
        except Exception:  # noqa: BLE001  统计失败不能影响对话
            log.warning("记录子任务用量失败 device=%s subtask=%s", device_id, subtask_id, exc_info=True)

    if body.get("stream"):
        headers["Cache-Control"] = "no-cache"
        headers["X-Accel-Buffering"] = "no"

        async def relay():
            scanner = usage_store.StreamUsageScanner()
            text = chat_archive.StreamTextScanner()
            counter = subtask_store.StreamCharCounter() if subtask_id else None
            # 有替换过才逐个事件还原；没替换过原样转发，一个字节都不动
            restorer = dlp.StreamRestorer(vault) if not vault.empty else None

            def observe(data: bytes) -> None:
                scanner.feed(data)
                text.feed(data)
                if counter is not None:
                    counter.feed(data)

            try:
                try:
                    async for chunk in upstream.aiter_raw():
                        if restorer is not None:
                            chunk = restorer.feed(chunk)
                            if not chunk:
                                continue
                        observe(chunk)
                        yield chunk
                    if restorer is not None and (tail := restorer.finish()):
                        observe(tail)
                        yield tail
                except httpx.HTTPError as exc:
                    # 已经回了 200 和一部分内容，改不了状态码，只能在流里补一条错误让客户端提示
                    log.warning("上游中途断开 scene=%s device=%s: %s", scene, device_id, exc)
                    yield STREAM_BROKEN
            finally:
                # 用户点「停止」时连接断开，这个生成器会被取消；不屏蔽取消的话，下面的
                # await 一碰就再抛 CancelledError，用量和存档都不会记。存的是已经生成的那部分
                with anyio.CancelScope(shield=True):
                    await finish_stream(upstream, scanner, text, save_usage, save_chat)
                    if counter is not None:
                        counter.finish()
                        await save_subtask(scanner.prompt, scanner.completion, counter.chars, scanner.cached)

        return StreamingResponse(
            relay(),
            status_code=upstream.status_code,
            media_type=upstream.headers.get("content-type", "text/event-stream"),
            headers=headers,
        )

    try:
        content = await upstream.aread()
    except httpx.HTTPError as exc:
        log.warning("读取上游响应失败 scene=%s device=%s: %s", scene, device.id, exc)
        return _error(502, "模型服务读取超时或连接中断，请重试")
    finally:
        await upstream.aclose()
    if not vault.empty:
        # 非流式：正文、思考、工具调用参数里的占位符换回原值
        try:
            content = json.dumps(dlp.restore_payload(json.loads(content), vault), ensure_ascii=False).encode()
        except (ValueError, TypeError):
            pass
    prompt, completion = usage_store.extract_usage(content)
    cached = usage_store.extract_cached(content)
    await save_usage(prompt, completion, cached)
    try:
        payload = json.loads(content)
    except (ValueError, TypeError):
        payload = None
    await save_subtask(prompt, completion, subtask_store.completion_chars(payload), cached)
    if isinstance(payload, dict):
        await save_chat(chat_archive.assistant_reply(payload), prompt, completion)
    else:
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
    # 向量也是发给外部模型的：同样脱敏（只替换，不用还原）
    texts, vault = dlp.protect_texts(texts, await _dlp_policy(session, device))
    await _dlp_record(session, device, vault, "embedding", "", "masked")

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
                await usage_store.record(session, device.id, "embedding", tokens, 0,
                                         model_id=target.model_id, model_name=target.display_name)
            except Exception:  # noqa: BLE001  统计失败不能影响检索
                log.warning("记录用量失败 device=%s", device.id, exc_info=True)
    return Response(
        content=upstream.content,
        status_code=upstream.status_code,
        media_type=upstream.headers.get("content-type", "application/json"),
        headers=headers,
    )
