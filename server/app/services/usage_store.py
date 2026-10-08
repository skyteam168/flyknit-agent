"""Token 用量统计与配额。

用量按「设备 + 日期 + 场景」累加，数据来自上游模型返回的 usage 字段（服务端自己解析，客户端改不了）。
管理员可以设置每台电脑每天的 token 上限，超出后网关直接拒绝，客户端提示联系 IT。
"""

from __future__ import annotations

import json
from datetime import date, datetime, timedelta, timezone

from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import Device, UsageDaily
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


async def record(session: AsyncSession, device_id: int, scene: str, prompt: int, completion: int) -> None:
    """累加一次调用的用量。"""
    if prompt <= 0 and completion <= 0:
        return
    day = today()
    scene = scene if scene in SCENES else "chat"
    row = await session.scalar(
        select(UsageDaily).where(UsageDaily.device_id == device_id, UsageDaily.day == day, UsageDaily.scene == scene)
    )
    if row is None:
        row = UsageDaily(device_id=device_id, day=day, scene=scene, prompt_tokens=0, completion_tokens=0, requests=0)
        session.add(row)
    row.prompt_tokens += max(0, prompt)
    row.completion_tokens += max(0, completion)
    row.requests += 1
    await session.commit()


async def record_request(session: AsyncSession, device_id: int, scene: str) -> None:
    """只记一次调用，不计 token。语音转文字按音频时长计费，没有 token 可记。"""
    day = today()
    scene = scene if scene in SCENES else "chat"
    row = await session.scalar(
        select(UsageDaily).where(UsageDaily.device_id == device_id, UsageDaily.day == day, UsageDaily.scene == scene)
    )
    if row is None:
        row = UsageDaily(device_id=device_id, day=day, scene=scene, prompt_tokens=0, completion_tokens=0, requests=0)
        session.add(row)
    row.requests += 1
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
