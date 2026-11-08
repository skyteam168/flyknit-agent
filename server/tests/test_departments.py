"""部门与隔离：部门清单、技能 / 专家 / 连接器的使用范围（服务端强制）、部门安全设置。

重点是隔离由服务端执行：范围外的电脑在列表里看不到，按名字直接下载技能包、使用专家、派遣专家也一律拒绝；
范围留空 = 全公司（升级前的数据行为不变）；删掉在用的部门不允许（会让范围变空 = 放大权限）。
"""
import io
import zipfile

import pytest
import sqlalchemy as sa

from app import db
from app.models import Device, SkillPackage
from app.services import config_events
from conftest import ADMIN
from test_privileges import ready

pytestmark = pytest.mark.asyncio

EXPERT = {"id": "attendance", "name": "考勤专家", "instructions": "你是考勤专家。", "description": "处理考勤问题"}
VENDOR = {
    "id": "attendance-mcp", "name": "考勤系统", "description": "查考勤", "transport": "http",
    "url": "https://hr.example/mcp", "auth": "none",
}


async def register(client, machine: str) -> tuple[int, dict]:
    r = await client.post("/api/v1/devices/register",
                          json={"enrollment_key": "test-enroll", "machine_name": machine, "user_name": machine.lower()})
    assert r.status_code == 200, r.text
    headers = {"Authorization": f"Bearer {r.json()['token']}"}
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    return next(d["id"] for d in devices if d["machine_name"] == machine), headers


async def department(client, name: str) -> dict:
    r = await client.post("/api/v1/admin/departments", headers=ADMIN, json={"name": name})
    assert r.status_code == 201, r.text
    return r.json()


async def assign(client, device_id: int, dept: str):
    r = await client.patch(f"/api/v1/admin/devices/{device_id}", headers=ADMIN, json={"department": dept})
    assert r.status_code == 200, r.text


async def scope(client, kind: str, item: str, departments=(), devices=()):
    r = await client.put(f"/api/v1/admin/scopes/{kind}/{item}", headers=ADMIN,
                         json={"departments": list(departments), "devices": list(devices)})
    assert r.status_code == 200, r.text
    return r.json()


async def add_skill(name: str):
    """技能库里放一个技能（元数据 + 磁盘上的包），和导入后的样子一样。"""
    from pathlib import Path

    from app.config import get_settings
    from app.services import skill_library

    folder = skill_library.storage_dir(Path(get_settings().data_dir))
    folder.mkdir(parents=True, exist_ok=True)
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr(f"{name}/SKILL.md", f"---\nname: {name}\ndescription: test\n---\n正文")
    (folder / f"{name}.zip").write_bytes(buf.getvalue())
    async with db.get_sessionmaker()() as s:
        s.add(SkillPackage(name=name, description="test"))
        await s.commit()


async def setup_two_departments(client):
    await department(client, "行政部")
    await department(client, "研发部")
    admin_id, admin_headers = await register(client, "PC-ADMIN")
    dev_id, dev_headers = await register(client, "PC-DEV")
    await assign(client, admin_id, "行政部")
    await assign(client, dev_id, "研发部")
    return (admin_id, admin_headers), (dev_id, dev_headers)


# ---------- 部门清单 ----------

async def test_department_list_imports_names_already_on_devices(client):
    device_id, _ = await register(client, "PC-OLD")
    # 升级前的部门是自由填写的，直接写进库（模拟老数据）
    async with db.get_sessionmaker()() as s:
        (await s.get(Device, device_id)).department = " 质检部 "
        await s.commit()
    names = [d["name"] for d in (await client.get("/api/v1/admin/departments", headers=ADMIN)).json()]
    assert "质检部" in names
    row = next(d for d in (await client.get("/api/v1/admin/departments", headers=ADMIN)).json() if d["name"] == "质检部")
    assert row["devices"] == 1


async def test_device_department_must_come_from_the_list(client):
    device_id, _ = await register(client, "PC-1")
    r = await client.patch(f"/api/v1/admin/devices/{device_id}", headers=ADMIN, json={"department": "行政"})
    assert r.status_code == 400 and "部门清单" in r.json()["detail"]

    await department(client, "行政部")
    before = config_events.current()
    # 大小写、首尾空格不同也认成同一个部门，存的是清单里的写法
    r = await client.patch(f"/api/v1/admin/devices/{device_id}", headers=ADMIN, json={"department": "  行政部 "})
    assert r.status_code == 200 and r.json()["department"] == "行政部"
    assert config_events.current() > before, "换部门要让员工端马上重新同步"
    # 清空部门照样可以
    assert (await client.patch(f"/api/v1/admin/devices/{device_id}", headers=ADMIN, json={"department": ""})).json()["department"] == ""


async def test_duplicate_department_is_rejected(client):
    await department(client, "行政部")
    r = await client.post("/api/v1/admin/departments", headers=ADMIN, json={"name": "行政部 "})
    assert r.status_code == 409


async def test_rename_cascades_to_devices_plugins_and_scopes(client):
    dept = await department(client, "行政")
    device_id, _ = await register(client, "PC-1")
    await assign(client, device_id, "行政")
    await add_skill("attendance-skill")
    await scope(client, "skill", "attendance-skill", departments=["行政"])

    r = await client.patch(f"/api/v1/admin/departments/{dept['id']}", headers=ADMIN, json={"name": "行政部"})
    assert r.status_code == 200 and r.json()["name"] == "行政部"
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    assert next(d for d in devices if d["id"] == device_id)["department"] == "行政部"
    scopes = (await client.get("/api/v1/admin/scopes", headers=ADMIN)).json()
    assert scopes["skill"]["attendance-skill"]["departments"] == ["行政部"]


async def test_a_department_in_use_cannot_be_deleted(client):
    """删掉范围里最后一个部门会让范围变空 = 全公司，等于悄悄放大了权限，所以不让删。"""
    dept = await department(client, "行政部")
    await add_skill("attendance-skill")
    await scope(client, "skill", "attendance-skill", departments=["行政部"])
    r = await client.delete(f"/api/v1/admin/departments/{dept['id']}", headers=ADMIN)
    assert r.status_code == 409 and "attendance-skill" in r.json()["detail"]

    await scope(client, "skill", "attendance-skill")
    assert (await client.delete(f"/api/v1/admin/departments/{dept['id']}", headers=ADMIN)).status_code == 204


# ---------- 使用范围：技能 ----------

async def test_skill_scope_is_enforced_on_list_and_download(client):
    (_, admin_headers), (_, dev_headers) = await setup_two_departments(client)
    await add_skill("attendance-skill")
    await add_skill("common-skill")
    await scope(client, "skill", "attendance-skill", departments=["行政部"])

    names = lambda r: {s["name"] for s in r.json()}  # noqa: E731
    assert names(await client.get("/api/v1/client/skills", headers=admin_headers)) == {"attendance-skill", "common-skill"}
    assert names(await client.get("/api/v1/client/skills", headers=dev_headers)) == {"common-skill"}

    assert (await client.get("/api/v1/client/skills/attendance-skill/download", headers=admin_headers)).status_code == 200
    # 知道名字也下不到：和不存在一样回 404
    assert (await client.get("/api/v1/client/skills/attendance-skill/download", headers=dev_headers)).status_code == 404


async def test_scope_can_name_single_computers(client):
    (_, _), (dev_id, dev_headers) = await setup_two_departments(client)
    await add_skill("attendance-skill")
    await scope(client, "skill", "attendance-skill", departments=["行政部"], devices=[dev_id])
    names = {s["name"] for s in (await client.get("/api/v1/client/skills", headers=dev_headers)).json()}
    assert "attendance-skill" in names


async def test_empty_scope_means_everyone(client):
    (_, admin_headers), (_, dev_headers) = await setup_two_departments(client)
    await add_skill("common-skill")
    nobody_id, nobody_headers = await register(client, "PC-NODEPT")
    for headers in (admin_headers, dev_headers, nobody_headers):
        assert [s["name"] for s in (await client.get("/api/v1/client/skills", headers=headers)).json()] == ["common-skill"]


async def test_scope_rejects_unknown_departments_and_devices(client):
    await add_skill("attendance-skill")
    r = await client.put("/api/v1/admin/scopes/skill/attendance-skill", headers=ADMIN, json={"departments": ["不存在的部门"]})
    assert r.status_code == 400
    r = await client.put("/api/v1/admin/scopes/skill/attendance-skill", headers=ADMIN, json={"devices": [999]})
    assert r.status_code == 400
    assert (await client.put("/api/v1/admin/scopes/skill/nope", headers=ADMIN, json={})).status_code == 404
    assert (await client.put("/api/v1/admin/scopes/other/x", headers=ADMIN, json={})).status_code == 422


async def test_scope_change_notifies_online_computers(client):
    await department(client, "行政部")
    await add_skill("attendance-skill")
    before = config_events.current()
    await scope(client, "skill", "attendance-skill", departments=["行政部"])
    assert config_events.current() > before


# ---------- 使用范围：专家、连接器 ----------

async def test_expert_scope_hides_it_and_blocks_use_and_delegation(client):
    (_, admin_headers), (_, dev_headers) = await setup_two_departments(client)
    assert (await client.post("/api/v1/admin/experts", headers=ADMIN, json={**EXPERT, "allow_delegate": True})).status_code == 201
    assert (await client.post(f"/api/v1/admin/experts/{EXPERT['id']}/publish", headers=ADMIN, json={})).status_code == 200
    assert (await client.put("/api/v1/admin/experts/delegation", headers=ADMIN, json={"enabled": True})).status_code == 200
    await scope(client, "expert", EXPERT["id"], departments=["行政部"])

    ids = lambda r: [e["id"] for e in r.json()]  # noqa: E731
    assert ids(await client.get("/api/v1/client/experts", headers=admin_headers)) == [EXPERT["id"]]
    assert ids(await client.get("/api/v1/client/experts", headers=dev_headers)) == []
    assert (await client.post(f"/api/v1/client/experts/{EXPERT['id']}/use", headers=dev_headers)).status_code == 404

    task = {"run_id": "r1", "conversation_id": "c1", "expert_id": EXPERT["id"], "expert_version": 1, "task": "查考勤"}
    r = await client.post("/api/v1/client/subtasks", headers=dev_headers, json=task)
    assert r.status_code == 403 and "expert_unavailable" in r.text, r.text
    r = await client.post("/api/v1/client/subtasks", headers=admin_headers, json=task)
    assert r.status_code == 201, r.text


async def test_connector_scope_keeps_shared_accounts_inside_the_department(client):
    (_, admin_headers), (_, dev_headers) = await setup_two_departments(client)
    assert (await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN, json=VENDOR)).status_code == 201
    await scope(client, "mcp", VENDOR["id"], departments=["行政部"])
    ids = lambda r: [v["id"] for v in r.json()]  # noqa: E731
    assert ids(await client.get("/api/v1/client/mcp/vendors", headers=admin_headers)) == [VENDOR["id"]]
    assert ids(await client.get("/api/v1/client/mcp/vendors", headers=dev_headers)) == []


async def test_access_view_matches_what_the_computer_gets(client):
    (admin_id, _), (dev_id, _) = await setup_two_departments(client)
    await add_skill("attendance-skill")
    await scope(client, "skill", "attendance-skill", departments=["行政部"])
    a = (await client.get(f"/api/v1/admin/devices/{admin_id}/access", headers=ADMIN)).json()
    d = (await client.get(f"/api/v1/admin/devices/{dev_id}/access", headers=ADMIN)).json()
    assert a["skills"] == ["attendance-skill"] and d["skills"] == []
    assert a["department"] == "行政部" and a["department_known"] is True


# ---------- 部门安全设置 ----------

async def test_department_policy_sits_between_global_and_device(client):
    (admin_id, admin_headers), (dev_id, dev_headers) = await setup_two_departments(client)
    depts = {d["name"]: d["id"] for d in (await client.get("/api/v1/admin/departments", headers=ADMIN)).json()}
    r = await client.put(f"/api/v1/admin/departments/{depts['研发部']}/policy", headers=ADMIN,
                         json={"overrides": {"developer_tools": True}, "locks": {}})
    assert r.status_code == 200, r.text
    assert r.json()["effective"]["developer_tools"]["value"] is True

    sec = lambda h: client.get("/api/v1/client/config", headers=h)  # noqa: E731
    assert (await sec(dev_headers)).json()["security"]["developer_tools"]["value"] is True
    assert (await sec(admin_headers)).json()["security"]["developer_tools"]["value"] is False

    # 这台电脑单独的设置优先于部门
    r = await client.put(f"/api/v1/admin/devices/{dev_id}/policy", headers=ADMIN,
                         json={"overrides": {"developer_tools": False}, "locks": {}})
    assert r.status_code == 200
    assert (await sec(dev_headers)).json()["security"]["developer_tools"]["value"] is False
    access = (await client.get(f"/api/v1/admin/devices/{dev_id}/access", headers=ADMIN)).json()
    assert access["security_sources"]["developer_tools"] == "device"

    # 删掉这台的单独设置，回到部门的值
    await client.delete(f"/api/v1/admin/devices/{dev_id}/policy", headers=ADMIN)
    access = (await client.get(f"/api/v1/admin/devices/{dev_id}/access", headers=ADMIN)).json()
    assert access["security"]["developer_tools"]["value"] is True
    assert access["security_sources"]["developer_tools"] == "department"


async def test_department_policy_follows_a_rename(client):
    (_, _), (_, dev_headers) = await setup_two_departments(client)
    depts = {d["name"]: d["id"] for d in (await client.get("/api/v1/admin/departments", headers=ADMIN)).json()}
    await client.put(f"/api/v1/admin/departments/{depts['研发部']}/policy", headers=ADMIN,
                     json={"overrides": {"developer_tools": True}})
    await client.patch(f"/api/v1/admin/departments/{depts['研发部']}", headers=ADMIN, json={"name": "研发中心"})
    sec = (await client.get("/api/v1/client/config", headers=dev_headers)).json()["security"]
    assert sec["developer_tools"]["value"] is True


async def test_only_owners_change_department_security(client):
    await ready(client, "owner")
    _, clerk = await ready(client, "clerk")
    dept = await department(client, "研发部")
    body = {"overrides": {"developer_tools": True}}
    assert (await client.put(f"/api/v1/admin/departments/{dept['id']}/policy", headers=clerk, json=body)).status_code == 403
    assert (await client.delete(f"/api/v1/admin/departments/{dept['id']}/policy", headers=clerk)).status_code == 403


async def test_department_delegation_switch_reaches_the_gateway_checks(client):
    """派遣开关按部门放开后，登记子任务时用的也是部门这一层（不是只看全厂）。"""
    (admin_id, admin_headers), _ = await setup_two_departments(client)
    depts = {d["name"]: d["id"] for d in (await client.get("/api/v1/admin/departments", headers=ADMIN)).json()}
    assert (await client.post("/api/v1/admin/experts", headers=ADMIN, json={**EXPERT, "allow_delegate": True})).status_code == 201
    assert (await client.post(f"/api/v1/admin/experts/{EXPERT['id']}/publish", headers=ADMIN, json={})).status_code == 200
    task = {"run_id": "r1", "conversation_id": "c1", "expert_id": EXPERT["id"], "expert_version": 1, "task": "查考勤"}
    # 全厂关着：拒绝
    assert (await client.post("/api/v1/client/subtasks", headers=admin_headers, json=task)).status_code == 403
    await client.put(f"/api/v1/admin/departments/{depts['行政部']}/policy", headers=ADMIN,
                     json={"overrides": {"expert_delegation": True}})
    assert (await client.post("/api/v1/client/subtasks", headers=admin_headers, json=task)).status_code == 201


async def test_old_rows_without_scope_columns_are_everyone(client):
    """升级上来的老行范围列是 NULL：按「全公司」处理，行为和升级前一样。"""
    _, headers = await register(client, "PC-1")
    await add_skill("legacy-skill")
    async with db.get_sessionmaker()() as s:
        await s.execute(sa.update(SkillPackage).values(scope_departments=None, scope_devices=None))
        await s.commit()
    assert [x["name"] for x in (await client.get("/api/v1/client/skills", headers=headers)).json()] == ["legacy-skill"]
