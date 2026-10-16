"""
钉钉机器人通道：用 Stream 模式收消息，用会话 webhook 或机器人单聊接口回消息。

Stream 模式是服务端主动连到钉钉（先 HTTPS 换一个带票据的地址，再连 WebSocket），
所以服务器不需要公网地址和备案域名，内网能出网就行。

协议要点（钉钉开放平台「Stream 模式」）：
- POST https://api.dingtalk.com/v1.0/gateway/connections/open 拿 endpoint 和 ticket，连 `endpoint?ticket=...`
- 每一帧是 JSON：{"type": "SYSTEM|EVENT|CALLBACK", "headers": {"topic", "messageId", ...}, "data": "<JSON 字符串>"}
- SYSTEM/ping 原样回 data；SYSTEM/disconnect 表示服务端要断开，重连即可
- CALLBACK /v1.0/im/bot/messages/get 是机器人收到的消息，必须回 ACK（code 200 + 同一个 messageId）
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Awaitable, Callable

import httpx

log = logging.getLogger("flyknit.dingtalk")

API = "https://api.dingtalk.com"
BOT_TOPIC = "/v1.0/im/bot/messages/get"
#: 钉钉 markdown 消息正文的长度上限比这个大一些，留点余量
MAX_REPLY = 3800


@dataclass
class BotMessage:
    """机器人收到的一条消息里我们关心的部分。"""

    msg_id: str
    staff_id: str
    nick: str
    text: str
    #: 1 单聊、2 群聊
    conversation_type: str
    session_webhook: str
    webhook_expires_ms: int
    robot_code: str
    #: 从哪个通道来的：dingtalk / wecom（企业微信借用 session_webhook 存回调的 req_id）
    channel: str = "dingtalk"

    @property
    def webhook_expires(self) -> datetime | None:
        if not self.webhook_expires_ms:
            return None
        return datetime.fromtimestamp(self.webhook_expires_ms / 1000, tz=timezone.utc)


def parse_bot_message(data: dict) -> BotMessage | None:
    """从回调数据里取出消息；不是文字消息、或者拿不到员工 ID 的返回 None。"""
    staff = str(data.get("senderStaffId") or "")
    if not staff:
        return None  # 外部联系人没有 staffId，不接
    msgtype = data.get("msgtype")
    if msgtype == "text":
        text = str((data.get("text") or {}).get("content") or "")
    elif msgtype == "richText":
        parts = ((data.get("content") or {}).get("richText")) or []
        text = "".join(str(p.get("text") or "") for p in parts if isinstance(p, dict))
    else:
        text = ""
    return BotMessage(
        msg_id=str(data.get("msgId") or ""),
        staff_id=staff,
        nick=str(data.get("senderNick") or ""),
        text=text.strip(),
        conversation_type=str(data.get("conversationType") or "1"),
        session_webhook=str(data.get("sessionWebhook") or ""),
        webhook_expires_ms=int(data.get("sessionWebhookExpiredTime") or 0),
        robot_code=str(data.get("robotCode") or ""),
    )


def handle_frame(raw: str) -> tuple[dict | None, BotMessage | None, str]:
    """
    处理 Stream 推来的一帧。

    返回（要回给钉钉的帧，收到的机器人消息，控制信号）。控制信号是 "disconnect" 时调用方应重连。
    """
    try:
        frame = json.loads(raw)
    except ValueError:
        return None, None, ""
    headers = frame.get("headers") or {}
    topic = headers.get("topic") or ""
    message_id = headers.get("messageId") or ""
    kind = frame.get("type") or ""
    if kind == "SYSTEM":
        if topic == "ping":
            return _ack(message_id, frame.get("data") or ""), None, ""
        if topic == "disconnect":
            return None, None, "disconnect"
        return None, None, ""
    if kind == "CALLBACK" and topic == BOT_TOPIC:
        message = None
        try:
            message = parse_bot_message(json.loads(frame.get("data") or "{}"))
        except ValueError:
            log.warning("钉钉机器人消息格式不对，已忽略")
        # 不管能不能处理都先 ACK，不然钉钉会重试投递
        return _ack(message_id, json.dumps({"response": None})), message, ""
    # 其他事件（我们没订阅的）也 ACK 掉
    return _ack(message_id, json.dumps({"status": "SUCCESS", "message": "ok"})), None, ""


def _ack(message_id: str, data: str) -> dict:
    return {
        "code": 200,
        "headers": {"contentType": "application/json", "messageId": message_id},
        "message": "OK",
        "data": data,
    }


_FENCE = re.compile(r"```([\w-]*)[^\n]*\n(.*?)```", re.S)
_PICTURES = {"chart", "flyknit-chart", "mermaid", "mmd", "dot", "graphviz", "gv"}


def for_dingtalk(text: str) -> str:
    """
    员工端的回答里可能有图表、流程图、界面卡片这类代码块，钉钉里只会显示成一堆代码。
    图换成一句「请在电脑上查看」，卡片拆成普通列表。
    """
    def repl(m: re.Match) -> str:
        lang = m.group(1).lower()
        body = m.group(2)
        if lang in _PICTURES:
            return "（这里有一张图，请在电脑上的 FlyknitBuddy 里查看）"
        if lang not in ("ui", "flyknit-ui"):
            return m.group(0)
        try:
            spec = json.loads(body)
            items = spec.get("items") or spec.get("columns") or spec.get("steps") or []
        except (ValueError, AttributeError):
            return ""
        lines = []
        if spec.get("title"):
            lines.append(f"**{spec['title']}**")
        if spec.get("kind") == "actions":
            lines.append("接下来可以直接回复：")
        for it in items[:12]:
            if isinstance(it, str):
                lines.append(f"- {it}")
                continue
            if not isinstance(it, dict):
                continue
            title = str(it.get("title") or it.get("label") or it.get("name") or "")
            extra = it.get("prompt") if spec.get("kind") == "actions" else (it.get("value") or it.get("detail") or "")
            unit = str(it.get("unit") or "") if it.get("value") is not None else ""
            lines.append(f"- {title}" + (f"：{extra}{unit}" if extra and extra != title else ""))
        return "\n".join(lines)

    return _FENCE.sub(repl, text)


def clip(text: str) -> str:
    text = text.strip()
    if len(text) <= MAX_REPLY:
        return text
    return text[:MAX_REPLY] + "\n\n…（内容较长，完整结果请在电脑上的 FlyknitBuddy 里查看）"


class DingTalkApi:
    """回消息用的几个开放接口。access_token 缓存到过期前 5 分钟。"""

    def __init__(self, http: httpx.AsyncClient, client_id: str, client_secret: str, robot_code: str):
        self._http = http
        self.client_id = client_id
        self._secret = client_secret
        self.robot_code = robot_code or client_id
        self._token = ""
        self._token_until = 0.0

    async def access_token(self) -> str:
        if self._token and time.monotonic() < self._token_until:
            return self._token
        r = await self._http.post(f"{API}/v1.0/oauth2/accessToken",
                                  json={"appKey": self.client_id, "appSecret": self._secret})
        r.raise_for_status()
        body = r.json()
        self._token = body["accessToken"]
        self._token_until = time.monotonic() + max(60, int(body.get("expireIn", 7200)) - 300)
        return self._token

    async def open_stream(self) -> tuple[str, str]:
        """换一个 Stream 连接地址和票据。"""
        r = await self._http.post(f"{API}/v1.0/gateway/connections/open", json={
            "clientId": self.client_id,
            "clientSecret": self._secret,
            "subscriptions": [
                {"type": "EVENT", "topic": "*"},
                {"type": "CALLBACK", "topic": BOT_TOPIC},
            ],
            "ua": "flyknit-server",
        })
        if r.status_code >= 400:
            raise RuntimeError(f"钉钉拒绝了连接（HTTP {r.status_code}）：{r.text[:200]}")
        body = r.json()
        return body["endpoint"], body["ticket"]

    async def reply(self, webhook: str, webhook_expires: datetime | None, staff_id: str, title: str, text: str) -> None:
        """会话 webhook 还没过期就用它（不占调用额度），否则走机器人单聊接口。"""
        body = clip(text)
        now = datetime.now(timezone.utc)
        if webhook and webhook_expires and webhook_expires.replace(tzinfo=timezone.utc) > now:
            r = await self._http.post(webhook, json={"msgtype": "markdown", "markdown": {"title": title, "text": body}})
            if r.status_code < 400 and (r.json() or {}).get("errcode", 0) == 0:
                return
            log.warning("钉钉会话 webhook 回复失败，改用单聊接口：HTTP %s %s", r.status_code, r.text[:200])
        token = await self.access_token()
        r = await self._http.post(
            f"{API}/v1.0/robot/oToMessages/batchSend",
            headers={"x-acs-dingtalk-access-token": token},
            json={
                "robotCode": self.robot_code,
                "userIds": [staff_id],
                "msgKey": "sampleMarkdown",
                "msgParam": json.dumps({"title": title, "text": body}, ensure_ascii=False),
            },
        )
        if r.status_code >= 400:
            raise RuntimeError(f"钉钉单聊消息发送失败（HTTP {r.status_code}）：{r.text[:200]}")


@dataclass
class StreamStatus:
    connected: bool = False
    since: datetime | None = None
    last_error: str = ""
    last_message_at: datetime | None = None
    received: int = field(default=0)


async def run_stream(api: DingTalkApi, status: StreamStatus,
                     on_message: Callable[[BotMessage], Awaitable[None]]) -> None:
    """一直连着钉钉 Stream，断了就退避重连（最长 60 秒），直到被取消。"""
    from websockets.asyncio.client import connect

    backoff = 2.0
    while True:
        try:
            endpoint, ticket = await api.open_stream()
            async with connect(f"{endpoint}?ticket={ticket}", ping_interval=30, max_size=4 * 1024 * 1024) as ws:
                status.connected = True
                status.since = datetime.now(timezone.utc)
                status.last_error = ""
                backoff = 2.0
                log.info("钉钉 Stream 已连接")
                async for raw in ws:
                    reply, message, signal = handle_frame(raw if isinstance(raw, str) else raw.decode("utf-8", "replace"))
                    if reply is not None:
                        await ws.send(json.dumps(reply, ensure_ascii=False))
                    if message is not None:
                        status.received += 1
                        status.last_message_at = datetime.now(timezone.utc)
                        # 处理（查库、回复）放到后台，别挡住收下一帧和回 ping
                        asyncio.create_task(_safe(on_message(message)))
                    if signal == "disconnect":
                        log.info("钉钉要求断开，马上重连")
                        break
        except asyncio.CancelledError:
            status.connected = False
            raise
        except Exception as exc:  # noqa: BLE001  网络、凭证错误都在这里，记下来给后台看
            status.last_error = str(exc)[:300]
            log.warning("钉钉 Stream 连接出错，%.0f 秒后重连：%s", backoff, exc)
        status.connected = False
        await asyncio.sleep(backoff)
        backoff = min(backoff * 2, 60.0)


async def _safe(work: Awaitable[None]) -> None:
    try:
        await work
    except Exception:  # noqa: BLE001
        log.exception("处理钉钉消息失败")
