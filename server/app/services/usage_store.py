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
SCENES = ("agent", "chat", "translate", "vision", "title", "asr")


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
        "daily_limit": quota["daily_tokens"],
        "remaining": max(0, quota["daily_tokens"] - total_today) if quota["daily_tokens"] else 0,
        "exceeded": bool(quota["daily_tokens"]) and total_today >= quota["daily_tokens"],
        "by_scene": [{"scene": s, **v} for s, v in sorted(by_scene.items())],
        "by_day": [{"day": d, "tokens": by_day.get(d, 0)} for d in sorted(by_day)],
        "contact_name": quota["contact_name"],
        "contact_email": quota["contact_email"],
        "contact_phone": quota["contact_phone"],
    }


async def all_stats(session: AsyncSession, days: int = 7) -> list[dict]:
    """管理后台：每台电脑的用量汇总。"""
    start = (date.fromisoformat(today()) - timedelta(days=days - 1)).isoformat()
    rows = (
        await session.execute(
            select(
                UsageDaily.device_id,
                Device.machine_name,
                Device.user_name,
                func.sum(UsageDaily.prompt_tokens + UsageDaily.completion_tokens),
                func.sum(UsageDaily.requests),
            )
            .join(Device, Device.id == UsageDaily.device_id, isouter=True)
            .where(UsageDaily.day >= start)
            .group_by(UsageDaily.device_id, Device.machine_name, Device.user_name)
            .order_by(func.sum(UsageDaily.prompt_tokens + UsageDaily.completion_tokens).desc())
        )
    ).all()
    out = []
    for device_id, machine, user, tokens, requests in rows:
        out.append({
            "device_id": device_id,
            "machine_name": machine or "",
            "user_name": user or "",
            "tokens": int(tokens or 0),
            "requests": int(requests or 0),
            "today_tokens": await used_today(session, device_id),
        })
    return out


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
