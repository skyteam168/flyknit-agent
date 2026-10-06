"""安全中心的接口：后台统一配置 + 给某台机器单独放开。

纯逻辑的用例在 test_security_rules.py。
"""

import pytest

from app.services import security_settings as ss
from conftest import ADMIN

pytestmark = pytest.mark.asyncio


# ---------- 接口 ----------

async def test_client_config_carries_the_settings(client, device_headers):
    config = (await client.get("/api/v1/client/config", headers=device_headers)).json()

    assert config["security"]["sandbox"]["locked"] is True
    assert config["security"]["auto_backup"]["locked"] is False
    assert config["security"]["auto_backup"]["value"] is True


async def test_factory_wide_change_reaches_every_machine(client, device_headers):
    r = await client.put("/api/v1/admin/security", headers=ADMIN,
                         json={"values": {"backup_quota_mb": 2048}, "locks": {}})
    assert r.status_code == 200

    config = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert config["security"]["backup_quota_mb"]["value"] == 2048


async def test_one_machine_can_be_granted_more(client, device_headers):
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    device_id = devices[0]["id"]

    r = await client.put(f"/api/v1/admin/devices/{device_id}/policy", headers=ADMIN, json={
        "locks": {"system_tools": False},
        "overrides": {"system_tools": True},
        "note": "工模组需要查机台服务状态",
    })
    assert r.status_code == 200
    assert r.json()["effective"]["system_tools"]["locked"] is False

    config = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert config["security"]["system_tools"]["locked"] is False
    assert config["security"]["system_tools"]["value"] is True
    # 别的项不受影响，还是锁着
    assert config["security"]["sandbox"]["locked"] is True


async def test_clearing_a_machines_policy_returns_it_to_the_default(client, device_headers):
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    device_id = devices[0]["id"]
    await client.put(f"/api/v1/admin/devices/{device_id}/policy", headers=ADMIN,
                     json={"locks": {"sandbox": False}})

    r = await client.delete(f"/api/v1/admin/devices/{device_id}/policy", headers=ADMIN)
    assert r.status_code == 204

    config = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert config["security"]["sandbox"]["locked"] is True


async def test_a_machine_only_stores_what_differs(client, device_headers):
    """单独设置只存差异，所以改全厂默认时没单独设过的机器会跟着变。"""
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    device_id = devices[0]["id"]
    await client.put(f"/api/v1/admin/devices/{device_id}/policy", headers=ADMIN,
                     json={"locks": {"system_tools": False}})

    saved = (await client.get(f"/api/v1/admin/devices/{device_id}/policy", headers=ADMIN)).json()
    assert saved["locks"] == {"system_tools": False}
    assert saved["overrides"] == {}


async def test_garbage_from_the_admin_api_is_rejected_not_stored(client, device_headers):
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    r = await client.put(f"/api/v1/admin/devices/{devices[0]['id']}/policy", headers=ADMIN, json={
        "overrides": {"backup_quota_mb": 999999, "nonsense": True},
        "locks": {"also_nonsense": True},
    })
    assert r.status_code == 200
    assert r.json()["overrides"] == {"backup_quota_mb": 20480}
    assert r.json()["locks"] == {}


async def test_policy_for_an_unknown_device_is_404(client):
    assert (await client.get("/api/v1/admin/devices/9999/policy", headers=ADMIN)).status_code == 404


async def test_catalog_lists_every_configurable_item(client):
    catalog = (await client.get("/api/v1/admin/security/catalog", headers=ADMIN)).json()

    assert {c["key"] for c in catalog} == {s.key for s in ss.SETTINGS}
    assert all("risk" in c and "locked_by_default" in c for c in catalog)


async def test_security_endpoints_need_the_admin_token(client):
    assert (await client.get("/api/v1/admin/security")).status_code == 401
    assert (await client.put("/api/v1/admin/security", json={})).status_code == 401
