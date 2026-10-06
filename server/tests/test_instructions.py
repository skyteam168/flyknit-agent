"""远程指令：下发、员工端拉取/领取/回报、取消、模板、权限。"""

import pytest

from conftest import ADMIN

pytestmark = pytest.mark.asyncio


async def dispatcher(client, username="it.ops", can_dispatch=True):
    r = await client.post("/api/v1/admin/users", headers=ADMIN, json={
        "username": username, "password": "flyknit-2026", "can_dispatch": can_dispatch,
    })
    assert r.status_code == 201, r.text
    tok = (await client.post("/api/v1/admin/login", json={"username": username, "password": "flyknit-2026"})).json()["token"]
    await client.post("/api/v1/admin/password", headers={"Authorization": f"Bearer {tok}"},
                      json={"old_password": "flyknit-2026", "new_password": "flyknit-2027"})
    tok = (await client.post("/api/v1/admin/login", json={"username": username, "password": "flyknit-2027"})).json()["token"]
    return {"Authorization": f"Bearer {tok}"}


async def device_id_of(client):
    return (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()[0]["id"]


async def test_full_cycle(client, device_headers):
    ops = await dispatcher(client)
    did = await device_id_of(client)

    r = await client.post("/api/v1/admin/instructions", headers=ops,
                          json={"prompt": "把桌面上的 PDF 都整理到一个文件夹里", "device_ids": [did]})
    assert r.status_code == 201, r.text
    ins = r.json()
    assert ins["created_by"] == "it.ops" and ins["counts"] == {"pending": 1}
    assert ins["title"] == "把桌面上的 PDF 都整理到一个文件夹里"

    poll = (await client.get("/api/v1/client/instructions", headers=device_headers)).json()
    assert len(poll["instructions"]) == 1
    run = poll["instructions"][0]
    assert run["prompt"].startswith("把桌面")

    assert (await client.post(f"/api/v1/client/instructions/{run['run_id']}/start", headers=device_headers)).json()["ok"] is True
    # 已在执行的不会再被拉到
    assert (await client.get("/api/v1/client/instructions", headers=device_headers)).json()["instructions"] == []

    r = await client.post(f"/api/v1/client/instructions/{run['run_id']}/finish", headers=device_headers,
                          json={"status": "succeeded", "answer": "已整理 5 个 PDF 到「PDF」文件夹", "conversation_id": "abc123"})
    assert r.status_code == 204

    detail = (await client.get(f"/api/v1/admin/instructions/{ins['id']}", headers=ADMIN)).json()
    assert detail["counts"] == {"succeeded": 1}
    assert detail["runs"][0]["answer"].startswith("已整理")
    assert detail["runs"][0]["machine_name"] == "PC-001"
    assert detail["runs"][0]["conversation_id"] == "abc123"


async def test_custom_title(client, device_headers):
    ops = await dispatcher(client)
    did = await device_id_of(client)
    r = await client.post("/api/v1/admin/instructions", headers=ops,
                          json={"prompt": "随便做点什么", "title": "周末清理", "device_ids": [did]})
    assert r.json()["title"] == "周末清理"


async def test_dispatch_requires_permission(client, device_headers):
    did = await device_id_of(client)
    body = {"prompt": "做点事", "device_ids": [did]}
    # 共享令牌不行
    assert (await client.post("/api/v1/admin/instructions", headers=ADMIN, json=body)).status_code == 401
    # 没授权的账号不行
    plain = await dispatcher(client, "it.view", can_dispatch=False)
    r = await client.post("/api/v1/admin/instructions", headers=plain, json=body)
    assert r.status_code == 403


async def test_empty_prompt_rejected(client, device_headers):
    ops = await dispatcher(client)
    did = await device_id_of(client)
    r = await client.post("/api/v1/admin/instructions", headers=ops, json={"prompt": "", "device_ids": [did]})
    assert r.status_code == 422


async def test_cancel_only_pending(client, device_headers):
    ops = await dispatcher(client)
    did = await device_id_of(client)
    ins = (await client.post("/api/v1/admin/instructions", headers=ops,
                             json={"prompt": "任务", "device_ids": [did]})).json()
    run = (await client.get("/api/v1/client/instructions", headers=device_headers)).json()["instructions"][0]
    await client.post(f"/api/v1/client/instructions/{run['run_id']}/start", headers=device_headers)

    r = await client.post(f"/api/v1/admin/instructions/{ins['id']}/cancel", headers=ops)
    # 已经在跑的不取消
    assert r.json()["counts"] == {"running": 1}
    assert r.json()["cancelled_by"] == "it.ops"


async def test_cancelled_run_cannot_start(client, device_headers):
    ops = await dispatcher(client)
    did = await device_id_of(client)
    ins = (await client.post("/api/v1/admin/instructions", headers=ops,
                             json={"prompt": "任务", "device_ids": [did]})).json()
    run = (await client.get("/api/v1/client/instructions", headers=device_headers)).json()["instructions"][0]
    await client.post(f"/api/v1/admin/instructions/{ins['id']}/cancel", headers=ops)
    r = await client.post(f"/api/v1/client/instructions/{run['run_id']}/start", headers=device_headers)
    assert r.json() == {"ok": False, "status": "cancelled"}


async def test_device_cannot_touch_other_runs(client, device_headers):
    ops = await dispatcher(client)
    did = await device_id_of(client)
    ins = (await client.post("/api/v1/admin/instructions", headers=ops,
                             json={"prompt": "任务", "device_ids": [did]})).json()
    detail = (await client.get(f"/api/v1/admin/instructions/{ins['id']}", headers=ADMIN)).json()
    run_id = detail["runs"][0]["id"]
    # 另一台设备
    other = (await client.post("/api/v1/devices/register",
                               json={"enrollment_key": "test-enroll", "machine_name": "PC-002", "user_name": "le"})).json()
    other_h = {"Authorization": f"Bearer {other['token']}"}
    assert (await client.post(f"/api/v1/client/instructions/{run_id}/start", headers=other_h)).status_code == 404


async def test_disabled_device_excluded(client, device_headers):
    ops = await dispatcher(client)
    did = await device_id_of(client)
    await client.patch(f"/api/v1/admin/devices/{did}", headers=ADMIN, json={"disabled": True})
    r = await client.post("/api/v1/admin/instructions", headers=ops, json={"prompt": "任务", "device_ids": [did]})
    assert r.status_code == 400


async def test_templates(client):
    ops = await dispatcher(client)
    r = await client.post("/api/v1/admin/instruction-templates", headers=ops,
                          json={"name": "清桌面", "prompt": "把桌面整理干净"})
    assert r.status_code == 201
    tpl = r.json()
    assert (await client.get("/api/v1/admin/instruction-templates", headers=ADMIN)).json()[0]["name"] == "清桌面"
    assert (await client.delete(f"/api/v1/admin/instruction-templates/{tpl['id']}", headers=ops)).status_code == 204
    assert (await client.get("/api/v1/admin/instruction-templates", headers=ADMIN)).json() == []

    # 共享令牌不能建模板
    assert (await client.post("/api/v1/admin/instruction-templates", headers=ADMIN,
                              json={"name": "x", "prompt": "y"})).status_code == 401
