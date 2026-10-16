"""
通道中枢：钉钉、企业微信进来的消息怎么认人、怎么排队交给员工电脑、结果怎么回去。

    钉钉 Stream / 企业微信长连接 ──▶ handle_inbound ──▶ channel_messages（pending）
                                            │  员工端长轮询（最多挂 25 秒）领走 → running
                                            ▼
                         员工端执行完 finish ──▶ 回复发回原来的通道

认人：钉钉消息里的 senderStaffId、企业微信消息里的 userid 都是平台签发的企业员工 ID，员工自己改不了。
员工在自己电脑上的 FlyknitBuddy 里生成绑定码，再在钉钉或企业微信里发「绑定 123456」，两边都得是本人才绑得上。
同一个绑定码钉钉、企业微信都能用；两边的绑定各自独立。
只接单聊：群里 @机器人 的结果别人也看得见，电脑上的东西不该这样发出去。

进程内内存记「哪台电脑在线、在等」，单机部署足够；多实例时员工端换到别的实例，最多晚一个轮询周期。
"""

from __future__ import annotations

import asyncio
import logging
import re
import secrets
from datetime import datetime, timedelta, timezone
from typing import Protocol

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from .. import crypto
from ..models import ChannelBindCode, ChannelBinding, ChannelMessage, Device
from . import settings_store
from .dingtalk import BotMessage, DingTalkApi, StreamStatus, for_dingtalk, run_stream
from .wecom import WeComBot

log = logging.getLogger("flyknit.channels")

CONFIG_KEY = "channel_dingtalk"
DEFAULT_CONFIG = {"enabled": False, "client_id": "", "client_secret": "", "robot_code": ""}
WECOM_KEY = "channel_wecom"
WECOM_DEFAULT = {"enabled": False, "bot_id": "", "secret": ""}
CHANNEL_NAMES = {"dingtalk": "钉钉", "wecom": "企业微信"}

BIND_CODE_TTL = timedelta(minutes=10)
#: 同一个钉钉账号一小时内最多输错几次绑定码（防止在钉钉里挨个试别人的码）
MAX_BIND_FAILURES = 5
BIND_FAILURE_WINDOW = 3600.0
#: 电脑一直不在线，消息排队多久就作废
PENDING_TTL = timedelta(minutes=30)
#: 领走后多久还没回结果就当失败（员工端崩了、关机了）
RUNNING_TTL = timedelta(hours=2)
#: 多久内轮询过就算在线（员工端每次最多挂 25 秒，然后马上再来）
ONLINE_WINDOW = 90.0
MAX_TEXT = 4000

HELP = (
    "我是 FlyknitBuddy。绑定后，你在这里发的消息会交给你电脑上的 FlyknitBuddy 去办，办完把结果发回来。\n\n"
    "- **绑定**：在电脑上打开 FlyknitBuddy →「设置 → 钉钉 / 企业微信」，生成绑定码，然后在这里发「绑定 123456」\n"
    "- **解绑**：发「解绑」\n"
    "- 需要在电脑上确认的操作（删除、安装软件等）不会远程执行，会提示你回到电脑上处理"
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _aware(t: datetime) -> datetime:
    return t if t.tzinfo else t.replace(tzinfo=timezone.utc)


class Replier(Protocol):
    async def reply(self, webhook: str, webhook_expires: datetime | None, staff_id: str, title: str, text: str) -> None: ...


# ---------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------

async def get_config(session: AsyncSession) -> dict:
    cfg = await settings_store.get_value(session, CONFIG_KEY, DEFAULT_CONFIG)
    for k, v in DEFAULT_CONFIG.items():
        cfg.setdefault(k, v)
    return cfg


def secret_of(cfg: dict) -> str:
    try:
        return crypto.decrypt(cfg.get("client_secret") or "")
    except ValueError:
        return ""


async def save_config(session: AsyncSession, enabled: bool, client_id: str, client_secret: str | None, robot_code: str) -> dict:
    """client_secret 传 None 或空表示不改（后台只显示打码后的值）。"""
    cfg = await get_config(session)
    cfg["enabled"] = bool(enabled)
    cfg["client_id"] = client_id.strip()
    cfg["robot_code"] = robot_code.strip()
    if client_secret:
        cfg["client_secret"] = crypto.encrypt(client_secret.strip())
    await settings_store.set_value(session, CONFIG_KEY, cfg)
    hub.wake_supervisor()
    return cfg


def ready(cfg: dict) -> bool:
    return bool(cfg.get("enabled") and cfg.get("client_id") and cfg.get("client_secret"))


async def get_wecom_config(session: AsyncSession) -> dict:
    cfg = await settings_store.get_value(session, WECOM_KEY, WECOM_DEFAULT)
    for k, v in WECOM_DEFAULT.items():
        cfg.setdefault(k, v)
    return cfg


def wecom_secret(cfg: dict) -> str:
    try:
        return crypto.decrypt(cfg.get("secret") or "")
    except ValueError:
        return ""


async def save_wecom_config(session: AsyncSession, enabled: bool, bot_id: str, secret: str | None) -> dict:
    """secret 传 None 或空表示不改。"""
    cfg = await get_wecom_config(session)
    cfg["enabled"] = bool(enabled)
    cfg["bot_id"] = bot_id.strip()
    if secret:
        cfg["secret"] = crypto.encrypt(secret.strip())
    await settings_store.set_value(session, WECOM_KEY, cfg)
    hub.wake_supervisor()
    return cfg


def wecom_ready(cfg: dict) -> bool:
    return bool(cfg.get("enabled") and cfg.get("bot_id") and cfg.get("secret"))


# ---------------------------------------------------------------------
# 中枢
# ---------------------------------------------------------------------

class Hub:
    def __init__(self) -> None:
        self._waiters: dict[int, set[asyncio.Event]] = {}
        self._last_poll: dict[int, float] = {}
        #: 每个通道的连接状态和回消息的对象（钉钉 / 企业微信）
        self.statuses: dict[str, StreamStatus] = {}
        self.repliers: dict[str, Replier] = {}
        self._sessionmaker: async_sessionmaker | None = None
        #: 在 supervise 里按当前事件循环创建（模块导入时还没有循环）
        self._supervisor_wake: asyncio.Event | None = None
        self._bind_failures: dict[str, list[float]] = {}

    # ---- 员工端在不在线 ----

    def mark_polling(self, device_id: int) -> None:
        self._last_poll[device_id] = asyncio.get_running_loop().time()

    def online(self, device_id: int) -> bool:
        if self._waiters.get(device_id):
            return True
        seen = self._last_poll.get(device_id)
        return seen is not None and asyncio.get_running_loop().time() - seen < ONLINE_WINDOW

    def notify(self, device_id: int) -> None:
        for ev in list(self._waiters.get(device_id, ())):
            ev.set()

    async def wait(self, device_id: int, timeout: float) -> None:
        ev = asyncio.Event()
        self._waiters.setdefault(device_id, set()).add(ev)
        try:
            await asyncio.wait_for(ev.wait(), timeout)
        except asyncio.TimeoutError:
            pass
        finally:
            self._waiters.get(device_id, set()).discard(ev)

    def wake_supervisor(self) -> None:
        if self._supervisor_wake is not None:
            self._supervisor_wake.set()

    # ---- 回消息 ----

    def status(self, channel: str) -> StreamStatus:
        return self.statuses.setdefault(channel, StreamStatus())

    async def say(self, channel: str, webhook: str, expires: datetime | None, staff_id: str, text: str,
                  title: str = "FlyknitBuddy") -> bool:
        replier = self.repliers.get(channel)
        if replier is None:
            log.warning("%s还没连上，回复没发出去", CHANNEL_NAMES.get(channel, channel))
            return False
        try:
            await replier.reply(webhook, expires, staff_id, title, text)
            return True
        except Exception:  # noqa: BLE001
            log.exception("回复%s消息失败", CHANNEL_NAMES.get(channel, channel))
            return False

    # ---- 收消息 ----

    async def handle_inbound(self, session: AsyncSession, msg: BotMessage) -> ChannelMessage | None:
        """处理一条钉钉 / 企业微信消息。需要员工电脑执行的返回排进队列的那条记录。"""
        ch = msg.channel
        if msg.msg_id and await session.scalar(
            select(ChannelMessage.id).where(ChannelMessage.channel == ch, ChannelMessage.external_msg_id == msg.msg_id)
        ):
            return None  # 平台重复投递

        async def say(text: str) -> None:
            await self.say(ch, msg.session_webhook, msg.webhook_expires, msg.staff_id, text)

        if msg.conversation_type != "1":
            await say("为了保护你电脑上的信息，请私聊我，不要在群里让我办事。")
            return None
        text = msg.text.strip()
        if not text:
            await say("目前只能处理文字消息。")
            return None
        if text in ("帮助", "help", "?", "？"):
            await say(HELP)
            return None
        if m := re.fullmatch(r"(?:绑定|bind)\s*(\d{6})", text, re.IGNORECASE):
            await say(await self._bind(session, msg, m.group(1)))
            return None
        binding = await session.scalar(
            select(ChannelBinding).where(ChannelBinding.channel == ch, ChannelBinding.external_id == msg.staff_id)
        )
        if text in ("解绑", "unbind"):
            if binding is None:
                await say("你还没有绑定电脑。")
            else:
                await session.delete(binding)
                await session.commit()
                await say("已解绑，这里发的消息不会再转到你的电脑上。")
            return None
        if binding is None:
            await say("你还没有绑定电脑。\n\n" + HELP)
            return None
        device = await session.get(Device, binding.device_id)
        if device is None or device.disabled:
            await say("绑定的电脑已被管理员停用或删除，请在新电脑上重新绑定。")
            return None

        row = ChannelMessage(
            channel=ch, binding_id=binding.id, device_id=binding.device_id,
            external_id=msg.staff_id, external_name=msg.nick or binding.external_name,
            external_msg_id=msg.msg_id, text=text[:MAX_TEXT],
            reply_webhook=msg.session_webhook, reply_webhook_expires=msg.webhook_expires,
        )
        session.add(row)
        binding.last_used = _now()
        if msg.nick:
            binding.external_name = msg.nick
        await session.commit()
        if self.online(binding.device_id):
            await say("收到，正在你的电脑上处理…")
            self.notify(binding.device_id)
        else:
            await say(f"你的电脑现在没有打开 FlyknitBuddy。开机并打开后，{int(PENDING_TTL.total_seconds() // 60)} 分钟内会自动接着办；超时就取消，到时再发一次即可。")
        return row

    async def _bind(self, session: AsyncSession, msg: BotMessage, code: str) -> str:
        loop_now = asyncio.get_running_loop().time()
        recent = [t for t in self._bind_failures.get(msg.staff_id, []) if loop_now - t < BIND_FAILURE_WINDOW]
        self._bind_failures[msg.staff_id] = recent
        if len(recent) >= MAX_BIND_FAILURES:
            return "绑定码输错次数太多，请一小时后再试。"
        row = await session.get(ChannelBindCode, code)
        if row is None or _aware(row.expires_at) < _now():
            recent.append(loop_now)
            log.warning("%s账号 %s 绑定码不对（一小时内第 %d 次）", CHANNEL_NAMES.get(msg.channel, msg.channel), msg.staff_id, len(recent))
            return "绑定码不对或已过期。请在电脑上的 FlyknitBuddy「设置 → 钉钉 / 企业微信」里重新生成。"
        device = await session.get(Device, row.device_id)
        if device is None or device.disabled:
            return "这台电脑已被管理员停用，不能绑定。"
        binding = await session.scalar(
            select(ChannelBinding).where(ChannelBinding.channel == msg.channel, ChannelBinding.external_id == msg.staff_id)
        )
        if binding is None:
            binding = ChannelBinding(channel=msg.channel, external_id=msg.staff_id)
            session.add(binding)
        # 企业微信长连接里只有 userid，没有姓名，就用 userid 显示
        binding.external_name = msg.nick or msg.staff_id
        binding.device_id = device.id
        binding.conversation_id = ""
        binding.created_at = _now()
        await session.delete(row)  # 一次性
        await session.commit()
        log.info("%s账号 %s（%s）绑定到电脑 %s", CHANNEL_NAMES.get(msg.channel, msg.channel), msg.staff_id, msg.nick, device.machine_name)
        return f"已绑定到电脑 **{device.machine_name}**。现在可以直接发消息让我办事了。"

    # ---- 员工端 ----

    async def new_bind_code(self, session: AsyncSession, device_id: int) -> ChannelBindCode:
        old = (await session.scalars(select(ChannelBindCode).where(ChannelBindCode.device_id == device_id))).all()
        for o in old:
            await session.delete(o)
        while True:
            code = f"{secrets.randbelow(1_000_000):06d}"
            if await session.get(ChannelBindCode, code) is None:
                break
        row = ChannelBindCode(code=code, device_id=device_id, expires_at=_now() + BIND_CODE_TTL)
        session.add(row)
        await session.commit()
        return row

    async def take_pending(self, session: AsyncSession, device_id: int, limit: int = 5) -> list[ChannelMessage]:
        rows = (await session.scalars(
            select(ChannelMessage)
            .where(ChannelMessage.device_id == device_id, ChannelMessage.status == "pending")
            .order_by(ChannelMessage.id)
            .limit(limit)
        )).all()
        for r in rows:
            r.status = "running"
            r.started_at = _now()
        await session.commit()
        return list(rows)

    async def finish(self, session: AsyncSession, row: ChannelMessage, ok: bool, answer: str, error: str, conversation_id: str) -> None:
        row.status = "succeeded" if ok else "failed"
        row.answer = answer
        row.error = error
        row.finished_at = _now()
        if conversation_id and row.binding_id and (binding := await session.get(ChannelBinding, row.binding_id)):
            binding.conversation_id = conversation_id
        await session.commit()
        text = for_dingtalk(answer) if ok and answer.strip() else f"没办成：{error or '未知原因'}"
        row.replied = await self.say(row.channel, row.reply_webhook, row.reply_webhook_expires, row.external_id, text)
        await session.commit()

    async def expire(self, session: AsyncSession) -> int:
        """排太久的作废并告诉员工；领走太久没回音的算失败。"""
        now = _now()
        stale = (await session.scalars(
            select(ChannelMessage).where(ChannelMessage.status == "pending", ChannelMessage.created_at < now - PENDING_TTL)
        )).all()
        for r in stale:
            r.status = "expired"
            r.finished_at = now
        lost = (await session.scalars(
            select(ChannelMessage).where(ChannelMessage.status == "running", ChannelMessage.started_at < now - RUNNING_TTL)
        )).all()
        for r in lost:
            r.status = "failed"
            r.error = "电脑上的 FlyknitBuddy 一直没有回结果"
            r.finished_at = now
        await session.commit()

        def short(r: ChannelMessage) -> str:
            return r.text if len(r.text) <= 30 else r.text[:30] + "…"

        for r in stale:
            await self.say(r.channel, r.reply_webhook, r.reply_webhook_expires, r.external_id,
                           f"你的电脑一直没有上线，「{short(r)}」已取消。需要的话开机后再发一次。")
        for r in lost:
            r.replied = await self.say(r.channel, r.reply_webhook, r.reply_webhook_expires, r.external_id,
                                       f"「{short(r)}」在电脑上一直没有办完（可能关机或退出了 FlyknitBuddy），请到电脑上看看，或者再发一次。")
        if lost:
            await session.commit()
        return len(stale)

    # ---- 后台常驻：按配置连钉钉 / 企业微信，定时清理 ----

    async def supervise(self, sessionmaker: async_sessionmaker, http) -> None:
        self._sessionmaker = sessionmaker
        self._supervisor_wake = wake = asyncio.Event()
        tasks: dict[str, asyncio.Task] = {}
        current: dict[str, tuple | None] = {"dingtalk": None, "wecom": None}
        try:
            while True:
                async with sessionmaker() as s:
                    ding = await get_config(s)
                    wc = await get_wecom_config(s)
                    try:
                        await self.expire(s)
                    except Exception:  # noqa: BLE001
                        log.exception("清理通道消息失败")
                want: dict[str, tuple | None] = {
                    "dingtalk": (ding["client_id"], secret_of(ding), ding.get("robot_code") or "") if ready(ding) else None,
                    "wecom": (wc["bot_id"], wecom_secret(wc)) if wecom_ready(wc) else None,
                }
                for ch, w in want.items():
                    if w is not None and not w[1]:
                        w = None  # 密钥解不开（SECRET_KEY 换过）就当没配
                    if w == current[ch]:
                        continue
                    if (old := tasks.pop(ch, None)) is not None:
                        old.cancel()
                        await asyncio.gather(old, return_exceptions=True)
                    self.repliers.pop(ch, None)
                    self.statuses[ch] = status = StreamStatus()
                    current[ch] = w
                    if w is None:
                        continue
                    if ch == "dingtalk":
                        api = DingTalkApi(http, *w)
                        self.repliers[ch] = api
                        tasks[ch] = asyncio.create_task(run_stream(api, status, self._on_message))
                    else:
                        bot = WeComBot(*w)
                        self.repliers[ch] = bot
                        tasks[ch] = asyncio.create_task(bot.run(status, self._on_message))
                    log.info("%s通道已启用", CHANNEL_NAMES[ch])
                wake.clear()
                try:
                    await asyncio.wait_for(wake.wait(), 60)
                except asyncio.TimeoutError:
                    pass
        finally:
            for t in tasks.values():
                t.cancel()
            await asyncio.gather(*tasks.values(), return_exceptions=True)

    async def _on_message(self, msg: BotMessage) -> None:
        assert self._sessionmaker is not None
        async with self._sessionmaker() as s:
            await self.handle_inbound(s, msg)


hub = Hub()
