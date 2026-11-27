"""
OpenTelemetry 导出（docs/observability.md）：把模型调用、工具调用变成 OTLP 追踪（trace），发到公司自己的采集器
（OpenTelemetry Collector、Grafana Tempo、Jaeger、SigNoz、阿里云 / 腾讯云可观测……凡是收 OTLP/HTTP 的都行）。

- 不加依赖：直接按 OTLP/HTTP 的 JSON 编码发（POST <地址>/v1/traces），httpx 本来就有；
- 只在服务端：模型调用都经过网关，工具调用员工端本来就上报审计，员工电脑什么都不用改、也不直连采集器；
- 不带内容：不发提示词、回答、工具参数和结果；默认也不带电脑名和使用者（include_identity 打开才带）；
- 不拖慢业务：放进内存队列（满了丢最旧的，记数），后台每 5 秒批量发一次；采集器挂了只记错误，不影响任何请求；
- 总开关默认关；配置只有超级管理员能改，请求头（比如 API Key）加密存储、后台只显示名字。

同一个任务（没有任务编号时同一个对话）的调用放在同一条 trace 里：trace id 由任务编号算出来，
在追踪工具里点开就能看到一个任务里先后调了几次模型、用了哪些工具、每一步多久、花了多少 token。
"""

from __future__ import annotations

import asyncio
import collections
import hashlib
import json
import logging
import os
import time
from datetime import datetime, timezone
from typing import Any

import httpx
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from .. import __version__
from ..crypto import decrypt
from . import settings_store

log = logging.getLogger("flyknit.telemetry")

KEY = "otel"
MAX_QUEUE = 5000
BATCH = 500
FLUSH_SECONDS = 5
RELOAD_SECONDS = 30
DEFAULTS: dict[str, Any] = {
    "enabled": False,
    "endpoint": "",
    #: [{"name": "Authorization", "value_enc": "..."}]
    "headers": [],
    "service_name": "flyknitbuddy",
    "include_identity": False,
    "timeout_ms": 5000,
}

SPAN_KIND_INTERNAL = 1
SPAN_KIND_CLIENT = 3
STATUS_OK = 1
STATUS_ERROR = 2


class _State:
    def __init__(self) -> None:
        self.config: dict = dict(DEFAULTS)
        self.queue: collections.deque[dict] = collections.deque(maxlen=MAX_QUEUE)
        self.sent = 0
        self.dropped = 0
        self.failed_batches = 0
        self.last_error = ""
        self.last_error_at: datetime | None = None
        self.last_sent_at: datetime | None = None


state = _State()


def merged(raw: dict | None) -> dict:
    return {**DEFAULTS, **(raw or {})}


async def load(session: AsyncSession) -> dict:
    state.config = merged(await settings_store.get_value(session, KEY, {}))
    return state.config


def on() -> bool:
    c = state.config
    return bool(c.get("enabled") and c.get("endpoint"))


def traces_url(endpoint: str) -> str:
    e = endpoint.rstrip("/")
    return e if e.endswith("/v1/traces") else e + "/v1/traces"


def _trace_id(key: str) -> str:
    if not key:
        return os.urandom(16).hex()
    return hashlib.sha256(key.encode()).hexdigest()[:32]


def _attr(key: str, value: Any) -> dict:
    if isinstance(value, bool):
        v: dict = {"boolValue": value}
    elif isinstance(value, int):
        v = {"intValue": str(value)}  # OTLP JSON 里 64 位整数写成字符串
    elif isinstance(value, float):
        v = {"doubleValue": value}
    else:
        v = {"stringValue": str(value)}
    return {"key": key, "value": v}


def snapshot(device: Any) -> Any:
    """流式回答结束时请求的数据库会话可能已经关了：先把要用的几个字段抄下来。"""
    from types import SimpleNamespace

    return SimpleNamespace(id=device.id, department=device.department or "", machine_name=device.machine_name or "",
                           owner=getattr(device, "owner", "") or "", user_name=device.user_name or "")


def _device_attrs(device: Any) -> dict:
    out: dict[str, Any] = {}
    if device is None:
        return out
    out["flyknit.device.id"] = device.id
    if getattr(device, "department", ""):
        out["flyknit.department"] = device.department
    if state.config.get("include_identity"):
        out["host.name"] = device.machine_name or ""
        if getattr(device, "owner", "") or getattr(device, "user_name", ""):
            out["enduser.id"] = device.owner or device.user_name
    return out


def emit(name: str, start_ns: int, end_ns: int, attrs: dict[str, Any], trace_key: str = "",
         kind: int = SPAN_KIND_INTERNAL, error: str = "") -> None:
    """放进队列；总开关关着时什么都不做。队列满了丢最旧的。"""
    if not on():
        return
    if len(state.queue) == state.queue.maxlen:
        state.dropped += 1
    span = {
        "traceId": _trace_id(trace_key),
        "spanId": os.urandom(8).hex(),
        "name": name,
        "kind": kind,
        "startTimeUnixNano": str(start_ns),
        "endTimeUnixNano": str(max(end_ns, start_ns)),
        "attributes": [_attr(k, v) for k, v in attrs.items() if v is not None and v != ""],
        "status": {"code": STATUS_ERROR, "message": error[:300]} if error else {"code": STATUS_OK},
    }
    state.queue.append(span)


def trace_key(device_id: int, task_id: str = "", conversation_id: str = "") -> str:
    if task_id:
        return f"{device_id}:task:{task_id}"
    if conversation_id:
        return f"{device_id}:conv:{conversation_id}"
    return ""


def model_call(*, device: Any, scene: str, purpose: str, model: str, start_ns: int, status: int,
               prompt: int = 0, completion: int = 0, cached: int = 0, conversation_id: str = "",
               subtask: bool = False, error: str = "") -> None:
    """一次模型调用（网关转发到上游）。只有用量和耗时，没有内容。"""
    attrs: dict[str, Any] = {
        "gen_ai.operation.name": "chat",
        "gen_ai.system": "flyknitbuddy",
        "gen_ai.request.model": model,
        "gen_ai.usage.input_tokens": max(0, prompt),
        "gen_ai.usage.output_tokens": max(0, completion),
        "flyknit.usage.cached_tokens": max(0, cached),
        "flyknit.scene": scene,
        "flyknit.purpose": purpose,
        "flyknit.subtask": subtask,
        "http.response.status_code": status,
        "flyknit.conversation_id": conversation_id,
        **_device_attrs(device),
    }
    emit(f"chat {model or scene}", start_ns, time.time_ns(), attrs,
         trace_key(device.id if device else 0, "", conversation_id), SPAN_KIND_CLIENT,
         error or (f"HTTP {status}" if status >= 400 else ""))


def _ns(when: datetime | None) -> int:
    if when is None:
        return time.time_ns()
    if when.tzinfo is None:
        when = when.replace(tzinfo=timezone.utc)
    return int(when.timestamp() * 1_000_000_000)


def tool_call(*, device: Any, item: Any, mcp_source: str = "") -> None:
    """员工端上报的一次工具调用（审计）。不带参数和结果，只带工具名、风险判定、谁批准的、成功与否。"""
    start = _ns(getattr(item, "occurred_at", None))
    attrs: dict[str, Any] = {
        "gen_ai.operation.name": "execute_tool",
        "gen_ai.tool.name": item.tool_name,
        "flyknit.tool.risk": item.risk,
        "flyknit.tool.decision": item.decision,
        "flyknit.tool.status": item.status,
        "flyknit.actor": item.actor,
        "flyknit.scene": item.scene,
        "flyknit.task_id": item.task_id,
        "flyknit.grant_id": item.grant_id,
        "flyknit.subtask_id": item.subtask_id,
        "flyknit.mcp_source": mcp_source,
        "flyknit.conversation_id": item.conversation_id,
        **_device_attrs(device),
    }
    error = "被安全策略拦下" if item.decision == "blocked" else "员工拒绝" if item.decision == "rejected" else \
        "执行失败" if item.status == "error" else ""
    emit(f"execute_tool {item.tool_name}", start, start, attrs,
         trace_key(device.id if device else 0, item.task_id, item.conversation_id), SPAN_KIND_INTERNAL, error)


def body(spans: list[dict]) -> dict:
    return {"resourceSpans": [{
        "resource": {"attributes": [
            _attr("service.name", state.config.get("service_name") or "flyknitbuddy"),
            _attr("service.version", __version__),
        ]},
        "scopeSpans": [{"scope": {"name": "flyknitbuddy.server", "version": __version__}, "spans": spans}],
    }]}


def _headers(config: dict) -> dict[str, str]:
    out = {"Content-Type": "application/json"}
    for h in config.get("headers") or []:
        name = str(h.get("name") or "").strip()
        if not name:
            continue
        try:
            out[name] = decrypt(h.get("value_enc") or "") if h.get("value_enc") else ""
        except Exception:  # noqa: BLE001 - 密钥换过解不开：这个头不带，采集器会拒绝，记成错误
            continue
    return out


async def send(config: dict, spans: list[dict]) -> str:
    """发一批。成功返回空串，失败返回原因（不抛）。"""
    try:
        async with httpx.AsyncClient(timeout=max(1, int(config.get("timeout_ms") or 5000)) / 1000) as http:
            resp = await http.post(traces_url(config["endpoint"]), content=json.dumps(body(spans)), headers=_headers(config))
    except httpx.TimeoutException:
        return "采集器超时没有回应"
    except httpx.HTTPError as e:
        return f"连不上采集器：{type(e).__name__}"
    if resp.status_code >= 300:
        return f"采集器返回 HTTP {resp.status_code}：{resp.text[:200]}"
    return ""


async def flush() -> int:
    """把队列里的发出去（每批最多 500 条）。返回发出去的条数；失败的这批丢掉（记数），不重试，免得越积越多。"""
    sent = 0
    while state.queue and on():
        batch = [state.queue.popleft() for _ in range(min(BATCH, len(state.queue)))]
        error = await send(state.config, batch)
        if error:
            state.failed_batches += 1
            state.dropped += len(batch)
            state.last_error = error
            state.last_error_at = datetime.now(timezone.utc)
            log.warning("OpenTelemetry 导出失败：%s（丢弃 %s 条）", error, len(batch))
            break
        sent += len(batch)
        state.sent += len(batch)
        state.last_sent_at = datetime.now(timezone.utc)
    if not on():
        state.queue.clear()
    return sent


def stats() -> dict:
    return {"queued": len(state.queue), "sent": state.sent, "dropped": state.dropped, "failed_batches": state.failed_batches,
            "last_error": state.last_error, "last_error_at": state.last_error_at, "last_sent_at": state.last_sent_at}


async def run_forever(maker: async_sessionmaker) -> None:
    last_reload = 0.0
    while True:
        try:
            if time.monotonic() - last_reload > RELOAD_SECONDS:
                async with maker() as session:
                    await load(session)
                last_reload = time.monotonic()
            await flush()
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001 - 导出出错不能让后台任务退出
            log.exception("OpenTelemetry 导出循环出错")
        await asyncio.sleep(FLUSH_SECONDS)
