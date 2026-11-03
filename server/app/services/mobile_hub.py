"""
手机端（FlyknitBuddy 安卓 App）。

    手机 ──扫电脑上的二维码──▶ pair ──▶ 手机令牌（挂在那台电脑上，可吊销）
    手机 ══ WebSocket ══▶ 聊天、翻译：服务端直接调模型，逐字推回手机
                    ╰──▶ 办事：排进通道中枢（channel = mobile），员工电脑领走执行，
                         执行过程（进度、需要确认的操作）经这里实时推给手机，结果也推回来

手机没有自己的账号体系：继承绑定的那台电脑那位员工的身份、额度和权限。
进程内内存记连接和待审批，单机部署足够（和通道中枢一样）。
"""

from __future__ import annotations

import asyncio
import json
import logging
import secrets
import time
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

import anyio
import httpx
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import ChannelBinding, ChannelMessage, ChatRecord, Device, MobileDevice, MobilePairCode
from . import chat_archive, model_router, settings_store, usage_store, web_search

log = logging.getLogger("flyknit.mobile")

CONFIG_KEY = "mobile"
DEFAULT_CONFIG = {"enabled": False, "allow_approval": True, "approval_timeout_min": 5}

CHANNEL = "mobile"
PAIR_CODE_TTL = timedelta(minutes=5)
#: 配对码字母表：去掉容易看错的 0/O、1/I/L
_ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"
PAIR_CODE_LEN = 8
#: 同一个来源地址 10 分钟内最多输错几次配对码
MAX_PAIR_FAILURES = 10
PAIR_FAILURE_WINDOW = 600.0

#: 手机上一轮聊天最多带多少条历史、每条多长
MAX_HISTORY = 20
MAX_MESSAGE_CHARS = 8000
LANGUAGES = {"zh": "简体中文", "vi": "越南语", "en": "英语", "km": "柬埔寨语（高棉语）"}


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _aware(t: datetime) -> datetime:
    return t if t.tzinfo else t.replace(tzinfo=timezone.utc)


def external_id(mobile_id: int) -> str:
    """手机在通道中枢里的「外部账号」：channel_bindings / channel_messages 里用它找回是哪部手机。"""
    return f"mobile:{mobile_id}"


def mobile_id_of(external: str) -> int | None:
    if not external.startswith("mobile:"):
        return None
    try:
        return int(external.split(":", 1)[1])
    except ValueError:
        return None


# ---------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------

async def get_config(session: AsyncSession) -> dict:
    cfg = await settings_store.get_value(session, CONFIG_KEY, DEFAULT_CONFIG)
    for k, v in DEFAULT_CONFIG.items():
        cfg.setdefault(k, v)
    return cfg


async def save_config(session: AsyncSession, enabled: bool, allow_approval: bool, approval_timeout_min: int) -> dict:
    cfg = await get_config(session)
    cfg["enabled"] = bool(enabled)
    cfg["allow_approval"] = bool(allow_approval)
    cfg["approval_timeout_min"] = max(1, min(30, int(approval_timeout_min)))
    await settings_store.set_value(session, CONFIG_KEY, cfg)
    return cfg


# ---------------------------------------------------------------------
# 提示词（服务端统一给，手机只发对话内容）
# ---------------------------------------------------------------------

def system_prompt(mode: str, target_lang: str, now: datetime | None = None) -> str:
    today = (now or datetime.now()).strftime("%Y-%m-%d")
    if mode == "translate":
        lang = LANGUAGES.get(target_lang, LANGUAGES["zh"])
        return (
            f"你是专业的翻译。把用户发来的内容翻译成{lang}。\n"
            "只输出译文，不要解释，不要加引号或前缀；保留原文的换行、数字、单位和专有名词。\n"
            f"如果原文已经是{lang}，就翻译成简体中文。"
        )
    return (
        "你是 FlyknitBuddy，工厂员工的 AI 办公助手。现在员工在手机上和你聊天：\n"
        "- 回答简洁、分段清楚，适合手机屏幕阅读；能用列表就用列表，少用宽表格\n"
        "- 用用户使用的语言回答\n"
        "- 需要操作电脑（找文件、整理表格、查系统）的事，提醒员工切到「办事」，交给他电脑上的 FlyknitBuddy 去做\n"
        "- 若下文附有联网检索结果，请优先依据它们回答，并在关键处标注来源；不要编造检索里没有的网页\n"
        f"今天是 {today}。"
    )


def _mobile_content(content: object) -> str | list | None:
    """纯文字，或带图的 OpenAI 分片（手机拍照 / 相册）。"""
    if isinstance(content, str) and content.strip():
        return content[:MAX_MESSAGE_CHARS]
    if not isinstance(content, list):
        return None
    parts: list[dict] = []
    text_budget = MAX_MESSAGE_CHARS
    for part in content[:8]:
        if not isinstance(part, dict):
            continue
        kind = part.get("type")
        if kind == "text":
            text = str(part.get("text") or "")[:text_budget]
            text_budget = max(0, text_budget - len(text))
            if text:
                parts.append({"type": "text", "text": text})
        elif kind == "image_url":
            url = part.get("image_url")
            if isinstance(url, dict):
                url = url.get("url")
            if isinstance(url, str) and url.startswith(("data:image/", "http://", "https://")) and len(url) < 4_000_000:
                parts.append({"type": "image_url", "image_url": {"url": url}})
    return parts or None


def build_messages(mode: str, target_lang: str, raw: object) -> list[dict]:
    """手机发来的历史只认 user / assistant；用户消息可带图。系统提示词由这里加。"""
    out: list[dict] = []
    if isinstance(raw, list):
        for m in raw[-MAX_HISTORY:]:
            if not isinstance(m, dict) or m.get("role") not in ("user", "assistant"):
                continue
            content = _mobile_content(m.get("content"))
            if content is None:
                continue
            # 助手历史只留纯文字，避免把旧图反复塞回上下文
            if m["role"] == "assistant" and not isinstance(content, str):
                continue
            out.append({"role": m["role"], "content": content})
    if mode == "translate":
        # 翻译不需要上下文，只翻最后一句，省 token 也不会被前文带偏
        out = [m for m in out if m["role"] == "user"][-1:]
    return [{"role": "system", "content": system_prompt(mode, target_lang)}] + out


def delta_of(line: bytes) -> tuple[str, str]:
    """从一行 SSE 里取出（正文增量，思考增量）。思考在 reasoning_content，正文在 content。"""
    line = line.strip()
    if not line.startswith(b"data:"):
        return "", ""
    body = line[5:].strip()
    if not body or body == b"[DONE]":
        return "", ""
    try:
        obj = json.loads(body)
    except ValueError:
        return "", ""
    choices = obj.get("choices") if isinstance(obj, dict) else None
    if not choices or not isinstance(choices[0], dict):
        return "", ""
    delta = choices[0].get("delta") or {}
    if not isinstance(delta, dict):
        return "", ""
    text = delta.get("content") or ""
    reasoning = delta.get("reasoning_content") or delta.get("reasoning") or ""
    return (text if isinstance(text, str) else ""), (reasoning if isinstance(reasoning, str) else "")


# ---------------------------------------------------------------------
# 审批：电脑上执行到需要确认的操作，交给发起任务的那部手机决定
# ---------------------------------------------------------------------

@dataclass
class Approval:
    id: str
    message_id: int
    device_id: int
    mobile_id: int
    summary: str
    detail: str
    risk: str
    expires: float
    status: str = "pending"  # pending / approved / rejected / expired
    created_at: datetime = field(default_factory=_now)
    done: asyncio.Event = field(default_factory=asyncio.Event)

    def out(self) -> dict:
        return {"id": self.id, "task": self.message_id, "summary": self.summary, "detail": self.detail,
                "risk": self.risk, "status": self.status, "created_at": self.created_at.isoformat(),
                "expires_in": max(0, int(self.expires - time.monotonic()))}


# ---------------------------------------------------------------------
# 插话：电脑上正在办的事，手机上又补充了一句（或者要求停下）
# ---------------------------------------------------------------------

#: 一件事最多攒多少条没送到电脑的插话（防止刷屏把内存撑大）
MAX_PENDING_INPUTS = 20


@dataclass
class TaskInbox:
    texts: list[str] = field(default_factory=list)
    stop: bool = False
    wake: asyncio.Event = field(default_factory=asyncio.Event)


class MobileHub:
    def __init__(self) -> None:
        self._sockets: dict[int, set] = {}
        #: 每部手机一条发送锁：Starlette WebSocket 不能多协程同时 send，否则审批推送会丢
        self._send_locks: dict[int, asyncio.Lock] = {}
        self._approvals: dict[str, Approval] = {}
        self._pair_failures: dict[str, list[float]] = {}
        self._inputs: dict[int, TaskInbox] = {}
        #: 已预热过的上游主机，避免每部手机连上都打一枪
        self._warm_hosts: set[str] = set()
        self._warm_lock = asyncio.Lock()

    # ---- 连接 ----

    def attach(self, mobile_id: int, ws) -> None:
        self._sockets.setdefault(mobile_id, set()).add(ws)
        self._send_locks.setdefault(mobile_id, asyncio.Lock())

    async def warm_upstream(self, http: httpx.AsyncClient, sessionmaker) -> None:
        """手机连上后预热上游：TLS + 连接池 + 模型冷启动。

        第一次聊天慢、第二次快，多半是连阿里云和模型冷启；这里用 1 token 的短请求提前热好。
        """
        host = ""
        try:
            async with sessionmaker() as session:
                targets = await model_router.prefer_mobile(session, "chat")
            target = targets[0]
            host = httpx.URL(target.url).host or target.url
            async with self._warm_lock:
                if host in self._warm_hosts:
                    return
                self._warm_hosts.add(host)
            body = {
                "messages": [{"role": "user", "content": "."}],
                "stream": True,
                "max_tokens": 1,
                "enable_thinking": False,
            }
            upstream, _ = await model_router.open_upstream(http, targets, body)
            try:
                async for _ in upstream.aiter_raw():
                    break
            finally:
                await upstream.aclose()
            log.info("手机上游已预热 host=%s model=%s", host, target.display_name)
        except Exception:  # noqa: BLE001  预热失败不影响正常聊天，下次连上再试
            if host:
                self._warm_hosts.discard(host)
            log.debug("手机上游预热跳过", exc_info=True)

    def detach(self, mobile_id: int, ws) -> None:
        self._sockets.get(mobile_id, set()).discard(ws)
        if not self._sockets.get(mobile_id):
            self._sockets.pop(mobile_id, None)
            # 发送锁留给下次 attach 复用，避免某次 send 还握着锁时被摘掉

    def connected(self, mobile_id: int) -> bool:
        return bool(self._sockets.get(mobile_id))

    def _lock_for(self, mobile_id: int) -> asyncio.Lock:
        return self._send_locks.setdefault(mobile_id, asyncio.Lock())

    async def send(self, mobile_id: int, event: dict) -> bool:
        """推给这部手机的所有连接。手机没连着返回 False（下次打开 App 时按任务列表补齐）。"""
        sockets = list(self._sockets.get(mobile_id, ()))
        if not sockets:
            return False
        sent = False
        async with self._lock_for(mobile_id):
            for ws in sockets:
                try:
                    await ws.send_json(event)
                    sent = True
                except Exception:  # noqa: BLE001  断了的连接摘掉
                    self.detach(mobile_id, ws)
        return sent

    async def send_ws(self, mobile_id: int, ws, event: dict) -> bool:
        """经统一锁向指定连接发送（聊天流式与审批/进度共用，避免交错写坏帧）。"""
        async with self._lock_for(mobile_id):
            try:
                await ws.send_json(event)
                return True
            except Exception:  # noqa: BLE001
                self.detach(mobile_id, ws)
                return False

    async def close_all(self, mobile_id: int) -> None:
        sockets = list(self._sockets.pop(mobile_id, ()))
        self._send_locks.pop(mobile_id, None)
        for ws in sockets:
            try:
                await ws.close(code=4401)
            except Exception:  # noqa: BLE001
                pass

    # ---- 配对 ----

    async def new_pair_code(self, session: AsyncSession, device_id: int) -> MobilePairCode:
        for old in (await session.scalars(select(MobilePairCode).where(MobilePairCode.device_id == device_id))).all():
            await session.delete(old)
        while True:
            code = "".join(secrets.choice(_ALPHABET) for _ in range(PAIR_CODE_LEN))
            if await session.get(MobilePairCode, code) is None:
                break
        row = MobilePairCode(code=code, device_id=device_id, expires_at=_now() + PAIR_CODE_TTL)
        session.add(row)
        await session.commit()
        return row

    def pair_blocked(self, source: str) -> bool:
        now = time.monotonic()
        recent = [t for t in self._pair_failures.get(source, []) if now - t < PAIR_FAILURE_WINDOW]
        self._pair_failures[source] = recent
        return len(recent) >= MAX_PAIR_FAILURES

    def pair_failed(self, source: str) -> None:
        self._pair_failures.setdefault(source, []).append(time.monotonic())

    async def pair(self, session: AsyncSession, code: str, name: str, platform: str, app_version: str,
                   token_hash: str) -> tuple[MobileDevice, Device] | None:
        row = await session.get(MobilePairCode, code.strip().upper())
        if row is None or _aware(row.expires_at) < _now():
            return None
        device = await session.get(Device, row.device_id)
        if device is None or device.disabled:
            return None
        mobile = MobileDevice(device_id=device.id, name=(name.strip() or "手机")[:100], platform=platform[:20],
                              app_version=app_version[:50], token_hash=token_hash, last_seen=_now())
        session.add(mobile)
        await session.delete(row)  # 一次性
        await session.flush()
        # 办事走通道中枢：给这部手机建一条绑定，和钉钉、企业微信的绑定放在一起
        session.add(ChannelBinding(channel=CHANNEL, external_id=external_id(mobile.id),
                                   external_name=mobile.name, device_id=device.id))
        await session.commit()
        log.info("手机 %s（#%s）绑定到电脑 %s", mobile.name, mobile.id, device.machine_name)
        return mobile, device

    async def unpair(self, session: AsyncSession, mobile: MobileDevice) -> None:
        binding = await session.scalar(select(ChannelBinding).where(
            ChannelBinding.channel == CHANNEL, ChannelBinding.external_id == external_id(mobile.id)))
        if binding is not None:
            await session.delete(binding)
        await session.delete(mobile)
        await session.commit()
        for a in [a for a in self._approvals.values() if a.mobile_id == mobile.id and a.status == "pending"]:
            self._close(a, "rejected")
        await self.close_all(mobile.id)

    # ---- 任务（办事） ----

    async def task_event(self, row: ChannelMessage, kind: str, text: str) -> None:
        """电脑上执行到哪一步了。"""
        if (mid := mobile_id_of(row.external_id)) is not None:
            await self.send(mid, {"type": "task.event", "task": row.id, "kind": kind, "text": text})

    def push_input(self, message_id: int, text: str) -> bool:
        """正在电脑上办的事，手机上补充一句：电脑下一次来取时交给 AI（AI 下一步就能看到）。"""
        box = self._inputs.setdefault(message_id, TaskInbox())
        if len(box.texts) >= MAX_PENDING_INPUTS:
            return False
        box.texts.append(text)
        box.wake.set()
        return True

    def request_stop(self, message_id: int) -> None:
        """打断：电脑停掉这一件事（手机上那句优先的话已经作为新任务排在最前面）。"""
        box = self._inputs.setdefault(message_id, TaskInbox())
        box.stop = True
        box.wake.set()

    async def take_inputs(self, message_id: int, wait: float) -> tuple[list[str], bool]:
        """电脑来取插话：有就马上返回，没有就等最多 wait 秒。"""
        box = self._inputs.setdefault(message_id, TaskInbox())
        if not box.texts and not box.stop and wait > 0:
            box.wake.clear()
            try:
                await asyncio.wait_for(box.wake.wait(), wait)
            except asyncio.TimeoutError:
                pass
        texts, box.texts = box.texts, []
        return texts, box.stop

    def drop_inputs(self, message_id: int) -> list[str]:
        """这件事结束了：清掉它的插话箱，返回没送到电脑的话（调用方告诉手机这几句没赶上）。"""
        box = self._inputs.pop(message_id, None)
        if box is None:
            return []
        box.wake.set()  # 正在等的电脑马上返回
        return box.texts

    async def task_changed(self, row: ChannelMessage) -> bool:
        """任务状态变了（开始、完成、失败、超时）。通道中枢在 finish / expire 时调用。"""
        if row.status not in ("pending", "running"):
            self.drop_inputs(row.id)
        mid = mobile_id_of(row.external_id)
        if mid is None:
            return False
        return await self.send(mid, {"type": "task.updated", "task": task_out(row)})

    # ---- 审批 ----

    def request_approval(self, row: ChannelMessage, summary: str, detail: str, risk: str, timeout_min: int) -> Approval | None:
        mid = mobile_id_of(row.external_id)
        if mid is None or row.device_id is None:
            return None
        a = Approval(id=secrets.token_urlsafe(9), message_id=row.id, device_id=row.device_id, mobile_id=mid,
                     summary=summary[:500], detail=detail[:2000], risk=risk[:20],
                     expires=time.monotonic() + timeout_min * 60)
        self._approvals[a.id] = a
        self._prune()
        return a

    def get_approval(self, approval_id: str) -> Approval | None:
        a = self._approvals.get(approval_id)
        if a is not None and a.status == "pending" and time.monotonic() > a.expires:
            self._close(a, "expired")
        return a

    def pending_for(self, mobile_id: int) -> list[Approval]:
        return [a for a in list(self._approvals.values())
                if a.mobile_id == mobile_id and (self.get_approval(a.id) or a).status == "pending"]

    async def answer(self, approval_id: str, mobile_id: int, approve: bool) -> Approval | None:
        a = self.get_approval(approval_id)
        if a is None or a.mobile_id != mobile_id:
            return None
        if a.status == "pending":
            self._close(a, "approved" if approve else "rejected")
            log.info("手机 #%s %s了任务 #%s 的操作：%s", mobile_id, "批准" if approve else "拒绝", a.message_id, a.summary)
            await self.send(mobile_id, {"type": "approval.closed", "approval": a.out()})
        return a

    async def cancel_by_device(self, approval_id: str, device_id: int) -> Approval | None:
        """电脑本机先点了允许/拒绝：关掉手机上还挂着的同一张审批卡。"""
        a = self.get_approval(approval_id)
        if a is None or a.device_id != device_id:
            return None
        if a.status == "pending":
            self._close(a, "cancelled")
            log.info("电脑 #%s 本机已确认，取消手机审批 %s（任务 #%s）", device_id, a.id, a.message_id)
            await self.send(a.mobile_id, {"type": "approval.closed", "approval": a.out()})
        return a

    async def wait_approval(self, a: Approval, timeout: float) -> str:
        if a.status == "pending":
            remaining = max(0.0, a.expires - time.monotonic())
            try:
                await asyncio.wait_for(a.done.wait(), min(timeout, remaining))
            except asyncio.TimeoutError:
                pass
        return (self.get_approval(a.id) or a).status

    def _close(self, a: Approval, status: str) -> None:
        a.status = status
        a.done.set()

    def _prune(self) -> None:
        cutoff = time.monotonic() - 3600
        for k, a in list(self._approvals.items()):
            if a.status != "pending" and a.expires < cutoff:
                self._approvals.pop(k, None)

    # ---- 聊天、翻译：服务端直接调模型，逐字推回 ----

    async def chat(self, ws, http: httpx.AsyncClient, sessionmaker, mobile: MobileDevice, device: Device, req: dict) -> None:
        rid = str(req.get("id") or "")[:64]
        mode = "translate" if req.get("mode") == "translate" else "chat"
        target_lang = str(req.get("target") or "zh")
        conversation_id = str(req.get("conversation_id") or "")[:56]
        # 默认关思考：手机上开着的话会先空等很久才出字。只有点了「深度思考」才打开
        thinking = bool(req.get("thinking")) and mode != "translate"
        raw_model = req.get("model_id")
        try:
            model_id = int(raw_model) if raw_model not in (None, "") else None
        except (TypeError, ValueError):
            model_id = None

        async def emit(event: dict) -> None:
            event["id"] = rid
            # 和审批/进度推送共用发送锁，避免聊天流与 approval.request 交错丢帧
            await self.send_ws(mobile.id, ws, event)

        messages = build_messages(mode, target_lang, req.get("messages"))
        if len(messages) < 2:
            await emit({"type": "chat.error", "message": "没有要发送的内容"})
            return
        want_search = bool(req.get("web_search")) and mode == "chat"
        if model_router.has_image(messages):
            # 带图时走 vision 场景路由（用户指定了模型则仍用那个，前提是它能看图）
            mode_for_route = "vision"
        else:
            mode_for_route = mode
        async with sessionmaker() as session:
            quota = await usage_store.get_quota(session)
            limit = usage_store.limit_for(quota, device)
            if limit and await usage_store.used_today(session, device.id) >= limit:
                await emit({"type": "chat.error", "message": f"今天的用量已达上限，请联系 {quota['contact_name']} 增加额度"})
                return
            try:
                targets = await model_router.prefer_mobile(session, mode_for_route, model_id)
            except model_router.NoRouteError as exc:
                await emit({"type": "chat.error", "message": str(exc)})
                return
            if model_router.has_image(messages) and not targets[0].supports_vision:
                await emit({"type": "chat.error", "message": "当前模型不支持看图，请在设置里换一个带视觉的模型，或去掉图片"})
                return
            search_cfg = await web_search.get_config(session) if want_search else None
        if want_search:
            await emit({"type": "chat.searching"})
            if search_cfg is None or not web_search.is_ready(search_cfg):
                await emit({"type": "chat.sources", "sources": [], "skipped": "管理员未开启联网搜索"})
            else:
                query = web_search.last_user_query(messages)
                try:
                    hits = await web_search.search(http, search_cfg, query)
                    messages = web_search.inject_into_messages(messages, hits)
                    await emit({"type": "chat.sources", "sources": web_search.hits_out(hits)})
                except web_search.SearchError as exc:
                    log.warning("手机 #%s 联网搜索失败：%s", mobile.id, exc)
                    await emit({"type": "chat.sources", "sources": [], "skipped": str(exc)})
        body = {
            "messages": messages,
            "stream": True,
            "stream_options": {"include_usage": True},
            # 显式关掉思考；模型 extra_body 里的同名字段不会覆盖（setdefault）
            "enable_thinking": thinking,
        }
        try:
            upstream, target = await model_router.open_upstream(http, targets, body)
        except model_router.UpstreamUnavailable as exc:
            await emit({"type": "chat.error", "message": f"模型服务暂时不可用：{exc}"})
            return
        if upstream.status_code >= 400:
            await upstream.aclose()
            await emit({"type": "chat.error", "message": f"模型服务返回错误（HTTP {upstream.status_code}）"})
            return

        await emit({"type": "chat.start", "model": target.display_name})
        scanner = usage_store.StreamUsageScanner()
        text = chat_archive.StreamTextScanner()
        buffer = b""
        recorded = False
        try:
            try:
                async for chunk in upstream.aiter_raw():
                    scanner.feed(chunk)
                    text.feed(chunk)
                    buffer += chunk
                    while b"\n" in buffer:
                        line, buffer = buffer.split(b"\n", 1)
                        delta, reasoning = delta_of(line)
                        # 没开深度思考时上游偶发 reasoning 也不推，避免手机一直停在「正在思考」
                        if thinking and reasoning:
                            await emit({"type": "chat.reasoning", "text": reasoning})
                        if delta:
                            await emit({"type": "chat.delta", "text": delta})
                if buffer.strip():
                    delta, reasoning = delta_of(buffer)
                    if thinking and reasoning:
                        await emit({"type": "chat.reasoning", "text": reasoning})
                    if delta:
                        await emit({"type": "chat.delta", "text": delta})
                with anyio.CancelScope(shield=True):
                    await upstream.aclose()
                    scanner.finish()
                    text.finish()
                    await self._record(sessionmaker, device, mode, conversation_id, messages, text.text, scanner, target)
                    recorded = True
                await emit({"type": "chat.done", "text": text.text})
            except httpx.HTTPError as exc:
                log.warning("手机 #%s 聊天时上游中途断开：%s", mobile.id, exc)
                await emit({"type": "chat.error", "message": "模型服务连接中断，请重试"})
        finally:
            # 收尾时屏蔽取消：手机断开会取消正在跑的聊天，用量和存档还是要记
            if not recorded:
                with anyio.CancelScope(shield=True):
                    await upstream.aclose()
                    scanner.finish()
                    text.finish()
                    await self._record(sessionmaker, device, mode, conversation_id, messages, text.text, scanner, target)

    async def _record(self, sessionmaker, device: Device, mode: str, conversation_id: str, messages: list[dict],
                      answer: str, scanner, target) -> None:
        try:
            async with sessionmaker() as s:
                if scanner.prompt > 0 or scanner.completion > 0:
                    await usage_store.record(s, device.id, "mobile", scanner.prompt, scanner.completion,
                                             model_id=target.model_id, model_name=target.display_name)
                user_text, attachments = chat_archive.last_user_message(messages)
                if conversation_id and (user_text or answer):
                    s.add(ChatRecord(
                        device_id=device.id, machine_name=device.machine_name, user_name=device.user_name,
                        conversation_id=f"mobile-{conversation_id}", scene="translate" if mode == "translate" else "chat",
                        model=target.display_name, user_content=chat_archive.clip(user_text),
                        assistant_content=chat_archive.clip(answer), attachments=attachments,
                        prompt_tokens=max(0, scanner.prompt), completion_tokens=max(0, scanner.completion),
                    ))
                    await s.commit()
        except Exception:  # noqa: BLE001  统计和存档失败不能影响聊天
            log.warning("记录手机聊天失败 device=%s", device.id, exc_info=True)


def task_out(row: ChannelMessage) -> dict:
    return {
        "id": row.id, "text": row.text, "status": row.status, "answer": row.answer, "error": row.error,
        "created_at": _aware(row.created_at).isoformat() if row.created_at else None,
        "finished_at": _aware(row.finished_at).isoformat() if row.finished_at else None,
    }


hub = MobileHub()
