"""
无人值守任务（docs/unattended-jobs.md）：服务端按时间表把任务派到常开的执行机，做完把结果发到消息中心。

- 时间表：每天 / 工作日 / 每周几 / 每月几号 / 每 N 小时，固定 UTC 偏移（越南、中国没有夏令时）；
- 派发：到点生成一条通道消息（channel=unattended），执行机上的 FlyknitBuddy 领走执行（和事件触发同一条路：
  没人在场，需要确认的操作一律拒绝；权限按任务设的上限封顶，服务端核对授权时也查）；
- 上一次还没做完（还在排队或执行中）就跳过这一次，不堆积；
- 总开关（设置表 unattended.enabled）默认关；关着时不派发，过期的时间点直接顺延，打开后不会一下子补跑一堆；
- 结果：发一条「定时报告」到指定部门 / 电脑的消息中心；没指定收件人就只在后台看（不会变成发给全公司）。
"""

from __future__ import annotations

import asyncio
import html
import logging
import re
import uuid
from datetime import date, datetime, timedelta, timezone

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from ..models import ChannelMessage, Device, Notice, UnattendedJob
from . import notice_service, settings_store

log = logging.getLogger("flyknit.unattended")

KEY = "unattended"
CHANNEL = "unattended"
KINDS = ("daily", "weekdays", "weekly", "monthly", "hourly")
PERMISSIONS = ("readonly", "workspace")
TICK_SECONDS = 30
MAX_PROMPT = 4000
_TIME = re.compile(r"^([01]\d|2[0-3]):([0-5]\d)$")


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _aware(t: datetime | None) -> datetime | None:
    if t is None:
        return None
    return t if t.tzinfo else t.replace(tzinfo=timezone.utc)


async def master_on(session: AsyncSession) -> bool:
    return bool((await settings_store.get_value(session, KEY, {"enabled": False})).get("enabled", False))


# ---------------------------------------------------------------- 时间表


def normalize_schedule(raw: dict | None) -> dict:
    """校验并整理时间表；不合法抛 ValueError（中文原因）。"""
    raw = dict(raw or {})
    kind = raw.get("kind", "daily")
    if kind not in KINDS:
        raise ValueError("时间表类型只能是：每天、工作日、每周、每月、每隔几小时")
    time = str(raw.get("time", "08:00")).strip()
    if not _TIME.match(time):
        raise ValueError("时间要写成 HH:MM，如 08:00")
    out: dict = {"kind": kind, "time": time}
    if kind == "weekly":
        days = sorted({int(d) for d in raw.get("days") or [] if str(d).isdigit() and 1 <= int(d) <= 7})
        if not days:
            raise ValueError("每周要至少选一天（1 = 周一 … 7 = 周日）")
        out["days"] = days
    elif kind == "monthly":
        day = int(raw.get("day") or 1)
        if not 1 <= day <= 28:
            raise ValueError("每月几号只能选 1–28（29–31 号不是每个月都有）")
        out["day"] = day
    elif kind == "hourly":
        every = int(raw.get("every") or 1)
        if not 1 <= every <= 12:
            raise ValueError("每隔几小时只能选 1–12")
        out["every"] = every
    return out


def next_after(schedule: dict, utc_offset: int, after: datetime) -> datetime:
    """after 之后（不含）的下一次执行时间（UTC）。"""
    s = normalize_schedule(schedule)
    tz = timezone(timedelta(minutes=utc_offset))
    local = _aware(after).astimezone(tz)
    hh, mm = (int(x) for x in s["time"].split(":"))
    if s["kind"] == "hourly":
        # 每天从 time 开始，每隔 every 小时一次，到第二天的 time 重新对齐（如 08:00 每 5 小时：08、13、18、23 点和次日 04 点，再从 08 点起）
        step = timedelta(hours=s["every"])
        candidates = []
        for offset in (-1, 0, 1):
            start = datetime.combine(local.date() + timedelta(days=offset), datetime.min.time(), tz).replace(hour=hh, minute=mm)
            t = start
            while t < start + timedelta(days=1):
                if t > local:
                    candidates.append(t)
                t += step
        return min(candidates).astimezone(timezone.utc)
    for i in range(0, 400):
        d: date = local.date() + timedelta(days=i)
        if s["kind"] == "weekdays" and d.isoweekday() > 5:
            continue
        if s["kind"] == "weekly" and d.isoweekday() not in s["days"]:
            continue
        if s["kind"] == "monthly" and d.day != s["day"]:
            continue
        t = datetime.combine(d, datetime.min.time(), tz).replace(hour=hh, minute=mm)
        if t > local:
            return t.astimezone(timezone.utc)
    raise ValueError("算不出下一次执行时间")


def describe(schedule: dict) -> str:
    s = normalize_schedule(schedule)
    names = "一二三四五六日"
    return {
        "daily": f"每天 {s['time']}",
        "weekdays": f"工作日（周一至周五）{s['time']}",
        "weekly": "每周" + "、".join(names[d - 1] for d in s.get("days", [])) + f" {s['time']}",
        "monthly": f"每月 {s.get('day', 1)} 号 {s['time']}",
        "hourly": f"从 {s['time']} 起每 {s.get('every', 1)} 小时",
    }[s["kind"]]


# ---------------------------------------------------------------- 派发


def render(job: UnattendedJob) -> str:
    """交给执行机上 AI 的话：IT 写的要求 + 无人值守的说明。"""
    return (
        f"{job.prompt.strip()}\n\n"
        "（这是无人值守的定时任务「" + job.name + "」：电脑前没有人，需要员工确认的操作都会被拒绝，不要等人回答；"
        "做完把结论写清楚——先一句话结论，再用表格列关键数字，最后写需要人关注的地方。结果会作为报告发到消息中心。）"
    )


async def dispatch(session: AsyncSession, job: UnattendedJob, now: datetime | None = None) -> ChannelMessage | None:
    """派一次：执行机不在、被停用或上一次还没做完时返回 None（不派）。"""
    now = now or _now()
    device = await session.get(Device, job.device_id) if job.device_id else None
    if device is None or device.disabled:
        return None
    busy = await session.scalar(select(ChannelMessage.id).where(
        ChannelMessage.channel == CHANNEL, ChannelMessage.external_id == str(job.id),
        ChannelMessage.status.in_(("pending", "running"))).limit(1))
    if busy is not None:
        job.skipped_at = now
        return None
    row = ChannelMessage(channel=CHANNEL, device_id=device.id, external_id=str(job.id), external_name=job.name[:100],
                         text=render(job), permission=job.permission if job.permission in PERMISSIONS else "readonly",
                         rehearsal=job.rehearsal)
    session.add(row)
    job.last_run_at = now
    job.run_count = (job.run_count or 0) + 1
    return row


async def tick(session: AsyncSession, now: datetime | None = None) -> list[ChannelMessage]:
    """到点的任务派出去，并排好下一次。总开关关着时只顺延时间、不派发。返回这次派出的消息。"""
    now = now or _now()
    on = await master_on(session)
    jobs = (await session.scalars(select(UnattendedJob).where(UnattendedJob.enabled.is_(True)))).all()
    sent: list[ChannelMessage] = []
    for job in jobs:
        due = _aware(job.next_run_at)
        if due is None:
            job.next_run_at = next_after(job.schedule or {}, job.utc_offset, now)
            continue
        if due > now:
            continue
        if on and (row := await dispatch(session, job, now)) is not None:
            sent.append(row)
        # 停机期间错过的多次只补一次：下一次从现在往后算
        job.next_run_at = next_after(job.schedule or {}, job.utc_offset, now)
    await session.commit()
    if sent:
        from .channel_hub import hub  # 避免循环引用

        for row in sent:
            if row.device_id:
                hub.notify(row.device_id)
    return sent


async def run_forever(sessionmaker: async_sessionmaker) -> None:
    while True:
        try:
            async with sessionmaker() as session:
                await tick(session)
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001  一次出错不能让定时停掉
            log.warning("无人值守任务调度出错", exc_info=True)
        await asyncio.sleep(TICK_SECONDS)


# ---------------------------------------------------------------- 结果送达


async def deliver(session: AsyncSession, row: ChannelMessage) -> bool:
    """执行机回报结果后：发一条「定时报告」到收件人的消息中心。没有收件人返回 False（只在后台看）。"""
    job = await session.get(UnattendedJob, int(row.external_id)) if row.external_id.isdigit() else None
    if job is None:
        return False
    departments = [d for d in (job.notify_departments or []) if d]
    devices = [d for d in (job.notify_devices or []) if d]
    if not departments and not devices:
        return False  # 两个都空在消息中心等于全公司：这里一律不发
    ok = row.status == "succeeded" and row.answer.strip()
    stamp = (_aware(row.finished_at) or _now()).astimezone(timezone(timedelta(minutes=job.utc_offset))).strftime("%m-%d %H:%M")
    title = f"{'【预演】' if row.rehearsal else ''}{job.name}（{stamp}）"[:120]
    body_md = row.answer if ok else f"**这次没有做成**：{row.error or '未知原因'}\n\n请联系 IT 查看执行机和任务设置。"
    body_html, _ = notice_service.sanitize_html(markdown_to_html(body_md))
    notice = Notice(
        id="rpt_" + uuid.uuid4().hex, kind="report", title=title,
        summary=notice_service.plain_text(body_html, 120), body_html=body_html,
        important=not ok, scope_departments=departments, scope_devices=devices,
        status="published", published_at=_now(), author=f"无人值守任务：{job.name}"[:200],
    )
    session.add(notice)
    await session.commit()
    return True


# ---------------------------------------------------------------- Markdown → HTML（够报告用：标题、列表、表格、代码、粗体）

_INLINE_CODE = re.compile(r"`([^`]+)`")
_BOLD = re.compile(r"\*\*(.+?)\*\*")
_ITALIC = re.compile(r"(?<![*\w])\*(?!\s)(.+?)(?<!\s)\*(?![*\w])")
_TABLE_SEP = re.compile(r"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")


def _inline(text: str) -> str:
    out = html.escape(text, quote=False)
    out = _INLINE_CODE.sub(lambda m: f"<code>{m.group(1)}</code>", out)
    out = _BOLD.sub(r"<strong>\1</strong>", out)
    return _ITALIC.sub(r"<em>\1</em>", out)


def _cells(line: str) -> list[str]:
    line = line.strip()
    if line.startswith("|"):
        line = line[1:]
    if line.endswith("|"):
        line = line[:-1]
    return [c.strip() for c in line.split("|")]


def markdown_to_html(md: str) -> str:
    lines = (md or "").replace("\r\n", "\n").split("\n")
    out: list[str] = []
    para: list[str] = []
    i = 0

    def flush() -> None:
        if para:
            out.append("<p>" + "<br>".join(_inline(p) for p in para) + "</p>")
            para.clear()

    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        if stripped.startswith("```"):
            flush()
            code: list[str] = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                code.append(lines[i])
                i += 1
            out.append("<pre><code>" + html.escape("\n".join(code), quote=False) + "</code></pre>")
            i += 1
            continue
        if not stripped:
            flush()
            i += 1
            continue
        if m := re.match(r"^(#{1,4})\s+(.*)$", stripped):
            flush()
            level = min(len(m.group(1)) + 1, 4)  # 报告里的一级标题降一级，留给通知标题
            out.append(f"<h{level}>{_inline(m.group(2))}</h{level}>")
            i += 1
            continue
        if stripped.startswith("|") and i + 1 < len(lines) and _TABLE_SEP.match(lines[i + 1]):
            flush()
            head = _cells(stripped)
            i += 2
            rows: list[list[str]] = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append(_cells(lines[i]))
                i += 1
            out.append("<table><thead><tr>" + "".join(f"<th>{_inline(c)}</th>" for c in head) + "</tr></thead><tbody>"
                       + "".join("<tr>" + "".join(f"<td>{_inline(c)}</td>" for c in r) + "</tr>" for r in rows)
                       + "</tbody></table>")
            continue
        if re.match(r"^([-*+]|\d+[.)])\s+", stripped):
            flush()
            ordered = bool(re.match(r"^\d+[.)]\s+", stripped))
            items: list[str] = []
            while i < len(lines) and re.match(r"^\s*([-*+]|\d+[.)])\s+", lines[i]):
                items.append(re.sub(r"^\s*([-*+]|\d+[.)])\s+", "", lines[i]))
                i += 1
            tag = "ol" if ordered else "ul"
            out.append(f"<{tag}>" + "".join(f"<li>{_inline(x)}</li>" for x in items) + f"</{tag}>")
            continue
        if stripped.startswith(">"):
            flush()
            quote: list[str] = []
            while i < len(lines) and lines[i].strip().startswith(">"):
                quote.append(lines[i].strip()[1:].strip())
                i += 1
            out.append("<blockquote>" + "<br>".join(_inline(q) for q in quote) + "</blockquote>")
            continue
        para.append(stripped)
        i += 1
    flush()
    return "\n".join(out)
