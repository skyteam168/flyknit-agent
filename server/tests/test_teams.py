"""团队（docs/team-mode.md 第 4、5、8.1、9 节）。

重点是范围由服务端执行：
- IT 团队按使用部门 / 电脑下发；部门团队只有同部门看得到；个人团队只有建的人看得到；
- 成员专家也要在使用者的范围内：建、改时不在范围内的拒收，列出时标成不可用；
- 只有建的人能改、解散；部门由服务端登记的为准，不信客户端；
- 团队模式开关默认关，关着时列表为空、新建被拒。
"""

import pytest

from conftest import ADMIN
from test_departments import assign, department, register, scope

pytestmark = pytest.mark.asyncio


def expert(eid: str, name: str) -> dict:
    return {"id": eid, "name": name, "instructions": f"你是{name}。", "description": f"负责{name}"}


async def publish(client, eid: str, name: str):
    assert (await client.post("/api/v1/admin/experts", headers=ADMIN, json=expert(eid, name))).status_code == 201
    assert (await client.post(f"/api/v1/admin/experts/{eid}/publish", headers=ADMIN, json={})).status_code == 200


async def teams_on(client, **extra):
    values = {"teams": True, **extra}
    r = await client.put("/api/v1/admin/security", headers=ADMIN, json={"values": values, "locks": {}})
    assert r.status_code == 200, r.text


async def setup(client):
    """两个部门、三台电脑（生产部两台、质量部一台），三位全公司可用的专家。"""
    await department(client, "生产部")
    await department(client, "质量部")
    a_id, a = await register(client, "PC-A")
    b_id, b = await register(client, "PC-B")
    q_id, q = await register(client, "PC-Q")
    await assign(client, a_id, "生产部")
    await assign(client, b_id, "生产部")
    await assign(client, q_id, "质量部")
    for eid, name in (("mes", "MES 专家"), ("qc", "质检专家"), ("eq", "设备专家")):
        await publish(client, eid, name)
    await teams_on(client)
    return (a_id, a), (b_id, b), (q_id, q)


def body(name="交付分析组", members=("mes", "qc"), share="department", **extra) -> dict:
    return {"name": name, "description": "查订单延期", "members": [{"expert_id": m, "note": ""} for m in members],
            "share": share, **extra}


def names(r) -> list[str]:
    assert r.status_code == 200, r.text
    return [t["name"] for t in r.json()]


async def test_teams_are_off_by_default(client, device_headers):
    sec = (await client.get("/api/v1/client/config", headers=device_headers)).json()["security"]
    assert sec["teams"]["value"] is False and sec["teams"]["locked"] is True
    assert sec["team_create"]["value"] is True
    assert sec["team_max_members"]["value"] == 8
    assert sec["team_parallel"]["value"] == 3
    assert sec["team_token_budget"]["value"] == 100
    assert (await client.get("/api/v1/client/teams", headers=device_headers)).json() == []
    r = await client.post("/api/v1/client/teams", headers=device_headers, json=body(share="personal"))
    assert r.status_code == 403
    assert r.json()["detail"]["code"] == "teams_disabled"


async def test_department_teams_are_shared_only_within_the_department(client):
    (_, a), (_, b), (_, q) = await setup(client)
    r = await client.post("/api/v1/client/teams", headers=a, json=body())
    assert r.status_code == 201, r.text
    team = r.json()
    assert team["source"] == "department" and team["department"] == "生产部" and team["editable"] is True
    assert names(await client.get("/api/v1/client/teams", headers=b)) == ["交付分析组"]   # 同部门看得到
    assert names(await client.get("/api/v1/client/teams", headers=q)) == []               # 别的部门看不到
    # 同部门的同事能用但不能改、不能解散
    other = (await client.get("/api/v1/client/teams", headers=b)).json()[0]
    assert other["editable"] is False
    assert (await client.put(f"/api/v1/client/teams/{team['id']}", headers=b, json=body(name="改名"))).status_code == 403
    assert (await client.post(f"/api/v1/client/teams/{team['id']}/dissolve", headers=b)).status_code == 403
    # 别的部门连看都看不到
    assert (await client.post(f"/api/v1/client/teams/{team['id']}/dissolve", headers=q)).status_code == 404


async def test_personal_teams_are_only_for_their_owner(client):
    (_, a), (_, b), _ = await setup(client)
    r = await client.post("/api/v1/client/teams", headers=a, json=body(name="我的周报搭子", share="personal"))
    assert r.status_code == 201
    assert names(await client.get("/api/v1/client/teams", headers=a)) == ["我的周报搭子"]
    assert names(await client.get("/api/v1/client/teams", headers=b)) == []


async def test_a_computer_without_a_department_can_only_make_personal_teams(client, device_headers):
    await setup(client)
    r = await client.post("/api/v1/client/teams", headers=device_headers, json=body())
    assert r.status_code == 400 and "部门" in r.json()["detail"]
    assert (await client.post("/api/v1/client/teams", headers=device_headers, json=body(share="personal"))).status_code == 201


async def test_members_must_be_experts_the_creator_can_use(client):
    (_, a), _, (_, q) = await setup(client)
    await scope(client, "expert", "eq", departments=["质量部"])     # 设备专家只给质量部
    r = await client.post("/api/v1/client/teams", headers=a, json=body(members=("mes", "eq")))
    assert r.status_code == 400 and "设备专家" in r.json()["detail"]
    assert (await client.post("/api/v1/client/teams", headers=a, json=body(members=("mes", "nobody")))).status_code == 400
    # 重复、太少、太多
    assert (await client.post("/api/v1/client/teams", headers=a, json=body(members=("mes", "mes")))).status_code == 400
    assert (await client.post("/api/v1/client/teams", headers=a, json=body(members=("mes",)))).status_code == 422
    await teams_on(client, team_max_members=2)
    r = await client.post("/api/v1/client/teams", headers=a, json=body(members=("mes", "qc", "eq")))
    assert r.status_code == 400  # eq 不在范围内，或者超过上限，都拒收
    assert (await client.post("/api/v1/client/teams", headers=q, json=body(members=("mes", "qc", "eq")))).status_code == 400
    assert (await client.post("/api/v1/client/teams", headers=q, json=body(members=("qc", "eq")))).status_code == 201


async def test_members_that_become_unavailable_are_marked_not_dropped(client):
    """建好以后专家下线或范围收窄：团队还在，那位成员标成不可用（开工时跳过）。"""
    (_, a), _, _ = await setup(client)
    await client.post("/api/v1/client/teams", headers=a, json=body(members=("mes", "qc", "eq")))
    await scope(client, "expert", "eq", departments=["质量部"])
    members = (await client.get("/api/v1/client/teams", headers=a)).json()[0]["members"]
    assert [(m["expert_id"], m["available"]) for m in members] == [("mes", True), ("qc", True), ("eq", False)]


async def test_department_comes_from_the_server_not_the_client(client):
    (_, a), _, (_, q) = await setup(client)
    r = await client.post("/api/v1/client/teams", headers=a, json={**body(), "department": "质量部"})
    assert r.status_code == 201 and r.json()["department"] == "生产部"
    assert names(await client.get("/api/v1/client/teams", headers=q)) == []


async def test_names_are_unique_within_a_department_and_edits_detect_conflicts(client):
    (_, a), (_, b), (_, q) = await setup(client)
    team = (await client.post("/api/v1/client/teams", headers=a, json=body())).json()
    assert (await client.post("/api/v1/client/teams", headers=b, json=body())).status_code == 409   # 生产部重名
    assert (await client.post("/api/v1/client/teams", headers=q, json=body(members=("qc", "mes")))).status_code == 201  # 别的部门可以
    # 改的时候带上读到的 updated_at：别人先改过就拒绝
    ok = await client.put(f"/api/v1/client/teams/{team['id']}", headers=a, json=body(name="交付分析一组", updated_at=team["updated_at"]))
    assert ok.status_code == 200, ok.text
    stale = await client.put(f"/api/v1/client/teams/{team['id']}", headers=a, json=body(name="又改", updated_at=team["updated_at"]))
    assert stale.status_code == 409


async def test_dissolve_and_copy(client):
    (_, a), (_, b), _ = await setup(client)
    team = (await client.post("/api/v1/client/teams", headers=a, json=body())).json()
    copied = await client.post(f"/api/v1/client/teams/{team['id']}/copy", headers=b)
    assert copied.status_code == 201
    assert copied.json()["source"] == "personal" and copied.json()["name"] == "交付分析组（我的）"
    assert (await client.post(f"/api/v1/client/teams/{team['id']}/dissolve", headers=a)).status_code == 204
    assert names(await client.get("/api/v1/client/teams", headers=b)) == ["交付分析组（我的）"]   # 解散的看不到，复制的还在
    # 后台看得到已解散的，可以恢复
    rows = (await client.get("/api/v1/admin/teams", headers=ADMIN)).json()
    dissolved = next(t for t in rows if t["id"] == team["id"])
    assert dissolved["dissolved_at"] is not None and dissolved["dissolved_by"].startswith("device:")
    assert (await client.post(f"/api/v1/admin/teams/{team['id']}/restore", headers=ADMIN)).status_code == 200
    assert "交付分析组" in names(await client.get("/api/v1/client/teams", headers=b))


async def test_employees_cannot_create_teams_when_it_turned_that_off(client):
    (_, a), _, _ = await setup(client)
    await teams_on(client, team_create=False)
    r = await client.post("/api/v1/client/teams", headers=a, json=body())
    assert r.status_code == 403 and r.json()["detail"]["code"] == "team_create_disabled"


async def test_it_teams_follow_their_scope(client):
    (_, a), _, (_, q) = await setup(client)
    r = await client.post("/api/v1/admin/teams", headers=ADMIN, json={
        "name": "月报小组", "members": [{"expert_id": "mes"}, {"expert_id": "qc"}], "scope_departments": ["质量部"]})
    assert r.status_code == 201, r.text
    tid = r.json()["id"]
    assert names(await client.get("/api/v1/client/teams", headers=q)) == ["月报小组"]
    assert names(await client.get("/api/v1/client/teams", headers=a)) == []
    it_team = (await client.get("/api/v1/client/teams", headers=q)).json()[0]
    assert it_team["source"] == "it" and it_team["editable"] is False
    # 员工改不了 IT 的团队；后台改不了员工的团队（只能看、解散、恢复）
    assert (await client.put(f"/api/v1/client/teams/{tid}", headers=q, json=body())).status_code == 403
    mine = (await client.post("/api/v1/client/teams", headers=a, json=body())).json()
    r = await client.put(f"/api/v1/admin/teams/{mine['id']}", headers=ADMIN, json={"name": "x", "members": [{"expert_id": "mes"}, {"expert_id": "qc"}]})
    assert r.status_code == 400
    # 范围清空 = 全公司
    r = await client.put(f"/api/v1/admin/teams/{tid}", headers=ADMIN, json={"name": "月报小组", "members": [{"expert_id": "mes"}, {"expert_id": "qc"}]})
    assert r.status_code == 200
    assert "月报小组" in names(await client.get("/api/v1/client/teams", headers=a))


async def test_devices_cannot_use_admin_endpoints(client):
    (_, a), _, _ = await setup(client)
    assert (await client.get("/api/v1/admin/teams", headers=a)).status_code in (401, 403)
    assert (await client.post("/api/v1/admin/teams", headers=a, json={"name": "x", "members": []})).status_code in (401, 403, 422)


async def test_team_model_must_be_an_enabled_tool_model(client):
    (_, a), _, _ = await setup(client)
    r = await client.post("/api/v1/client/teams", headers=a, json=body(model_id=99999))
    assert r.status_code == 400 and "模型" in r.json()["detail"]


async def test_start_counts_runs(client):
    (_, a), (_, b), _ = await setup(client)
    team = (await client.post("/api/v1/client/teams", headers=a, json=body())).json()
    assert (await client.post(f"/api/v1/client/teams/{team['id']}/start", headers=b)).status_code == 204
    assert (await client.get("/api/v1/client/teams", headers=a)).json()[0]["runs"] == 1
