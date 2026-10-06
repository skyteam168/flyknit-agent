"""运维代理：注册、下发、领取、回报、取消、安装包、权限。"""

import hashlib

import pytest

from conftest import ADMIN

pytestmark = pytest.mark.asyncio

GUID = "6f1c2a7e-0b4d-4c55-9e7a-1234567890ab"


async def register_agent(client, guid=GUID, name="PC-001"):
    r = await client.post("/api/v1/agent/register", json={
        "enrollment_key": "test-enroll", "machine_guid": guid, "machine_name": name,
        "os_version": "Windows 11 23H2", "agent_version": "0.1.0",
    })
    assert r.status_code == 200, r.text
    return r.json()["agent_id"], {"Authorization": f"Bearer {r.json()['token']}"}


async def dispatcher(client, username="it.ops", can_dispatch=True):
    r = await client.post("/api/v1/admin/users", headers=ADMIN, json={
        "username": username, "password": "flyknit-2026", "can_dispatch": can_dispatch,
    })
    assert r.status_code == 201, r.text
    token = (await client.post("/api/v1/admin/login", json={"username": username, "password": "flyknit-2026"})).json()["token"]
    await client.post("/api/v1/admin/password", headers={"Authorization": f"Bearer {token}"},
                      json={"old_password": "flyknit-2026", "new_password": "flyknit-2027"})
    token = (await client.post("/api/v1/admin/login", json={"username": username, "password": "flyknit-2027"})).json()["token"]
    return {"Authorization": f"Bearer {token}"}


async def test_register_is_idempotent_per_machine(client):
    first, h1 = await register_agent(client)
    second, h2 = await register_agent(client, name="PC-001-RENAMED")
    assert first == second
    # 重装后旧令牌作废
    assert (await client.post("/api/v1/agent/poll", headers=h1, json={})).status_code == 401
    assert (await client.post("/api/v1/agent/poll", headers=h2, json={})).status_code == 200
    agents = (await client.get("/api/v1/admin/agents", headers=ADMIN)).json()
    assert [a["machine_name"] for a in agents] == ["PC-001-RENAMED"]


async def test_wrong_enrollment_key_refused(client):
    r = await client.post("/api/v1/agent/register", json={"enrollment_key": "nope", "machine_guid": GUID})
    assert r.status_code == 403


async def test_full_cycle_collect_info(client):
    agent_id, agent = await register_agent(client)
    ops = await dispatcher(client)

    r = await client.post("/api/v1/admin/jobs", headers=ops, json={"kind": "collect_info", "agent_ids": [agent_id]})
    assert r.status_code == 201, r.text
    job = r.json()
    assert job["created_by"] == "it.ops" and job["counts"] == {"pending": 1}

    runs = (await client.post("/api/v1/agent/poll", headers=agent, json={"agent_version": "0.1.1"})).json()["runs"]
    assert len(runs) == 1 and runs[0]["kind"] == "collect_info"
    run_id = runs[0]["run_id"]

    assert (await client.post(f"/api/v1/agent/runs/{run_id}/start", headers=agent)).json()["ok"] is True
    # 已经在跑的不会再被领一次
    assert (await client.post("/api/v1/agent/poll", headers=agent, json={})).json()["runs"] == []

    inventory = {"cpu": "Intel i5-12400", "memory_gb": 16, "software": [{"name": "WPS Office"}]}
    r = await client.post(f"/api/v1/agent/runs/{run_id}/finish", headers=agent,
                          json={"status": "succeeded", "exit_code": 0, "output": "ok", "result": inventory})
    assert r.status_code == 204

    detail = (await client.get(f"/api/v1/admin/agents/{agent_id}", headers=ADMIN)).json()
    assert detail["inventory"]["cpu"] == "Intel i5-12400"
    assert detail["agent_version"] == "0.1.1"
    job = (await client.get(f"/api/v1/admin/jobs/{job['id']}", headers=ADMIN)).json()
    assert job["counts"] == {"succeeded": 1}
    assert job["runs"][0]["machine_name"] == "PC-001"


async def test_dispatch_requires_permission(client):
    agent_id, _ = await register_agent(client)
    body = {"kind": "clean", "agent_ids": [agent_id]}
    # 共享令牌不行
    assert (await client.post("/api/v1/admin/jobs", headers=ADMIN, json=body)).status_code == 401
    # 没授权的账号不行
    plain = await dispatcher(client, "it.view", can_dispatch=False)
    r = await client.post("/api/v1/admin/jobs", headers=plain, json=body)
    assert r.status_code == 403 and "下发运维任务" in r.json()["detail"]


async def test_only_catalog_tasks_and_params(client):
    agent_id, _ = await register_agent(client)
    ops = await dispatcher(client)
    r = await client.post("/api/v1/admin/jobs", headers=ops, json={"kind": "run_script", "agent_ids": [agent_id]})
    assert r.status_code == 400 and "不支持" in r.json()["detail"]
    r = await client.post("/api/v1/admin/jobs", headers=ops,
                          json={"kind": "clean", "params": {"targets": ["C:\\Windows"]}, "agent_ids": [agent_id]})
    assert r.status_code == 400
    r = await client.post("/api/v1/admin/jobs", headers=ops,
                          json={"kind": "restart", "params": {"delay_minutes": 0}, "agent_ids": [agent_id]})
    assert r.status_code == 400
    r = await client.post("/api/v1/admin/jobs", headers=ops,
                          json={"kind": "repair", "params": {"actions": ["sfc", "sfc"]}, "agent_ids": [agent_id]})
    assert r.status_code == 201 and r.json()["params"]["actions"] == ["sfc"]


async def test_cancel_only_pending(client):
    a1, h1 = await register_agent(client, GUID, "PC-001")
    a2, _ = await register_agent(client, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "PC-002")
    ops = await dispatcher(client)
    job = (await client.post("/api/v1/admin/jobs", headers=ops, json={"kind": "optimize", "agent_ids": [a1, a2]})).json()

    run = (await client.post("/api/v1/agent/poll", headers=h1, json={})).json()["runs"][0]
    await client.post(f"/api/v1/agent/runs/{run['run_id']}/start", headers=h1)

    r = await client.post(f"/api/v1/admin/jobs/{job['id']}/cancel", headers=ops)
    assert r.json()["counts"] == {"running": 1, "cancelled": 1}
    assert r.json()["cancelled_by"] == "it.ops"


async def test_cancelled_run_cannot_start(client):
    agent_id, agent = await register_agent(client)
    ops = await dispatcher(client)
    job = (await client.post("/api/v1/admin/jobs", headers=ops, json={"kind": "clean", "agent_ids": [agent_id]})).json()
    run = (await client.post("/api/v1/agent/poll", headers=agent, json={})).json()["runs"][0]
    await client.post(f"/api/v1/admin/jobs/{job['id']}/cancel", headers=ops)
    r = await client.post(f"/api/v1/agent/runs/{run['run_id']}/start", headers=agent)
    assert r.json() == {"ok": False, "status": "cancelled"}


async def test_agent_cannot_touch_other_agents_runs(client):
    a1, _ = await register_agent(client, GUID, "PC-001")
    _, h2 = await register_agent(client, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "PC-002")
    ops = await dispatcher(client)
    await client.post("/api/v1/admin/jobs", headers=ops, json={"kind": "clean", "agent_ids": [a1]})
    jobs = (await client.get("/api/v1/admin/jobs", headers=ADMIN)).json()
    run_id = (await client.get(f"/api/v1/admin/jobs/{jobs[0]['id']}", headers=ADMIN)).json()["runs"][0]["id"]
    assert (await client.post(f"/api/v1/agent/runs/{run_id}/start", headers=h2)).status_code == 404


async def test_install_package_flow(client):
    agent_id, agent = await register_agent(client)
    other_id, other = await register_agent(client, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "PC-002")
    ops = await dispatcher(client)
    content = b"MZ fake installer" * 100

    r = await client.post("/api/v1/admin/packages", headers=ops,
                          data={"name": "WPS Office", "version": "12.1"},
                          files={"file": ("wps.exe", content, "application/octet-stream")})
    assert r.status_code == 400 and "静默安装参数" in r.json()["detail"]

    r = await client.post("/api/v1/admin/packages", headers=ops,
                          data={"name": "WPS Office", "version": "12.1", "silent_args": "/S"},
                          files={"file": ("wps.exe", content, "application/octet-stream")})
    assert r.status_code == 201, r.text
    pkg = r.json()
    assert pkg["sha256"] == hashlib.sha256(content).hexdigest()

    job = (await client.post("/api/v1/admin/jobs", headers=ops,
                             json={"kind": "install", "params": {"package_id": pkg["id"]}, "agent_ids": [agent_id]})).json()
    assert job["title"] == "安装软件：WPS Office 12.1"
    run = (await client.post("/api/v1/agent/poll", headers=agent, json={})).json()["runs"][0]
    assert run["params"]["sha256"] == pkg["sha256"] and run["params"]["silent_args"] == "/S"

    r = await client.get(f"/api/v1/agent/packages/{pkg['id']}/download", headers=agent)
    assert r.status_code == 200 and r.content == content
    # 没有这个任务的电脑下载不了
    assert (await client.get(f"/api/v1/agent/packages/{pkg['id']}/download", headers=other)).status_code == 403


async def test_disabled_agent_is_skipped(client):
    agent_id, agent = await register_agent(client)
    ops = await dispatcher(client)
    await client.patch(f"/api/v1/admin/agents/{agent_id}", headers=ops, json={"disabled": True})
    assert (await client.post("/api/v1/agent/poll", headers=agent, json={})).status_code == 403
    r = await client.post("/api/v1/admin/jobs", headers=ops, json={"kind": "clean", "agent_ids": [agent_id]})
    assert r.status_code == 400


async def test_heartbeat_links_device_to_agent(client, device_headers):
    await client.post("/api/v1/devices/heartbeat", headers=device_headers, json={"machine_guid": GUID.upper()})
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    assert devices[0]["machine_guid"] == GUID
