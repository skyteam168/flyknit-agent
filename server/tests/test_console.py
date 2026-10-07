"""管理后台网页：具名账号登录后调配置接口、首页统计、语音配置。"""

from datetime import date, timedelta

import httpx
import respx
from conftest import ADMIN, setup_models

from app import db
from app.models import Device, UsageDaily
from app.services import usage_store


async def login(client, username="it.zhang", password="init-pass-123", **flags) -> dict:
    """建号、登录、改掉初始密码——真人就是这么走的，改密之前这个账号什么也做不了。"""
    body = {"username": username, "password": password, "display_name": "张工"}
    body.update(flags)
    r = await client.post("/api/v1/admin/users", headers=ADMIN, json=body)
    assert r.status_code == 201, r.text
    r = await client.post("/api/v1/admin/login", json={"username": username, "password": password})
    assert r.status_code == 200, r.text
    headers = {"Authorization": f"Bearer {r.json()['token']}"}

    changed = password + "-changed"
    r = await client.post("/api/v1/admin/password", headers=headers,
                          json={"old_password": password, "new_password": changed})
    assert r.status_code == 204, r.text
    # 改密会把已有会话全部踢掉，所以要用新密码重新登录
    r = await client.post("/api/v1/admin/login", json={"username": username, "password": changed})
    assert r.status_code == 200, r.text
    return {"Authorization": f"Bearer {r.json()['token']}"}


async def test_session_token_works_for_config_endpoints(client):
    headers = await login(client)
    assert (await client.get("/api/v1/admin/providers", headers=headers)).status_code == 200
    me = (await client.get("/api/v1/admin/me", headers=headers)).json()
    assert me["username"] == "it.zhang" and me["display_name"] == "张工"

    # 乱填的令牌依然拒绝
    bad = {"Authorization": "Bearer not-a-token"}
    assert (await client.get("/api/v1/admin/providers", headers=bad)).status_code == 403


async def test_logout_revokes_the_session(client):
    headers = await login(client)
    assert (await client.post("/api/v1/admin/logout", headers=headers)).status_code == 204
    assert (await client.get("/api/v1/admin/providers", headers=headers)).status_code == 403
    assert (await client.get("/api/v1/admin/me", headers=headers)).status_code == 401


async def test_disabling_an_account_kicks_it_out(client):
    await login(client)  # 第一个号是超级管理员，留着，否则后台没人管得了
    headers = await login(client, username="it.li", password="init-pass-456")
    user_id = (await client.get("/api/v1/admin/me", headers=headers)).json()["id"]
    r = await client.patch(f"/api/v1/admin/users/{user_id}", headers=ADMIN, json={"disabled": True})
    assert r.status_code == 200 and r.json()["disabled"] is True
    assert (await client.get("/api/v1/admin/providers", headers=headers)).status_code == 403


async def test_resetting_a_password_forces_a_change(client):
    headers = await login(client)
    user_id = (await client.get("/api/v1/admin/me", headers=headers)).json()["id"]
    r = await client.patch(f"/api/v1/admin/users/{user_id}", headers=ADMIN, json={"password": "reset-pass-456"})
    assert r.json()["must_change_password"] is True
    r = await client.post("/api/v1/admin/login", json={"username": "it.zhang", "password": "reset-pass-456"})
    assert r.status_code == 200


async def _seed_usage():
    today = date.fromisoformat(usage_store.today())
    async with db.get_sessionmaker()() as s:
        a = Device(token_hash="a" * 64, machine_name="PC-A", user_name="nguyen")
        b = Device(token_hash="b" * 64, machine_name="PC-B", user_name="tran")
        s.add_all([a, b])
        await s.flush()
        s.add_all([
            UsageDaily(device_id=a.id, day=today.isoformat(), scene="agent", prompt_tokens=1000, completion_tokens=200, requests=3),
            UsageDaily(device_id=b.id, day=today.isoformat(), scene="chat", prompt_tokens=300, completion_tokens=100, requests=2),
            UsageDaily(device_id=a.id, day=(today - timedelta(days=1)).isoformat(), scene="chat", prompt_tokens=500, completion_tokens=0, requests=1),
            UsageDaily(device_id=b.id, day=(today - timedelta(days=30)).isoformat(), scene="chat", prompt_tokens=9999, completion_tokens=0, requests=1),
        ])
        await s.commit()


async def test_dashboard_sums_today_and_ranks_users(client, device_headers):
    await _seed_usage()
    await client.get("/api/v1/client/usage", headers=device_headers)   # 客户端来过一次请求才算在线
    data = (await client.get("/api/v1/admin/dashboard?days=7", headers=ADMIN)).json()

    assert data["online_devices"] == 1
    assert data["online"][0]["machine_name"] == "PC-001"
    assert data["total_devices"] == 3
    assert data["today"]["tokens"] == 1600
    assert data["today"]["requests"] == 5
    assert data["yesterday"]["tokens"] == 500
    assert data["active_today"] == 2

    assert len(data["daily"]) == 7 and data["daily"][-1]["tokens"] == 1600
    assert data["daily"][-2]["tokens"] == 500
    assert sum(d["tokens"] for d in data["daily"]) == 2100   # 30 天前那条不在范围里

    assert [u["machine_name"] for u in data["users"]] == ["PC-A", "PC-B"]
    assert data["users"][0]["tokens"] == 1700 and data["users"][0]["today_tokens"] == 1200
    assert data["user_series"][0]["data"][-2:] == [500, 1200]
    assert {s["scene"]: s["tokens"] for s in data["scenes_today"]} == {"agent": 1200, "chat": 400}


async def test_shared_asr_hotwords_are_normalized(client):
    r = await client.put("/api/v1/admin/asr/hotwords", headers=ADMIN, json={"hotwords": "七号机台，飞织鞋面,七号机台\n楦头"})
    assert r.json()["shared_hotwords"] == ["七号机台", "飞织鞋面", "楦头"]
    data = (await client.get("/api/v1/admin/asr", headers=ADMIN)).json()
    assert data["shared_hotwords"] == ["七号机台", "飞织鞋面", "楦头"]
    assert data["model_id"] is None


async def test_asr_probe_without_a_route_explains_itself(client):
    r = await client.post("/api/v1/admin/asr/probe", headers=ADMIN)
    assert r.status_code == 400
    assert "语音转文字" in r.json()["detail"]


@respx.mock
async def test_asr_probe_saves_the_working_transport(client):
    m1, _ = await setup_models(client)
    p = (await client.post("/api/v1/admin/providers", headers=ADMIN,
                           json={"name": "百炼语音", "base_url": "http://asr.local/api/v1", "api_key": "sk-1"})).json()
    m = (await client.post("/api/v1/admin/models", headers=ADMIN,
                           json={"provider_id": p["id"], "name": "ASR", "model": "qwen3-asr-flash", "supports_tools": False})).json()
    await client.put("/api/v1/admin/routes/asr", headers=ADMIN, json={"model_id": m["id"]})
    respx.post(url__regex=r"http://asr\.local/.*").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "你好"}}]})
    )

    r = await client.post("/api/v1/admin/asr/probe?save=true", headers=ADMIN)
    body = r.json()
    assert body["ok"] is True and body["transport"] == "inline" and body["saved"] is True
    assert (await client.get("/api/v1/admin/asr", headers=ADMIN)).json()["transport"] == "inline"
