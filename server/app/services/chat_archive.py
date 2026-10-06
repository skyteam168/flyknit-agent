"""聊天记录归档。

内容本来就从网关过一遍，所以在这里落库，不用客户端再传一份——员工删掉本地
history.db 也绕不过去，而且离线时压根调不了模型，覆盖是完整的。

这里最容易写错的是**增量**：请求体里带的是整段历史，每轮重发一遍。照搬着存
十轮对话会变成五十五条消息，而且越往后越慢。所以只取这一轮新增的部分。
"""

from __future__ import annotations

import json
import logging
from dataclasses import dataclass, field
from typing import Any

log = logging.getLogger("flyknit.archive")

#: 单条消息入库前截断的长度。正常对话远到不了，这是防失控用的
MAX_CONTENT = 100_000

#: 这些角色不进归档：系统提示词、记忆注入、工具输出每轮都在重复，
#: 对后台没有价值，只会把库撑大。后台要查工具调用有 audit_logs。
ARCHIVED_ROLES = ("user", "assistant")


@dataclass
class Turn:
    """一轮对话里新产生的东西。"""

    user_content: str = ""
    assistant_content: str = ""
    attachments: int = 0
    extra: dict[str, Any] = field(default_factory=dict)

    @property
    def empty(self) -> bool:
        return not self.user_content and not self.assistant_content


def text_of(content: Any) -> tuple[str, int]:
    """
    把一条消息的 content 规整成纯文本，并数出附件数量。

    content 可能是字符串，也可能是 OpenAI 的分片数组（带图片时就是后者）。
    图片本身不存——base64 图片动辄几百 KB，存了既占地方又没法看。
    """
    if isinstance(content, str):
        return content, 0
    if not isinstance(content, list):
        return "", 0

    parts: list[str] = []
    attachments = 0
    for part in content:
        if not isinstance(part, dict):
            continue
        kind = part.get("type")
        if kind == "text":
            parts.append(str(part.get("text") or ""))
        elif kind in ("image_url", "input_audio", "input_file"):
            attachments += 1
    return "\n".join(p for p in parts if p), attachments


def last_user_message(messages: list[Any]) -> tuple[str, int]:
    """
    取这一轮用户真正说的话。

    从后往前找第一条 user 消息：它后面只可能跟着 assistant 的工具调用和工具结果，
    而那些属于同一轮。中途压缩过上下文也不影响——压缩只动前面的历史。
    """
    for msg in reversed(messages or []):
        if not isinstance(msg, dict):
            continue
        if msg.get("role") == "user":
            return text_of(msg.get("content"))
    return "", 0


def new_user_message(messages: list[Any]) -> tuple[str, int]:
    """
    这次请求新带来的用户消息。

    任务模式一轮里会多次请求：第一次以用户消息结尾，之后每次以工具结果结尾、
    带的还是同一条用户消息。只有第一次算数，否则一轮调几次工具就重复存几遍。
    """
    last = next((m for m in reversed(messages or []) if isinstance(m, dict)), None)
    if last is None or last.get("role") != "user":
        return "", 0
    return text_of(last.get("content"))


def assistant_reply(payload: dict[str, Any]) -> str:
    """从非流式响应里取回答正文。"""
    choices = payload.get("choices")
    if not isinstance(choices, list) or not choices:
        return ""
    message = choices[0].get("message") if isinstance(choices[0], dict) else None
    if not isinstance(message, dict):
        return ""
    text, _ = text_of(message.get("content"))
    return text


class StreamTextScanner:
    """边转发边把流式回答的正文拼起来。和 StreamUsageScanner 一个路子。"""

    def __init__(self) -> None:
        self._parts: list[str] = []
        self._buffer = b""
        self._overflow = False

    @property
    def text(self) -> str:
        return "".join(self._parts)

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
        for choice in payload.get("choices") or []:
            if not isinstance(choice, dict):
                continue
            delta = choice.get("delta")
            if not isinstance(delta, dict):
                continue
            piece = delta.get("content")
            if isinstance(piece, str) and piece:
                if self._overflow:
                    continue
                self._parts.append(piece)
                # 不等到最后才截，免得一个跑飞的回答把内存吃光
                if sum(len(p) for p in self._parts) > MAX_CONTENT:
                    self._overflow = True


def clip(text: str) -> str:
    if len(text) <= MAX_CONTENT:
        return text
    return text[:MAX_CONTENT] + f"…（已截断，原文 {len(text)} 字）"
