import pytest

from conftest import ADMIN, setup_models

pytestmark = pytest.mark.asyncio


async def test_admin_requires_token(client):
    assert (await client.get("/api/v1/admin/providers")).status_code == 401
    r = await client.get("/api/v1/admin/providers", headers={"Authorization": "Bearer wrong"})
    assert r.status_code == 403


async def test_provider_key_is_masked(client):
    r = await client.post("/api/v1/admin/providers", headers=ADMIN,
                          json={"name": "内部", "base_url": "http://10.0.0.1/v1/", "api_key": "sk-1234567890"})
    assert r.status_code == 201
    body = r.json()
    assert body["api_key_masked"] == "sk-****7890"
    assert body["base_url"] == "http://10.0.0.1/v1"
    assert "sk-1234567890" not in r.text


async def test_route_rejects_unknown_scene(client):
    r = await client.put("/api/v1/admin/routes/foo", headers=ADMIN, json={"model_id": None})
    assert r.status_code == 400


async def test_client_config_lists_scenes_and_policy(client, device_headers):
    await setup_models(client)
    r = await client.get("/api/v1/client/config", headers=device_headers)
    assert r.status_code == 200
    data = r.json()
    scenes = {s["scene"]: s for s in data["scenes"]}
    assert scenes["agent"]["available"] and scenes["agent"]["model_name"] == "Qwen3.5-397B"
    # translate 未单独配置时回退到 chat
    assert scenes["translate"]["available"]
    assert any("rm" in p for p in data["policy"]["blocked_patterns"])


async def test_policy_update_bumps_version_and_validates(client):
    policy = (await client.get("/api/v1/admin/policy", headers=ADMIN)).json()
    v = policy["version"]
    policy["blocked_patterns"].append(r"\bmy-danger\b")
    r = await client.put("/api/v1/admin/policy", headers=ADMIN, json=policy)
    assert r.status_code == 200 and r.json()["version"] == v + 1

    policy["blocked_patterns"].append("([unclosed")
    r = await client.put("/api/v1/admin/policy", headers=ADMIN, json=policy)
    assert r.status_code == 400


async def test_smb_password_never_returned(client):
    r = await client.put("/api/v1/admin/smb", headers=ADMIN,
                         json={"domain": "FACTORY", "username": "svc_install", "password": "P@ss",
                               "share_root": r"\\fileserver\software"})
    assert r.status_code == 200
    assert r.json()["password_set"] is True
    assert "P@ss" not in r.text
    # 不传密码时保持原密码
    r = await client.put("/api/v1/admin/smb", headers=ADMIN, json={"username": "svc_install2"})
    assert r.json()["password_set"] is True


async def test_audit_roundtrip(client, device_headers):
    r = await client.post("/api/v1/audit", headers=device_headers, json={"items": [
        {"tool_name": "run_shell", "arguments": "rm -rf C:\\", "risk": "blocked", "decision": "blocked"},
        {"tool_name": "write_file", "arguments": "D:\\a.txt", "risk": "confirm", "decision": "approved",
         "status": "ok"},
    ]})
    assert r.status_code == 204
    rows = (await client.get("/api/v1/admin/audit?decision=blocked", headers=ADMIN)).json()
    assert len(rows) == 1
    assert rows[0]["machine_name"] == "PC-001"
