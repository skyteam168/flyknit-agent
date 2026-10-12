"""
专家派遣的子任务：状态流转、超时、用量累加，以及网关上的那几道闸。

状态只能从 running 走到终态，而且只走一次：所有结束动作都是 `UPDATE ... WHERE status='running'`，
谁先到算谁的，后到的（员工端交卷晚了、网关发现超时、开关被关）一律不覆盖。
超时不靠后台定时任务：读到、用到这一行时发现过了 deadline 就顺手置为 timed_out。
"""

from __future__ import annotations

import json
import math
import secrets
from datetime import datetime, timezone
from typing import Any

from sqlalchemy import update
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import DevicePolicy, Subtask
from . import chat_archive, security_settings, settings_store

#: 同一设备同一次主任务最多派几次（失败、超时、取消的都算）
MAX_PER_RUN = 3
TOKEN_CAP_RANGE = (1000, 400_000)
TIMEOUT_RANGE = (60, 900)
#: 交卷全文序列化后的上限
RESULT_MAX_BYTES = 64 * 1024
SUMMARY_MAX = 2000
TASK_MAX = 20000

FINAL = ("completed", "partial", "needs_input", "failed", "cancelled", "timed_out")

#: 专家不能再派遣：子任务请求里删掉这个工具
DELEGATE_TOOL = "ask_expert"

#: 上游没给用量时的估算：提示约 3 字符/token，回答约 2 字符/token
PROMPT_CHARS_PER_TOKEN = 3
COMPLETION_CHARS_PER_TOKEN = 2
#: 一张图片按多少字符算（约 1000 token）。图片的 base64 不能按长度算，一张就是几十万字符
IMAGE_CHARS = 3000


def new_id() -> str:
    return "st_" + secrets.token_hex(16)


def utc(value: datetime | None) -> datetime | None:
    """SQLite 读回来不带时区，存进去的时候就是 UTC。"""
    if value is None:
        return None
    return value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value.astimezone(timezone.utc)


def clamp(value: int, bounds: tuple[int, int]) -> int:
    return max(bounds[0], min(bounds[1], int(value)))


async def delegation_enabled(session: AsyncSession, device_id: int) -> bool:
    """这台机器生效的 expert_delegation：全厂默认叠加这台机器的单独设置。"""
    shared = await settings_store.get_security(session)
    mine = await session.get(DevicePolicy, device_id)
    merged = security_settings.effective(
        global_values=shared.get("values"),
        device_values=mine.overrides if mine else None,
        global_locks=shared.get("locks"),
        device_locks=mine.locks if mine else None,
    )
    return bool(merged["expert_delegation"]["value"])


async def close(session: AsyncSession, st: Subtask, status: str, **values: Any) -> bool:
    """
    把一个 running 的子任务结束成 status。只有它还是 running 时才生效，返回这次是不是真的改了。
    不管改没改成，都把 st 刷新成库里的最新状态。
    """
    now = datetime.now(timezone.utc)
    result = await session.execute(
        update(Subtask)
        .where(Subtask.id == st.id, Subtask.status == "running")
        .values(status=status, finished_at=now, **values)
        .execution_options(synchronize_session=False)
    )
    await session.commit()
    await session.refresh(st)
    return (result.rowcount or 0) > 0


async def expire_if_due(session: AsyncSession, st: Subtask) -> Subtask:
    """读到、用到时发现已经过了 deadline 还在 running，就置为 timed_out。"""
    deadline = utc(st.deadline_at)
    if st.status == "running" and deadline is not None and datetime.now(timezone.utc) > deadline:
        await close(session, st, "timed_out", error_code="timeout")
    return st


async def expire_all_due(session: AsyncSession) -> int:
    """后台列表用：一次把所有过期还在 running 的都置为 timed_out。"""
    now = datetime.now(timezone.utc)
    result = await session.execute(
        update(Subtask)
        .where(Subtask.status == "running", Subtask.deadline_at < now)
        .values(status="timed_out", finished_at=now, error_code="timeout")
        .execution_options(synchronize_session=False)
    )
    await session.commit()
    return result.rowcount or 0


def clip_result(result: Any) -> tuple[str, bool]:
    """
    交卷全文序列化成 JSON，超过 64KB 就截断。截断后仍然是一段合法的 JSON：
    {"truncated": true, "original_bytes": n, "partial": "<原 JSON 的前一段>"}，免得后台读出来解析失败。
    """
    if result is None:
        return "", False
    raw = json.dumps(result, ensure_ascii=False)
    size = len(raw.encode("utf-8"))
    if size <= RESULT_MAX_BYTES:
        return raw, False
    budget = RESULT_MAX_BYTES - 200
    while budget > 0:
        partial = raw.encode("utf-8")[:budget].decode("utf-8", errors="ignore")
        out = json.dumps({"truncated": True, "original_bytes": size, "partial": partial}, ensure_ascii=False)
        if len(out.encode("utf-8")) <= RESULT_MAX_BYTES:
            return out, True
        # 引号、反斜杠转义后会变长，按超出的量再收一点
        budget -= len(out.encode("utf-8")) - RESULT_MAX_BYTES + 64
    return json.dumps({"truncated": True, "original_bytes": size, "partial": ""}), True


# ---------- 网关 ----------
class Rejected(Exception):
    """网关拒绝这次子任务请求：HTTP 状态码、error.type、给员工端看的话。"""

    def __init__(self, status: int, kind: str, message: str) -> None:
        super().__init__(message)
        self.status = status
        self.kind = kind
        self.message = message


async def admit(session: AsyncSession, device_id: int, subtask_id: str) -> Subtask:
    """模型网关放行一次子任务请求前的检查。不放行就抛 Rejected；顺序和约定里的表一致。"""
    st = await session.get(Subtask, subtask_id) if subtask_id else None
    if st is None or st.device_id != device_id:
        raise Rejected(403, "subtask_unknown", "没有这个子任务，或者它不属于这台电脑")
    if st.status != "running":
        raise Rejected(409, "subtask_closed", f"子任务已经结束（{st.status}）")
    await expire_if_due(session, st)
    if st.status != "running":
        raise Rejected(409, "subtask_closed", "子任务已超时")
    if not await delegation_enabled(session, device_id):
        await close(session, st, "cancelled", error_code="disabled", cancel_reason="disabled",
                    cancel_requested_at=datetime.now(timezone.utc))
        raise Rejected(409, "subtask_closed", "IT 已关闭「派遣专家」，子任务已取消")
    if (st.prompt_tokens or 0) + (st.completion_tokens or 0) >= (st.token_cap or 0):
        raise Rejected(429, "subtask_budget", f"子任务的 token 预算已用完（上限 {st.token_cap:,}）")
    return st


def _tool_name(tool: Any) -> str:
    if not isinstance(tool, dict):
        return ""
    fn = tool.get("function")
    if isinstance(fn, dict) and isinstance(fn.get("name"), str):
        return fn["name"]
    return tool.get("name") if isinstance(tool.get("name"), str) else ""


def has_tool(body: dict, name: str) -> bool:
    tools = body.get("tools")
    return isinstance(tools, list) and any(_tool_name(t) == name for t in tools)


def strip_tool(body: dict, name: str) -> bool:
    """从 tools 里删掉某个工具，返回删没删。删空了连 tools / tool_choice 一起去掉（有的上游不收空数组）。"""
    if not has_tool(body, name):
        return False
    body["tools"] = [t for t in body["tools"] if _tool_name(t) != name]
    choice = body.get("tool_choice")
    if isinstance(choice, dict) and _tool_name(choice) == name:
        body.pop("tool_choice", None)
    if not body["tools"]:
        body.pop("tools", None)
        body.pop("tool_choice", None)
    return True


def prompt_chars(body: dict) -> int:
    """估算用：这次请求的提示有多少字符。图片按固定字符数算，不按 base64 长度。"""
    total = 0
    for msg in body.get("messages") or []:
        if not isinstance(msg, dict):
            continue
        text, images = chat_archive.text_of(msg.get("content"))
        total += len(text) + images * IMAGE_CHARS
        if msg.get("tool_calls"):
            total += len(json.dumps(msg["tool_calls"], ensure_ascii=False))
    if body.get("tools"):
        total += len(json.dumps(body["tools"], ensure_ascii=False))
    return total


def _choice_chars(part: Any) -> int:
    """一条回答（非流式的 message 或流式的 delta）里正文、思考和工具调用的字符数。"""
    if not isinstance(part, dict):
        return 0
    total = 0
    for key in ("content", "reasoning_content"):
        if isinstance(part.get(key), str):
            total += len(part[key])
    for call in part.get("tool_calls") or []:
        fn = call.get("function") if isinstance(call, dict) else None
        if isinstance(fn, dict):
            total += len(fn.get("name") or "") + len(fn.get("arguments") or "")
    return total


def completion_chars(payload: Any) -> int:
    """非流式响应里回答有多少字符。"""
    if not isinstance(payload, dict):
        return 0
    return sum(_choice_chars(c.get("message")) for c in payload.get("choices") or [] if isinstance(c, dict))


class StreamCharCounter:
    """边转发边数流式回答的字符数（含工具调用参数），上游不报用量时拿它估算。"""

    def __init__(self) -> None:
        self.chars = 0
        self._buffer = b""

    def feed(self, chunk: bytes) -> None:
        self._buffer += chunk
        while b"\n" in self._buffer:
            line, self._buffer = self._buffer.split(b"\n", 1)
            self._line(line)
        if len(self._buffer) > 1_000_000:  # 异常长的一行，丢掉避免占内存
            self._buffer = b""

    def finish(self) -> None:
        if self._buffer:
            self._line(self._buffer)
            self._buffer = b""

    def _line(self, line: bytes) -> None:
        line = line.strip()
        if not line.startswith(b"data:"):
            return
        body = line[5:].strip()
        if not body or body == b"[DONE]":
            return
        try:
            payload = json.loads(body)
        except (ValueError, UnicodeDecodeError):
            return
        if isinstance(payload, dict):
            self.chars += sum(_choice_chars(c.get("delta")) for c in payload.get("choices") or [] if isinstance(c, dict))


def estimate(prompt: int, completion: int, prompt_text_chars: int, answer_chars: int) -> tuple[int, int, int]:
    """
    要记到子任务上的用量：(prompt, completion, estimated_calls 增量)。
    上游两项都是 0 时不能当 0 记——那样预算永远用不完——按字符估算，并记一次估算。
    """
    if prompt > 0 or completion > 0:
        return max(0, prompt), max(0, completion), 0
    return (math.ceil(prompt_text_chars / PROMPT_CHARS_PER_TOKEN),
            math.ceil(answer_chars / COMPLETION_CHARS_PER_TOKEN), 1)


async def add_usage(session: AsyncSession, subtask_id: str, prompt: int, completion: int, estimated: int) -> None:
    """用量累加。用 SQL 里的加法，并发的几次请求不会互相覆盖。终态之后才到的用量也照记（钱已经花了）。"""
    await session.execute(
        update(Subtask)
        .where(Subtask.id == subtask_id)
        .values(
            prompt_tokens=Subtask.prompt_tokens + prompt,
            completion_tokens=Subtask.completion_tokens + completion,
            estimated_calls=Subtask.estimated_calls + estimated,
        )
        .execution_options(synchronize_session=False)
    )
    await session.commit()
