"""管理后台网页用的聚合接口：首页统计、语音转文字配置。

其余配置（模型、配额、技能、设备、审计）网页直接调 admin.py 里现成的接口，
这里只放那些需要服务端先汇总、或者命令行脚本里才有的逻辑。
"""

import logging
import math
import struct
from datetime import date, datetime, timedelta, timezone

import httpx
from fastapi import APIRouter, Depends, HTTPException, Query, Request, status
from pydantic import BaseModel
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..db import get_session
from ..deps import require_admin
from ..models import Device, ModelConfig, RouteRule, UsageDaily
from ..services import asr, model_router, settings_store, usage_store
from .speech import build_target

log = logging.getLogger("flyknit.console")
router = APIRouter(prefix="/api/v1/admin", tags=["console"], dependencies=[Depends(require_admin)])

#: 客户端每 10 分钟拉一次配置，每次都会刷新 last_seen；留 5 分钟余量
ONLINE_MINUTES = 15
#: 「每人每天」那张堆叠图最多画几个人，再多就糊成一片了
SERIES_USERS = 5


def _aware(value: datetime | None) -> datetime | None:
    # SQLite 读回来不带时区，存进去的时候就是 UTC
    if value is None:
        return None
    return value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value


def _label(machine: str, user: str, device_id: int | None) -> str:
    if user and machine:
        return f"{user}（{machine}）"
    return user or machine or f"设备 #{device_id}"


@router.get("/dashboard")
async def dashboard(
    days: int = Query(14, ge=1, le=90),
    top: int = Query(10, ge=1, le=50),
    session: AsyncSession = Depends(get_session),
):
    """首页：在线设备、今天的消耗、每天的走势、每个人用了多少。"""
    now = datetime.now(timezone.utc)
    devices = (await session.scalars(select(Device))).all()
    online_since = now - timedelta(minutes=ONLINE_MINUTES)
    online = sorted(
        (d for d in devices if not d.disabled and (seen := _aware(d.last_seen)) and seen >= online_since),
        key=lambda d: _aware(d.last_seen),
        reverse=True,
    )
    by_id = {d.id: d for d in devices}

    today = usage_store.today()
    yesterday = (date.fromisoformat(today) - timedelta(days=1)).isoformat()
    start = (date.fromisoformat(today) - timedelta(days=days - 1)).isoformat()
    day_list = [(date.fromisoformat(start) + timedelta(days=i)).isoformat() for i in range(days)]

    rows = (
        await session.execute(
            select(
                UsageDaily.device_id, UsageDaily.day, UsageDaily.scene,
                UsageDaily.prompt_tokens, UsageDaily.completion_tokens, UsageDaily.requests,
            ).where(UsageDaily.day >= min(start, yesterday))
        )
    ).all()

    daily = {d: {"prompt": 0, "completion": 0, "requests": 0} for d in day_list}
    totals = {"today": {"prompt": 0, "completion": 0, "requests": 0}, "yesterday": {"prompt": 0, "completion": 0, "requests": 0}}
    per_device: dict[int | None, dict] = {}
    per_device_day: dict[int | None, dict[str, int]] = {}
    scenes_today: dict[str, dict[str, int]] = {}
    scenes_range: dict[str, dict[str, int]] = {}

    for device_id, day, scene, prompt, completion, requests in rows:
        tokens = prompt + completion
        for key, d in (("today", today), ("yesterday", yesterday)):
            if day == d:
                totals[key]["prompt"] += prompt
                totals[key]["completion"] += completion
                totals[key]["requests"] += requests
        if day < start:
            continue
        daily[day]["prompt"] += prompt
        daily[day]["completion"] += completion
        daily[day]["requests"] += requests

        entry = per_device.setdefault(device_id, {"tokens": 0, "requests": 0, "today_tokens": 0})
        entry["tokens"] += tokens
        entry["requests"] += requests
        if day == today:
            entry["today_tokens"] += tokens
        series = per_device_day.setdefault(device_id, {})
        series[day] = series.get(day, 0) + tokens

        for bucket, include in ((scenes_range, True), (scenes_today, day == today)):
            if include:
                s = bucket.setdefault(scene, {"tokens": 0, "requests": 0})
                s["tokens"] += tokens
                s["requests"] += requests

    def device_info(device_id: int | None) -> dict:
        d = by_id.get(device_id) if device_id is not None else None
        machine, user = (d.machine_name, d.user_name) if d else ("", "")
        return {"device_id": device_id, "machine_name": machine, "user_name": user, "label": _label(machine, user, device_id)}

    ranked = sorted(per_device.items(), key=lambda kv: kv[1]["tokens"], reverse=True)
    users = [{**device_info(device_id), **stats} for device_id, stats in ranked[:top]]
    user_series = [
        {**device_info(device_id), "data": [per_device_day.get(device_id, {}).get(d, 0) for d in day_list]}
        for device_id, _ in ranked[:SERIES_USERS]
    ]

    def total(t: dict) -> dict:
        return {**t, "tokens": t["prompt"] + t["completion"]}

    def scene_list(bucket: dict) -> list[dict]:
        return sorted(({"scene": k, **v} for k, v in bucket.items()), key=lambda s: s["tokens"], reverse=True)

    quota = await usage_store.get_quota(session)
    return {
        "generated_at": now,
        "day": today,
        "online_minutes": ONLINE_MINUTES,
        "online_devices": len(online),
        "total_devices": sum(1 for d in devices if not d.disabled),
        "disabled_devices": sum(1 for d in devices if d.disabled),
        "active_today": sum(1 for s in per_device.values() if s["today_tokens"] > 0),
        "daily_limit": quota["daily_tokens"],
        # 单独设置了每日上限的电脑（其余跟 daily_limit 走）
        "custom_limits": sum(1 for d in devices if d.daily_tokens is not None and not d.disabled),
        "today": total(totals["today"]),
        "yesterday": total(totals["yesterday"]),
        "days": day_list,
        "daily": [{"day": d, **total(daily[d])} for d in day_list],
        "users": users,
        "user_series": user_series,
        "scenes_today": scene_list(scenes_today),
        "scenes": scene_list(scenes_range),
        "online": [
            {"device_id": d.id, "machine_name": d.machine_name, "user_name": d.user_name,
             "client_version": d.client_version, "last_seen": _aware(d.last_seen)}
            for d in online[:20]
        ],
    }


# ---------- 语音转文字 ----------

def _split_words(raw) -> list[str]:
    if isinstance(raw, list):
        items = raw
    else:
        items = str(raw or "").replace("，", ",").replace("\n", ",").split(",")
    return list(dict.fromkeys(w for w in (str(i).strip() for i in items) if w))


class AsrHotwordsIn(BaseModel):
    hotwords: list[str] | str = []


@router.get("/asr")
async def get_asr(session: AsyncSession = Depends(get_session)):
    """语音转文字的现状：asr 场景路由到哪个模型、它的调用参数、全厂热词。"""
    shared = (await settings_store.get_value(session, "asr", {})).get("hotwords") or []
    route = await session.get(RouteRule, "asr")
    model = await session.get(ModelConfig, route.model_id) if route and route.model_id else None
    extra = dict(model.extra_body or {}) if model else {}
    return {
        "model_id": model.id if model else None,
        "model_name": model.name if model else "",
        "upstream_model": model.model if model else "",
        "provider": model.provider.name if model else "",
        "base_url": model.provider.base_url if model else "",
        "transport": extra.get("asr_transport", "auto"),
        "language": extra.get("asr_language", ""),
        "hotwords": extra.get("hotwords", ""),
        "vocabulary_id": extra.get("asr_vocabulary_id", ""),
        "shared_hotwords": _split_words(shared),
    }


@router.put("/asr/hotwords")
async def put_asr_hotwords(data: AsrHotwordsIn, session: AsyncSession = Depends(get_session)):
    """全厂热词（机台号、工序名…）。改一次所有电脑的语音输入都生效，不用重启客户端。"""
    value = await settings_store.get_value(session, "asr", {})
    value["hotwords"] = _split_words(data.hotwords)
    await settings_store.set_value(session, "asr", value)
    return {"shared_hotwords": value["hotwords"]}


def _sample_wav(seconds: float = 1.0, rate: int = 16000) -> bytes:
    """一段很轻的音频，只用来探测通道是否可用。纯静音有的上游会直接判无效，所以放个小正弦。"""
    frames = int(rate * seconds)
    body = bytearray()
    for i in range(frames):
        body += struct.pack("<h", int(1200 * math.sin(2 * math.pi * 220 * i / rate)))
    header = (
        b"RIFF" + struct.pack("<I", 36 + len(body)) + b"WAVEfmt " + struct.pack("<I", 16)
        + struct.pack("<HHIIHH", 1, 1, rate, rate * 2, 2, 16)
        + b"data" + struct.pack("<I", len(body))
    )
    return header + bytes(body)


@router.post("/asr/probe")
async def probe_asr(request: Request, save: bool = False, session: AsyncSession = Depends(get_session)):
    """
    真发一段 1 秒的音频，逐条试 inline / filetrans 哪条能通。
    save=true 时把能通的那条写进模型配置，客户端就不用每次先试错。
    """
    try:
        target = await build_target(session)
    except model_router.NoRouteError as exc:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "还没有给「语音转文字」场景指定模型") from exc

    audio = _sample_wav()
    notes: dict[str, str] = {}
    chosen: str | None = None
    for name in asr.TRANSPORTS:
        try:
            result = await asr.try_transport(request.app.state.http, target, audio, "wav", name)
        except asr.AsrError as exc:
            notes[name] = str(exc)
            continue
        except httpx.HTTPError as exc:
            notes[name] = f"网络不通：{exc}"
            continue
        heard = (result.text or "").strip()
        notes[name] = "可用" + (f"（识别到「{heard[:20]}」）" if heard else "（没识别出内容，但通道是通的）")
        chosen = name
        break

    if chosen and save:
        route = await session.get(RouteRule, "asr")
        model = await session.get(ModelConfig, route.model_id)
        extra = dict(model.extra_body or {})
        extra["asr_transport"] = chosen
        model.extra_body = extra
        await session.commit()
        asr.forget_learned()
        log.info("语音通道探测后写入配置：%s → %s", model.name, chosen)
    return {"ok": chosen is not None, "transport": chosen, "notes": notes, "saved": bool(chosen and save)}
