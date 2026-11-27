"""
钩子（docs/hooks.md）：IT 配置的 HTTP 地址，在 AI 调用工具前后、一轮任务结束时收到通知。

- pre_tool：员工端执行一个匹配的工具前，先问服务端（/client/hooks/check），服务端去问钩子，回答只能收紧：
  allow（照旧）/ ask（必须员工确认，不能记成以后自动）/ deny（拦下）。几个钩子都匹配时取最严的；
  钩子没回应、出错按这个钩子的 on_error 处理（默认 ask）。
- post_tool、stop：只通知，不影响任务（后台发，员工端不等）。

地址和签名密钥只在服务端；员工端只拿到「哪些事件、哪些工具」用来少问几次（/client/config 的 hooks）。
匹配和范围在服务端再算一遍：员工端报来什么工具，服务端就按自己的配置决定问哪些钩子。
发出去的参数：员工端先打码（密码、令牌、Cookie），服务端再把每个字符串截到 2000 字、整体截到 2 万字。
"""

from __future__ import annotations

import asyncio
import fnmatch
import hashlib
import hmac
import json
import logging
import re
import time
from datetime import datetime, timezone
from typing import Any

import httpx
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from ..crypto import decrypt
from ..models import Device, Hook, HookCall
from . import item_scope, settings_store

log = logging.getLogger("flyknit.hooks")

KEY = "hooks"
EVENTS = ("pre_tool", "post_tool", "stop")
DECISIONS = ("allow", "ask", "deny")
ON_ERROR = ("ask", "deny", "allow")
MAX_STRING = 2000
MAX_ARGS = 20000
MAX_SUMMARY = 1000
MAX_REASON = 300

#: 测试时换成 httpx.MockTransport
transport: httpx.AsyncBaseTransport | None = None


async def master_on(session: AsyncSession) -> bool:
    return bool((await settings_store.get_value(session, KEY, {})).get("enabled", False))


def tool_matches(spec: str, tool: str) -> bool:
    parts = [p.strip() for p in (spec or "*").split(",") if p.strip()]
    return not parts or any(fnmatch.fnmatchcase(tool, p) for p in parts)


def matches(hook: Hook, event: str, tool: str = "", args_json: str = "") -> bool:
    if not hook.enabled or event not in (hook.events or []):
        return False
    if event == "stop":
        return True
    if not tool_matches(hook.tools, tool):
        return False
    if hook.match:
        try:
            return re.search(hook.match, args_json[:MAX_ARGS], re.IGNORECASE) is not None
        except re.error:
            return False  # 保存时校验过；万一坏了，这一条不用
    return True


async def applicable(session: AsyncSession, device: Device | None, event: str) -> list[Hook]:
    rows = (await session.scalars(select(Hook).where(Hook.enabled.is_(True)).order_by(Hook.id))).all()
    return [h for h in rows if event in (h.events or []) and item_scope.in_scope(h, device)]


async def client_specs(session: AsyncSession, device: Device) -> list[dict]:
    """员工端配置里下发的：只有匹配条件，没有地址和密钥。总开关关着时为空。"""
    if not await master_on(session):
        return []
    rows = (await session.scalars(select(Hook).where(Hook.enabled.is_(True)).order_by(Hook.id))).all()
    return [
        {"id": h.id, "events": list(h.events or []), "tools": h.tools or "*", "match": h.match or "",
         "on_error": h.on_error, "timeout_ms": h.timeout_ms}
        for h in rows if item_scope.in_scope(h, device)
    ]


def _trim(value: Any) -> Any:
    if isinstance(value, str):
        return value if len(value) <= MAX_STRING else value[:MAX_STRING] + f"…（共 {len(value)} 字，已截断）"
    if isinstance(value, list):
        return [_trim(v) for v in value[:200]]
    if isinstance(value, dict):
        return {str(k)[:100]: _trim(v) for k, v in list(value.items())[:200]}
    return value


def arguments(args_json: str) -> Any:
    """工具参数：能解析就逐个字段截断，不能就当一段文字截断；整体不超过 2 万字。"""
    try:
        parsed = _trim(json.loads(args_json or "{}"))
    except (ValueError, TypeError):
        parsed = _trim(args_json or "")
    text = json.dumps(parsed, ensure_ascii=False)
    return parsed if len(text) <= MAX_ARGS else text[:MAX_ARGS] + "…（已截断）"


def payload(event: str, hook: Hook, device: Device | None, data: dict) -> dict:
    body: dict[str, Any] = {
        "event": event,
        "hook": hook.name,
        "time": datetime.now(timezone.utc).isoformat(),
        "device": {
            "id": device.id if device else None,
            "machine_name": device.machine_name if device else "",
            "owner": (device.owner or "") if device else "",
            "department": (device.department or "") if device else "",
        },
        "conversation_id": str(data.get("conversation_id", ""))[:64],
        "run_id": str(data.get("run_id", ""))[:64],
        "task_id": str(data.get("task_id", ""))[:64],
        "grant_id": str(data.get("grant_id", ""))[:64],
    }
    if data.get("test"):
        body["test"] = True
    if event in ("pre_tool", "post_tool"):
        body["tool"] = {"name": str(data.get("tool", ""))[:100], "arguments": arguments(str(data.get("arguments", "")))}
    if event == "post_tool":
        body["result"] = {"ok": bool(data.get("ok")), "summary": str(data.get("summary", ""))[:MAX_SUMMARY]}
    if event == "stop":
        body["stop"] = {"status": str(data.get("status", ""))[:20], "summary": str(data.get("summary", ""))[:MAX_SUMMARY]}
    return body


def sign(secret: str, body: bytes) -> str:
    return "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()


def _secret(hook: Hook) -> str:
    if not hook.secret_enc:
        return ""
    try:
        return decrypt(hook.secret_enc)
    except Exception:  # noqa: BLE001 - 密钥换过、解不开：不签名，钩子那边会拒绝，按出错处理
        return ""


async def send(hook: Hook, event: str, body: dict) -> tuple[str | None, str, str, int]:
    """发给一个钩子。返回（决定 / None、原因、出错原因、耗时毫秒）。只有 pre_tool 看决定。"""
    raw = json.dumps(body, ensure_ascii=False).encode()
    headers = {"Content-Type": "application/json", "X-Flyknit-Event": event, "User-Agent": "FlyknitBuddy-Hooks"}
    secret = _secret(hook)
    if secret:
        headers["X-Flyknit-Signature"] = sign(secret, raw)
    started = time.monotonic()
    try:
        async with httpx.AsyncClient(timeout=hook.timeout_ms / 1000, verify=not hook.skip_tls_verify, transport=transport) as client:
            resp = await client.post(hook.url, content=raw, headers=headers)
    except httpx.TimeoutException:
        return None, "", "钩子超时没有回应", int((time.monotonic() - started) * 1000)
    except httpx.HTTPError as e:
        return None, "", f"连不上钩子：{type(e).__name__}", int((time.monotonic() - started) * 1000)
    latency = int((time.monotonic() - started) * 1000)
    if resp.status_code >= 300:
        return None, "", f"钩子返回 HTTP {resp.status_code}", latency
    if event != "pre_tool":
        return "sent", "", "", latency
    try:
        answer = resp.json()
        decision = str(answer.get("decision", "")).lower()
        reason = str(answer.get("reason", "") or "")[:MAX_REASON]
    except (ValueError, AttributeError):
        return None, "", "钩子的回答不是 JSON", latency
    decision = {"block": "deny", "confirm": "ask"}.get(decision, decision)
    if decision not in DECISIONS:
        return None, "", "钩子的回答里 decision 不是 allow / ask / deny", latency
    return decision, reason, "", latency


def stricter(a: str, b: str) -> str:
    return a if DECISIONS.index(a) >= DECISIONS.index(b) else b


async def check(session: AsyncSession, device: Device, data: dict) -> dict:
    """pre_tool：问所有匹配的钩子，取最严的。总开关关着、没有匹配的：allow。"""
    if not await master_on(session):
        return {"decision": "allow", "reason": "", "hooks": []}
    tool = str(data.get("tool", ""))
    args_json = str(data.get("arguments", ""))
    hooks = [h for h in await applicable(session, device, "pre_tool") if matches(h, "pre_tool", tool, args_json)]
    if not hooks:
        return {"decision": "allow", "reason": "", "hooks": []}
    results = await asyncio.gather(*(send(h, "pre_tool", payload("pre_tool", h, device, data)) for h in hooks))
    final, reasons, out = "allow", [], []
    for hook, (decision, reason, error, latency) in zip(hooks, results):
        if decision is None:
            decision = hook.on_error if hook.on_error in ON_ERROR else "ask"
            reason = f"安全检查「{hook.name}」没能完成（{error}）"
        final = stricter(final, decision)
        if decision != "allow" and reason:
            reasons.append(reason)
        elif decision != "allow":
            reasons.append(f"安全检查「{hook.name}」要求" + ("拦下" if decision == "deny" else "确认"))
        out.append({"id": hook.id, "decision": decision, "error": error})
        session.add(HookCall(hook_id=hook.id, event="pre_tool", device_id=device.id,
                             conversation_id=str(data.get("conversation_id", ""))[:64], tool_name=tool[:100],
                             decision=decision, reason=reason[:500], error=error[:300], latency_ms=latency))
    await session.commit()
    return {"decision": final, "reason": "；".join(reasons)[:MAX_REASON * 2], "hooks": out}


async def notify(maker: async_sessionmaker, device_id: int, event: str, data: dict) -> None:
    """post_tool / stop：后台发给匹配的钩子，结果只记下来，不影响任务。"""
    try:
        async with maker() as session:
            if not await master_on(session):
                return
            device = await session.get(Device, device_id)
            tool = str(data.get("tool", ""))
            hooks = [h for h in await applicable(session, device, event) if matches(h, event, tool, str(data.get("arguments", "")))]
            if not hooks:
                return
            results = await asyncio.gather(*(send(h, event, payload(event, h, device, data)) for h in hooks))
            for hook, (decision, _, error, latency) in zip(hooks, results):
                session.add(HookCall(hook_id=hook.id, event=event, device_id=device_id,
                                     conversation_id=str(data.get("conversation_id", ""))[:64], tool_name=tool[:100],
                                     decision=decision or "", error=error[:300], latency_ms=latency))
            await session.commit()
    except Exception:  # noqa: BLE001 - 通知失败不影响任何事，记日志
        log.exception("钩子通知失败（%s）", event)
