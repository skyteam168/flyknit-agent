"""运维代理自升级（docs/agent-self-update.md）：安全中心开关按全厂 → 部门 → 单台电脑生效、默认关；
失败回滚上报；代理版本变化记审计；后台能看出哪些代理版本旧。"""

import pytest

from conftest import ADMIN
from test_agents import GUID, register_agent
from test_client_package import nested_zip, released
from test_departments import assign, department
from test_releases import owner_headers

pytestmark = pytest.mark.asyncio


async def poll(client, agent, **body):
    r = await client.post("/api/v1/agent/poll", headers=agent, json=body)
    assert r.status_code == 200, r.text
    return r.json()


async def link_device(client, device_headers) -> int:
    """员工端心跳报同一个 machine_guid：代理和员工端就是同一台电脑。"""
    await client.post("/api/v1/devices/heartbeat", headers=device_headers, json={"machine_guid": GUID})
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    return devices[0]["id"]


async def test_off_by_default(client):
    _, agent = await register_agent(client)
    assert (await poll(client, agent))["self_update"] is False


async def test_factory_wide_switch(client):
    _, agent = await register_agent(client)
    r = await client.put("/api/v1/admin/security", headers=ADMIN, json={"values": {"agent_self_update": True}, "locks": {}})
    assert r.status_code == 200, r.text
    # 还没有员工端记录的电脑：按全厂
    assert (await poll(client, agent))["self_update"] is True


async def test_department_and_single_pc_override(client, device_headers):
    _, agent = await register_agent(client)
    device_id = await link_device(client, device_headers)

    dept = await department(client, "IT 部")
    await assign(client, device_id, "IT 部")
    r = await client.put(f"/api/v1/admin/departments/{dept['id']}/policy", headers=ADMIN,
                         json={"overrides": {"agent_self_update": True}, "locks": {}})
    assert r.status_code == 200, r.text
    assert (await poll(client, agent))["self_update"] is True

    # 这一台单独关掉：比部门优先
    r = await client.put(f"/api/v1/admin/devices/{device_id}/policy", headers=ADMIN,
                         json={"overrides": {"agent_self_update": False}, "locks": {}})
    assert r.status_code == 200, r.text
    assert (await poll(client, agent))["self_update"] is False

    # 别的电脑（不在这个部门）照旧是关的
    _, other = await register_agent(client, guid="aaaaaaaa-0000-0000-0000-000000000001", name="PC-002")
    assert (await poll(client, other))["self_update"] is False


async def test_version_change_is_audited_and_failure_reported(client):
    agent_id, agent = await register_agent(client)   # 注册时报 0.1.0
    await poll(client, agent, agent_version="0.1.0")
    await poll(client, agent, agent_version="0.3.0")

    logs = (await client.get("/api/v1/admin/audit", headers=ADMIN, params={"tool": "agent_version"})).json()
    items = logs["items"] if isinstance(logs, dict) else logs
    hit = [x for x in items if x.get("tool_name") == "agent_version"]
    assert hit and hit[0]["machine_name"] == "PC-001" and hit[0]["scene"] == "ops"
    # 没有查看聊天权限的账号也能看出升到了哪个版本（摘要按员工内容处理，对它们是空的）
    assert hit[0]["actor"] == "agent:0.3.0"

    # 自升级失败、回滚：后台看得到失败的版本；之后报空就清掉
    await poll(client, agent, agent_version="0.3.0", self_update_failed="0.4.0")
    row = next(a for a in (await client.get("/api/v1/admin/agents", headers=ADMIN)).json() if a["id"] == agent_id)
    assert row["self_update_failed"] == "0.4.0"
    await poll(client, agent, agent_version="0.4.1")
    row = next(a for a in (await client.get("/api/v1/admin/agents", headers=ADMIN)).json() if a["id"] == agent_id)
    assert row["self_update_failed"] == ""


async def test_outdated_agents_are_flagged(client):
    headers = await owner_headers(client)
    await released(client, headers, version="0.3.0", body=nested_zip("0.3.0"))
    _, old = await register_agent(client)
    await poll(client, old, agent_version="0.2.9")
    _, fresh = await register_agent(client, guid="aaaaaaaa-0000-0000-0000-000000000002", name="PC-NEW")
    await poll(client, fresh, agent_version="0.3.0")

    rows = {a["machine_name"]: a for a in (await client.get("/api/v1/admin/agents", headers=ADMIN)).json()}
    assert rows["PC-001"]["outdated"] is True
    assert rows["PC-NEW"]["outdated"] is False
