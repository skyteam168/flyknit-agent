"""看门狗（docs/c3-watchdog.md）：心跳只记任务编号和来源、失联判定、手机端「电脑离线」、用途统计、安全中心默认值。"""

import json
from datetime import datetime, timedelta, timezone

from sqlalchemy import select

from app import db
from app.models import Device
from app.routers import mobile as mobile_router
from app.services import usage_store
from conftest import ADMIN


async def _device() -> Device:
    async with db.get_sessionmaker()() as s:
        return (await s.scalars(select(Device))).first()


async def _age_heartbeat(minutes: int) -> None:
    async with db.get_sessionmaker()() as s:
        d = (await s.scalars(select(Device))).first()
        d.heartbeat_at = datetime.now(timezone.utc) - timedelta(minutes=minutes)
        await s.commit()


async def _admin_device(client) -> dict:
    return (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()[0]


async def test_heartbeat_keeps_only_task_ids_and_origins(client, device_headers):
    r = await client.post("/api/v1/client/heartbeat", headers=device_headers, json={"running": [
        {"task_id": "t-1", "origin": "mobile", "title": "把工资表发给我", "content": "机密"},
        {"task_id": "t-2", "origin": "ui"},
    ]})
    assert r.status_code == 204, r.text
    d = await _device()
    stored = json.loads(d.running_tasks)
    assert stored == [{"task_id": "t-1", "origin": "mobile"}, {"task_id": "t-2", "origin": "ui"}]
    assert "工资" not in d.running_tasks and "机密" not in d.running_tasks   # 标题、内容不落库
    assert d.heartbeat_at is not None

    out = await _admin_device(client)
    assert out["running_count"] == 2
    assert out["stalled"] is False
    assert "running_tasks" not in out                                     # 列表里只给个数

    # 最多 20 条；没有设备 Token 不收
    too_many = {"running": [{"task_id": f"t{i}"} for i in range(21)]}
    assert (await client.post("/api/v1/client/heartbeat", headers=device_headers, json=too_many)).status_code == 422
    assert (await client.post("/api/v1/client/heartbeat", json={"running": []})).status_code == 401


async def test_a_computer_with_running_tasks_and_no_heartbeat_is_stalled(client, device_headers):
    await client.post("/api/v1/client/heartbeat", headers=device_headers, json={"running": [{"task_id": "t-1"}]})
    await _age_heartbeat(5)
    assert (await _admin_device(client))["stalled"] is False               # 5 分钟：还在容忍范围
    await _age_heartbeat(11)
    assert (await _admin_device(client))["stalled"] is True                # 有任务在跑、11 分钟没心跳：失联

    # 最后一个任务结束后报一次空的：没任务在跑，多久没心跳都不算失联
    await client.post("/api/v1/client/heartbeat", headers=device_headers, json={"running": []})
    await _age_heartbeat(60)
    out = await _admin_device(client)
    assert out["running_count"] == 0 and out["stalled"] is False


async def test_the_phone_sees_a_stalled_computer_as_offline(client, device_headers, monkeypatch):
    monkeypatch.setattr(mobile_router.channels, "online", lambda device_id: True)  # 通道连着
    await client.post("/api/v1/client/heartbeat", headers=device_headers, json={"running": [{"task_id": "t-1", "origin": "mobile"}]})
    assert mobile_router._computer_online(await _device()) is True
    await _age_heartbeat(15)
    assert mobile_router._computer_online(await _device()) is False         # 连着但任务没动静：显示成电脑离线
    monkeypatch.setattr(mobile_router.channels, "online", lambda device_id: False)
    await client.post("/api/v1/client/heartbeat", headers=device_headers, json={"running": []})
    assert mobile_router._computer_online(await _device()) is False         # 通道断了照旧是离线


async def test_old_devices_without_heartbeat_are_not_stalled(client, device_headers):
    out = await _admin_device(client)
    assert out["running_count"] == 0 and out["stalled"] is False and out["heartbeat_at"] is None


def test_watchdog_is_a_known_purpose():
    assert usage_store.clean_purpose("watchdog") == "watchdog"


async def test_watchdog_settings_default_off(client, device_headers):
    sec = (await client.get("/api/v1/client/config", headers=device_headers)).json()["security"]
    assert sec["watchdog"]["value"] is False
    assert sec["watchdog_auto_stop"]["value"] is False
    assert sec["watchdog_minutes"]["value"] == 10
    assert all(sec[k]["locked"] for k in ("watchdog", "watchdog_auto_stop", "watchdog_minutes"))


def test_router_is_a_known_purpose():
    assert usage_store.clean_purpose("router") == "router"


async def test_router_settings_default_off(client, device_headers):
    """C2 调度：三个开关默认关 / 一件接一件，都由 IT 统一配置。"""
    sec = (await client.get("/api/v1/client/config", headers=device_headers)).json()["security"]
    assert sec["router"]["value"] is False
    assert sec["router_split"]["value"] is False
    assert sec["router_parallel"]["value"] == 1
    assert all(sec[k]["locked"] for k in ("router", "router_split", "router_parallel"))
