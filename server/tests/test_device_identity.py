"""同一台电脑上的同一个员工只占一行：退出登录再登录、升级重装都沿用原来那条，用量跟着走。"""

from datetime import datetime, timedelta, timezone

import pytest
from sqlalchemy import select

from app import db
from app.models import Device, UsageDaily
from app.services import usage_store
from conftest import ADMIN

pytestmark = pytest.mark.asyncio

GUID = "5f1c0d2e-aaaa-bbbb-cccc-0123456789ab"


async def register(client, user="SZTERM\\yangxiaowei", guid=GUID, machine="TZML-131-220"):
    r = await client.post("/api/v1/devices/register", json={
        "enrollment_key": "test-enroll", "machine_name": machine, "user_name": user, "machine_guid": guid,
    })
    assert r.status_code == 200, r.text
    return r.json(), {"Authorization": f"Bearer {r.json()['token']}"}


async def beat(client, headers, user="yangxiaowei", guid=GUID):
    r = await client.post("/api/v1/devices/heartbeat", headers=headers,
                          json={"machine_guid": guid, "user_name": user, "domain": "SZTERM", "client_version": "0.3.1"})
    assert r.status_code == 204, r.text


async def add_usage(device_id, tokens, scene="agent"):
    async with db.get_sessionmaker()() as s:
        await usage_store.record(s, device_id, scene, tokens, 0)


async def devices(client):
    return (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()


async def today_tokens(client, device_id):
    rows = (await client.get("/api/v1/admin/usage?days=1", headers=ADMIN)).json()
    return next((u["today_tokens"] for u in rows if u["device_id"] == device_id), 0)


async def test_logging_in_again_keeps_the_same_device_and_its_usage(client):
    first, h1 = await register(client)
    await beat(client, h1)
    await add_usage(first["device_id"], 1117)
    await client.patch(f"/api/v1/admin/devices/{first['device_id']}", headers=ADMIN, json={"owner": "IT admin"})
    assert (await client.post("/api/v1/devices/logout", headers=h1)).status_code == 204

    again, h2 = await register(client)
    assert again["device_id"] == first["device_id"]
    rows = await devices(client)
    assert len(rows) == 1
    assert rows[0]["signed_out_at"] is None and rows[0]["owner"] == "IT admin"
    assert await today_tokens(client, again["device_id"]) == 1117
    # 旧令牌不能再用
    assert (await client.post("/api/v1/devices/heartbeat", headers=h1, json={})).status_code == 401
    await beat(client, h2)


async def test_another_employee_on_the_same_pc_is_a_separate_device(client):
    a, _ = await register(client, user="SZTERM\\yangxiaowei")
    b, _ = await register(client, user="SZTERM\\li.ming")
    assert a["device_id"] != b["device_id"]
    # 没报系统标识（老客户端）的照旧各建一行
    c, _ = await register(client, guid="")
    assert c["device_id"] not in (a["device_id"], b["device_id"])


async def test_duplicates_from_older_versions_are_merged_on_heartbeat(client):
    # 老版本登录不报系统标识，每次都是新的一行；系统标识要等心跳才有
    old, h_old = await register(client, guid="")
    await beat(client, h_old)
    await add_usage(old["device_id"], 503_000)
    await add_usage(old["device_id"], 100, scene="chat")
    await client.post("/api/v1/devices/logout", headers=h_old)
    async with db.get_sessionmaker()() as s:
        row = await s.get(Device, old["device_id"])
        row.disabled = True
        await s.commit()

    new, h_new = await register(client, guid="")
    assert new["device_id"] != old["device_id"]
    await add_usage(new["device_id"], 900)
    await beat(client, h_new)

    rows = await devices(client)
    assert [d["id"] for d in rows] == [new["device_id"]]
    assert await today_tokens(client, new["device_id"]) == 503_000 + 100 + 900
    # 旧行被停用过，合并后也停用，不能靠重新登录绕开
    assert rows[0]["disabled"] is True
    async with db.get_sessionmaker()() as s:
        left = (await s.scalars(select(UsageDaily).where(UsageDaily.device_id == old["device_id"]))).all()
        assert left == []


async def test_delete_device(client):
    gone, h = await register(client, user="SZTERM\\old.user")
    await add_usage(gone["device_id"], 10)
    # 在线的不让删
    await beat(client, h, user="old.user")
    r = await client.delete(f"/api/v1/admin/devices/{gone['device_id']}", headers=ADMIN)
    assert r.status_code == 409
    # 退出登录了就可以
    await client.post("/api/v1/devices/logout", headers=h)
    r = await client.delete(f"/api/v1/admin/devices/{gone['device_id']}", headers=ADMIN)
    assert r.status_code == 204
    assert await devices(client) == []


async def test_offline_device_can_be_deleted(client):
    d, h = await register(client)
    async with db.get_sessionmaker()() as s:
        row = await s.get(Device, d["device_id"])
        row.last_seen = datetime.now(timezone.utc) - timedelta(days=3)
        await s.commit()
    assert (await client.delete(f"/api/v1/admin/devices/{d['device_id']}", headers=ADMIN)).status_code == 204
    assert (await client.delete("/api/v1/admin/devices/9999", headers=ADMIN)).status_code == 404
