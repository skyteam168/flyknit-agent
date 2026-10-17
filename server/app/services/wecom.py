"""
企业微信「智能机器人」通道：长连接模式收消息、回消息。

和钉钉 Stream 一样是服务端主动连出去（wss://openws.work.weixin.qq.com），不需要公网地址和可信 IP。
协议按企业微信官方 SDK（WecomTeam/wecom-aibot-python-sdk）实现：

- 连上后发 {"cmd": "aibot_subscribe", "headers": {"req_id"}, "body": {"bot_id", "secret"}}，
  回执帧不带 cmd，headers.req_id 相同、errcode 为 0 表示认证通过
- 每 30 秒发一次 {"cmd": "ping"}，连续两次没回执就当连接断了，重连
- 收消息：{"cmd": "aibot_msg_callback", "headers": {"req_id"}, "body": {msgid, chattype, from: {userid}, msgtype, text}}
- 回复这条消息：{"cmd": "aibot_respond_msg", "headers": {"req_id": 回调里的 req_id}, "body": {"msgtype": "stream", ...}}
- 主动发（任务做完时回调早就过去了）：{"cmd": "aibot_send_msg", "body": {"chatid": userid, "msgtype": "markdown", ...}}
- 每条发出去的帧都有一个同 req_id、带 errcode 的回执
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import time
from datetime import datetime, timedelta, timezone
from typing import Awaitable, Callable

from .dingtalk import BotMessage, StreamStatus

log = logging.getLogger("flyknit.wecom")

WS_URL = "wss://openws.work.weixin.qq.com"
HEARTBEAT = 30.0
MAX_MISSED_PONG = 2
ACK_TIMEOUT = 10.0
#: 收到消息后多久内的回复走「回复这条消息」，之后（任务做完时）改为主动发送
RESPOND_WINDOW = timedelta(seconds=60)
#: 企业微信 markdown 正文上限 4096 字节，留点余量
MAX_REPLY_BYTES = 3800


def req_id(prefix: str) -> str:
    return f"{prefix}_{int(time.time() * 1000)}_{os.urandom(4).hex()}"


def clip_bytes(text: str, limit: int = MAX_REPLY_BYTES) -> str:
    text = text.strip()
    if len(text.encode("utf-8")) <= limit:
        return text
    tail = "\n\n…（内容较长，完整结果请在电脑上的 FlyknitBuddy 里查看）"
    budget = limit - len(tail.encode("utf-8"))
    out = text.encode("utf-8")[:budget].decode("utf-8", "ignore")
    return out + tail


def parse_callback(frame: dict) -> BotMessage | None:
    """把消息回调帧变成通道里统一的消息；不是文字或拿不到发消息的人就返回 None。"""
    if frame.get("cmd") != "aibot_msg_callback":
        return None
    body = frame.get("body") or {}
    userid = str((body.get("from") or {}).get("userid") or "")
    if not userid:
        return None
    msgtype = body.get("msgtype")
    if msgtype == "text":
        text = str((body.get("text") or {}).get("content") or "")
    elif msgtype == "mixed":
        items = ((body.get("mixed") or {}).get("msg_item")) or []
        text = "".join(str((i.get("text") or {}).get("content") or "") for i in items if isinstance(i, dict) and i.get("msgtype") == "text")
    else:
        text = ""
    rid = str((frame.get("headers") or {}).get("req_id") or "")
    expires = datetime.now(timezone.utc) + RESPOND_WINDOW
    return BotMessage(
        msg_id=str(body.get("msgid") or ""),
        staff_id=userid,
        nick="",  # 长连接回调里只有 userid，没有姓名
        text=text.strip(),
        conversation_type="1" if body.get("chattype", "single") == "single" else "2",
        # 回复用的「令牌」：回调的 req_id，短时间内可以直接回复这条消息
        session_webhook=rid,
        webhook_expires_ms=int(expires.timestamp() * 1000),
        robot_code=str(body.get("aibotid") or ""),
        channel="wecom",
    )


class WeComBot:
    """一条到企业微信的长连接：收消息，并在同一条连接上回消息。"""

    def __init__(self, bot_id: str, secret: str, url: str = WS_URL):
        self.bot_id = bot_id
        self._secret = secret
        self.url = url
        self._ws = None
        self._pending: dict[str, asyncio.Future] = {}
        self._missed = 0

    # ---- 发帧并等回执 ----

    async def _request(self, frame: dict) -> dict:
        ws = self._ws
        if ws is None:
            raise RuntimeError("企业微信还没连上")
        rid = frame["headers"]["req_id"]
        fut: asyncio.Future = asyncio.get_running_loop().create_future()
        self._pending[rid] = fut
        try:
            await ws.send(json.dumps(frame, ensure_ascii=False))
            ack = await asyncio.wait_for(fut, ACK_TIMEOUT)
        finally:
            self._pending.pop(rid, None)
        if ack.get("errcode", 0) != 0:
            raise RuntimeError(f"企业微信返回错误 {ack.get('errcode')}：{ack.get('errmsg')}")
        return ack

    async def reply(self, webhook: str, webhook_expires: datetime | None, staff_id: str, title: str, text: str) -> None:
        """刚收到的消息直接回复它；时间过了（任务做完时）改为主动发给这个人。"""
        body = clip_bytes(text)
        now = datetime.now(timezone.utc)
        if webhook and webhook_expires and webhook_expires.replace(tzinfo=timezone.utc) > now:
            try:
                await self._request({
                    "cmd": "aibot_respond_msg",
                    "headers": {"req_id": webhook},
                    "body": {"msgtype": "stream", "stream": {"id": req_id("stream"), "finish": True, "content": body}},
                })
                return
            except Exception as exc:  # noqa: BLE001  回复窗口过了之类，改用主动发送
                log.warning("企业微信回复消息失败，改为主动发送：%s", exc)
        await self._request({
            "cmd": "aibot_send_msg",
            "headers": {"req_id": req_id("aibot_send_msg")},
            "body": {"chatid": staff_id, "msgtype": "markdown", "markdown": {"content": body}},
        })

    # ---- 收帧 ----

    def _on_frame(self, frame: dict) -> BotMessage | None:
        """处理一帧：回执交给等着的请求，消息回调变成 BotMessage。"""
        if frame.get("cmd") == "aibot_msg_callback":
            return parse_callback(frame)
        if frame.get("cmd") == "aibot_event_callback":
            return None  # 进入会话、卡片点击等事件：目前不处理
        rid = str((frame.get("headers") or {}).get("req_id") or "")
        if rid.startswith("ping"):
            self._missed = 0
        if (fut := self._pending.get(rid)) is not None and not fut.done():
            fut.set_result(frame)
        return None

    async def _heartbeat(self) -> None:
        while True:
            await asyncio.sleep(HEARTBEAT)
            if self._missed >= MAX_MISSED_PONG:
                log.warning("企业微信心跳连续 %d 次没回执，重连", self._missed)
                if self._ws is not None:
                    await self._ws.close()
                return
            self._missed += 1
            if self._ws is not None:
                await self._ws.send(json.dumps({"cmd": "ping", "headers": {"req_id": req_id("ping")}}))

    async def run(self, status: StreamStatus, on_message: Callable[[BotMessage], Awaitable[None]]) -> None:
        """一直连着，断了就退避重连（最长 60 秒），直到被取消。"""
        from websockets.asyncio.client import connect

        backoff = 2.0
        while True:
            beat: asyncio.Task | None = None
            reader: asyncio.Task | None = None
            try:
                async with connect(self.url, ping_interval=None, max_size=4 * 1024 * 1024) as ws:
                    self._ws = ws
                    self._missed = 0
                    reader = asyncio.create_task(self._read(ws, status, on_message))
                    await self._request({
                        "cmd": "aibot_subscribe",
                        "headers": {"req_id": req_id("aibot_subscribe")},
                        "body": {"bot_id": self.bot_id, "secret": self._secret},
                    })
                    status.connected = True
                    status.since = datetime.now(timezone.utc)
                    status.last_error = ""
                    backoff = 2.0
                    log.info("企业微信长连接已认证")
                    beat = asyncio.create_task(self._heartbeat())
                    await reader
            except asyncio.CancelledError:
                status.connected = False
                raise
            except Exception as exc:  # noqa: BLE001  凭证错误、网络断开都在这里，记下来给后台看
                status.last_error = str(exc)[:300]
                log.warning("企业微信长连接出错，%.0f 秒后重连：%s", backoff, exc)
            finally:
                for t in (beat, reader):
                    if t is not None and not t.done():
                        t.cancel()
                self._ws = None
                for fut in self._pending.values():
                    if not fut.done():
                        fut.set_exception(RuntimeError("企业微信连接已断开"))
                self._pending.clear()
            status.connected = False
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 60.0)

    async def _read(self, ws, status: StreamStatus, on_message: Callable[[BotMessage], Awaitable[None]]) -> None:
        async for raw in ws:
            try:
                frame = json.loads(raw if isinstance(raw, str) else raw.decode("utf-8", "replace"))
            except ValueError:
                continue
            message = self._on_frame(frame)
            if message is not None:
                status.received += 1
                status.last_message_at = datetime.now(timezone.utc)
                asyncio.create_task(_safe(on_message(message)))


async def _safe(work: Awaitable[None]) -> None:
    try:
        await work
    except Exception:  # noqa: BLE001
        log.exception("处理企业微信消息失败")
