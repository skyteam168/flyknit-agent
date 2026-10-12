"""Token 用量统计与配额。

用量按「设备 + 日期 + 场景」累加，数据来自上游模型返回的 usage 字段（服务端自己解析，客户端改不了）。
管理员可以设置每台电脑每天的 token 上限，超出后网关直接拒绝，客户端提示联系 IT。
"""

from __future__ import annotations

import json
from datetime import date, datetime, timedelta, timezone

from sqlalchemy import func, select, update
from sqlalchemy.exc import IntegrityError
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import ChatRecord, Device, UsageDaily, UsageModelDaily
from . import settings_store

QUOTA_KEY = "quota"

DEFAULT_QUOTA = {
    # 每台电脑每天的 token 上限，0 表示不限制
    "daily_tokens": 0,
    "contact_name": "IT 管理员",
    "contact_email": "jamesyang@shenzhougroup.com",
    "contact_phone": "7815",
}

# 统计里展示的场景（title 场景只是生成标题，并入对话）
SCENES = ("agent", "chat", "translate", "vision", "title", "asr", "embedding")


def today(tz_offset_hours: int = 8) -> str:
    """按本地时区算“今天”，工厂都在同一个时区，默认 UTC+8。"""
    return (datetime.now(timezone.utc) + timedelta(hours=tz_offset_hours)).date().isoformat()


async def get_quota(session: AsyncSession) -> dict:
    quota = await settings_store.get_value(session, QUOTA_KEY, DEFAULT_QUOTA)
    for key, value in DEFAULT_QUOTA.items():
        quota.setdefault(key, value)
    try:
        quota["daily_tokens"] = max(0, int(quota["daily_tokens"]))
    except (TypeError, ValueError):
        quota["daily_tokens"] = 0
    return quota


async def set_quota(session: AsyncSession, quota: dict) -> dict:
    merged = await get_quota(session)
    merged.update({k: v for k, v in quota.items() if v is not None})
    merged["daily_tokens"] = max(0, int(merged.get("daily_tokens") or 0))
    await settings_store.set_value(session, QUOTA_KEY, merged)
    return merged


def limit_for(quota: dict, device: Device | None) -> int:
    """这台电脑每天的上限：单独设置过就用它自己的（0 表示不限），否则跟全局配额走。"""
    if device is not None and device.daily_tokens is not None:
        return max(0, int(device.daily_tokens))
    return quota["daily_tokens"]


async def used_today(session: AsyncSession, device_id: int, day: str | None = None) -> int:
    day = day or today()
    total = await session.scalar(
        select(func.coalesce(func.sum(UsageDaily.prompt_tokens + UsageDaily.completion_tokens), 0))
        .where(UsageDaily.device_id == device_id, UsageDaily.day == day)
    )
    return int(total or 0)


async def record(
    session: AsyncSession, device_id: int, scene: str, prompt: int, completion: int,
    model_id: int | None = None, model_name: str = "",
) -> None:
    """累加一次调用的用量。带上模型名的话，按模型的统计也记一笔。"""
    if prompt <= 0 and completion <= 0:
        return
    day = today()
    scene = scene if scene in SCENES else "chat"
    await _bump(session, UsageDaily, {"device_id": device_id, "day": day, "scene": scene},
                {"prompt_tokens": max(0, prompt), "completion_tokens": max(0, completion), "requests": 1})
    if model_name:
        await _add_model_usage(session, day, model_id, model_name, scene, max(0, prompt), max(0, completion), 1)
    await session.commit()


async def _add_model_usage(
    session: AsyncSession, day: str, model_id: int | None, model_name: str, scene: str,
    prompt: int, completion: int, requests: int,
) -> None:
    name = model_name.strip()[:200] or "未知模型"
    await _bump(session, UsageModelDaily, {"day": day, "model_name": name, "scene": scene},
                {"prompt_tokens": prompt, "completion_tokens": completion, "requests": requests},
                {"model_id": model_id} if model_id is not None else {})


async def _bump(session: AsyncSession, model, keys: dict, add: dict[str, int], also: dict | None = None) -> None:
    """
    原子地累加一行计数。

    原来是「读出来、加上、写回去」，两个请求同时结束时后写的会盖掉先写的，用量少记。
    现在直接 UPDATE col = col + n；这一行还没有就插入。两个请求同时插当天第一行时，
    慢的那个会撞唯一约束——回滚到保存点，再 UPDATE 一次就行。
    """
    also = also or {}
    stmt = (
        update(model)
        .where(*(getattr(model, k) == v for k, v in keys.items()))
        .values({**{k: getattr(model, k) + v for k, v in add.items()}, **also})
        .execution_options(synchronize_session=False)
    )
    if (await session.execute(stmt)).rowcount:
        return
    try:
        async with session.begin_nested():
            session.add(model(**keys, **add, **also))
    except IntegrityError:
        await session.execute(stmt)


MODEL_BACKFILL_KEY = "migration.usage_by_model_v1"


async def backfill_model_usage(session: AsyncSession, tz_offset_hours: int = 8) -> int:
    """
    升级上来时，按模型的统计是空的。聊天记录里每一轮都存了当时用的模型和 token，用它补出历史数据，只做一次。

    补出来的会比实际略少：压缩上下文、生成标题这类后台调用不进聊天记录；被清理掉的旧记录也补不回来。
    返回补了几条聊天记录。
    """
    if await settings_store.get_value(session, MODEL_BACKFILL_KEY, None) is not None:
        return 0
    if await session.scalar(select(func.count()).select_from(UsageModelDaily)):
        await settings_store.set_value(session, MODEL_BACKFILL_KEY, {"rows": 0, "skipped": "already has data"})
        return 0
    totals: dict[tuple[str, str, str], list[int]] = {}
    n = 0
    result = await session.stream(
        select(ChatRecord.created_at, ChatRecord.model, ChatRecord.scene,
               ChatRecord.prompt_tokens, ChatRecord.completion_tokens)
    )
    async for created_at, model, scene, prompt, completion in result:
        if not model or (prompt or 0) + (completion or 0) <= 0 or created_at is None:
            continue
        at = created_at if created_at.tzinfo else created_at.replace(tzinfo=timezone.utc)
        day = (at + timedelta(hours=tz_offset_hours)).date().isoformat()
        key = (day, model, scene if scene in SCENES else "chat")
        t = totals.setdefault(key, [0, 0, 0])
        t[0] += max(0, prompt or 0)
        t[1] += max(0, completion or 0)
        t[2] += 1
        n += 1
    for (day, model, scene), (prompt, completion, requests) in totals.items():
        await _add_model_usage(session, day, None, model, scene, prompt, completion, requests)
    await settings_store.set_value(session, MODEL_BACKFILL_KEY, {"rows": n})
    await session.commit()
    return n


async def record_request(session: AsyncSession, device_id: int, scene: str) -> None:
    """只记一次调用，不计 token。语音转文字按音频时长计费，没有 token 可记。"""
    scene = scene if scene in SCENES else "chat"
    await _bump(session, UsageDaily, {"device_id": device_id, "day": today(), "scene": scene}, {"requests": 1})
    await session.commit()


async def device_stats(session: AsyncSession, device_id: int, days: int = 7) -> dict:
    """某台电脑的用量：今天按场景分、最近几天按天分。"""
    start = (date.fromisoformat(today()) - timedelta(days=days - 1)).isoformat()
    rows = (
        await session.execute(
            select(UsageDaily.day, UsageDaily.scene, UsageDaily.prompt_tokens, UsageDaily.completion_tokens, UsageDaily.requests)
            .where(UsageDaily.device_id == device_id, UsageDaily.day >= start)
            .order_by(UsageDaily.day)
        )
    ).all()
    quota = await get_quota(session)
    limit = limit_for(quota, await session.get(Device, device_id))
    day = today()

    by_scene: dict[str, dict[str, int]] = {}
    by_day: dict[str, int] = {}
    total_today = 0
    for d, scene, prompt, completion, requests in rows:
        total = prompt + completion
        by_day[d] = by_day.get(d, 0) + total
        if d == day:
            entry = by_scene.setdefault(scene, {"prompt": 0, "completion": 0, "requests": 0, "total": 0})
            entry["prompt"] += prompt
            entry["completion"] += completion
            entry["requests"] += requests
            entry["total"] += total
            total_today += total

    return {
        "day": day,
        "today_tokens": total_today,
        "daily_limit": limit,
        "remaining": max(0, limit - total_today) if limit else 0,
        "exceeded": bool(limit) and total_today >= limit,
        "by_scene": [{"scene": s, **v} for s, v in sorted(by_scene.items())],
        "by_day": [{"day": d, "tokens": by_day.get(d, 0)} for d in sorted(by_day)],
        "contact_name": quota["contact_name"],
        "contact_email": quota["contact_email"],
        "contact_phone": quota["contact_phone"],
    }


async def all_stats(session: AsyncSession, days: int = 7) -> list[dict]:
    """管理后台：每台电脑的用量和每日上限。

    没有用量的电脑也列出来（停用的除外）——单独给某台调额度，往往正是在它还没开始用之前。
    """
    day = today()
    start = (date.fromisoformat(day) - timedelta(days=days - 1)).isoformat()
    rows = (
        await session.execute(
            select(
                UsageDaily.device_id,
                UsageDaily.day,
                func.sum(UsageDaily.prompt_tokens + UsageDaily.completion_tokens),
                func.sum(UsageDaily.requests),
            )
            .where(UsageDaily.day >= start)
            .group_by(UsageDaily.device_id, UsageDaily.day)
        )
    ).all()
    used: dict[int, dict[str, int]] = {}
    for device_id, d, tokens, requests in rows:
        entry = used.setdefault(device_id, {"tokens": 0, "requests": 0, "today_tokens": 0})
        entry["tokens"] += int(tokens or 0)
        entry["requests"] += int(requests or 0)
        if d == day:
            entry["today_tokens"] += int(tokens or 0)

    quota = await get_quota(session)
    devices = {d.id: d for d in await session.scalars(select(Device))}
    out = []
    for device_id in set(used) | {i for i, d in devices.items() if not d.disabled or d.daily_tokens is not None}:
        d = devices.get(device_id)
        stats = used.get(device_id, {"tokens": 0, "requests": 0, "today_tokens": 0})
        out.append({
            "device_id": device_id,
            "machine_name": d.machine_name if d else "",
            "user_name": d.user_name if d else "",
            "owner": d.owner if d else "",
            "department": d.department if d else "",
            "disabled": d.disabled if d else False,
            "daily_tokens": d.daily_tokens if d else None,
            "daily_limit": limit_for(quota, d),
            **stats,
        })
    out.sort(key=lambda u: (-u["tokens"], -u["today_tokens"], u["machine_name"]))
    return out


async def set_device_limit(session: AsyncSession, device: Device, daily_tokens: int | None) -> None:
    """单独设置一台电脑的每日上限。None 表示恢复跟全局走。"""
    device.daily_tokens = None if daily_tokens is None else max(0, int(daily_tokens))
    await session.commit()


def extract_usage(payload: str | bytes) -> tuple[int, int]:
    """从模型返回的 JSON 里取出 usage。"""
    try:
        data = json.loads(payload)
    except (ValueError, TypeError):
        return 0, 0
    usage = data.get("usage") if isinstance(data, dict) else None
    if not isinstance(usage, dict):
        return 0, 0
    try:
        return int(usage.get("prompt_tokens") or 0), int(usage.get("completion_tokens") or 0)
    except (TypeError, ValueError):
        return 0, 0


class StreamUsageScanner:
    """边转发边从 SSE 流里找 usage（客户端请求时带了 stream_options.include_usage）。"""

    def __init__(self) -> None:
        self.prompt = 0
        self.completion = 0
        self._buffer = b""

    def feed(self, chunk: bytes) -> None:
        self._buffer += chunk
        # 只按整行解析，半行留到下一个分片
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
        if not line.startswith(b"data:") or b'"usage"' not in line:
            return
        prompt, completion = extract_usage(line[5:].strip())
        if prompt or completion:
            # 同一次请求只会报一次总量，取最大值避免重复累加
            self.prompt = max(self.prompt, prompt)
            self.completion = max(self.completion, completion)
