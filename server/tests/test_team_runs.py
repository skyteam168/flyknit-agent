"""团队模式第 3 期（docs/team-mode.md 第 10、15 节）：队长专家、示例任务、任务结果统计、评价、团队经验、模板。

重点：
- 统计只存数字和评价，评价只能评自己电脑上的任务；
- 团队经验跟着团队的范围走（看不到团队就看不到经验），写入时服务端再查一遍密码 / 密钥，手机号打码；
- 经验只有写的人、团队创建人、IT 能删；
- 队长专家要在范围内、不能同时是成员。
"""

import pytest

from conftest import ADMIN
from test_teams import body, publish, setup, teams_on

pytestmark = pytest.mark.asyncio


async def make_team(client, headers, **extra) -> dict:
    r = await client.post("/api/v1/client/teams", headers=headers, json=body(**extra))
    assert r.status_code == 201, r.text
    return r.json()


def run(run_id="r1", **extra) -> dict:
    return {"run_id": run_id, "planned": 3, "completed": 2, "failed": 1, "stopped": 0, "reassigned": True,
            "tokens": 420000, "duration_seconds": 300, "status": "finished", **extra}


async def test_leader_expert_and_examples(client):
    (_, a), (_, b), _ = await setup(client)
    team = await make_team(client, a, leader_expert_id="eq", examples=["  查 10 月 延期 ", "查 10 月 延期", "出月报"])
    assert team["leader_expert_id"] == "eq" and team["leader_available"] is True
    assert team["examples"] == ["查 10 月 延期", "出月报"]                       # 去空白、去重

    r = await client.post("/api/v1/client/teams", headers=a, json=body(name="另一组", leader_expert_id="mes"))
    assert r.status_code == 400 and "队长不能同时是成员" in r.text
    r = await client.post("/api/v1/client/teams", headers=a, json=body(name="另一组", leader_expert_id="nobody"))
    assert r.status_code == 400
    r = await client.post("/api/v1/client/teams", headers=a, json=body(name="另一组", examples=["x"] * 6))
    assert r.status_code == 422                                                      # 最多 5 条

    # 复制时带上示例和（能用的）队长
    copied = (await client.post(f"/api/v1/client/teams/{team['id']}/copy", headers=b)).json()
    assert copied["leader_expert_id"] == "eq" and copied["examples"] == ["查 10 月 延期", "出月报"]


async def test_run_reports_and_ratings_feed_the_admin_stats(client):
    (_, a), (_, b), (_, q) = await setup(client)
    team = await make_team(client, a)
    tid = team["id"]
    assert (await client.post(f"/api/v1/client/teams/{tid}/runs", headers=a, json=run())).status_code == 204
    # 同一轮再报一次：按最后一次算，不重复计数
    assert (await client.post(f"/api/v1/client/teams/{tid}/runs", headers=a, json=run(completed=3, failed=0))).status_code == 204
    assert (await client.post(f"/api/v1/client/teams/{tid}/runs", headers=b, json=run("r2", reassigned=False))).status_code == 204
    # 别的部门看不到这个团队：不能往它身上记
    assert (await client.post(f"/api/v1/client/teams/{tid}/runs", headers=q, json=run("r3"))).status_code == 404

    r = await client.post(f"/api/v1/client/teams/{tid}/runs/r1/rating", headers=a,
                          json={"rating": 1, "feedback": "很有用，联系 13812345678 可以细聊"})
    assert r.status_code == 204
    assert (await client.post(f"/api/v1/client/teams/{tid}/runs/r2/rating", headers=b, json={"rating": -1})).status_code == 204
    # 只能评自己电脑上的任务
    assert (await client.post(f"/api/v1/client/teams/{tid}/runs/r1/rating", headers=b, json={"rating": -1})).status_code == 404

    stats = {s["team_id"]: s for s in (await client.get("/api/v1/admin/teams/stats", headers=ADMIN)).json()}
    s = stats[tid]
    assert s["runs"] == 2 and s["planned"] == 6 and s["completed"] == 5
    assert s["completion_rate"] == round(5 / 6, 3) and s["reassign_rate"] == 0.5
    assert s["up"] == 1 and s["down"] == 1 and s["avg_tokens"] == 420000

    runs = (await client.get(f"/api/v1/admin/teams/{tid}/runs", headers=ADMIN)).json()
    rated = next(x for x in runs if x["run_id"] == "r1")
    assert rated["rating"] == 1 and "13812345678" not in rated["feedback"] and "5678" in rated["feedback"]
    assert rated["department"] == "生产部"


async def test_run_reports_need_team_mode(client):
    (_, a), _, _ = await setup(client)
    team = await make_team(client, a)
    await teams_on(client, teams=False)
    r = await client.post(f"/api/v1/client/teams/{team['id']}/runs", headers=a, json=run())
    assert r.status_code == 403


async def test_team_notes_follow_the_team_scope_and_reject_secrets(client):
    (_, a), (_, b), (_, q) = await setup(client)
    team = await make_team(client, a)
    tid = team["id"]
    r = await client.post(f"/api/v1/client/teams/{tid}/notes", headers=b,
                          json={"text": "MES 的延期按计划完工日算，不按交期算", "kind": "source", "source": "leader"})
    assert r.status_code == 201, r.text
    note = r.json()
    assert note["source"] == "leader" and note["deletable"] is True

    # 密码、密钥：整条拒收（员工端查过一遍，服务端再查）
    r = await client.post(f"/api/v1/client/teams/{tid}/notes", headers=b, json={"text": "MES 账号 admin 密码是 Abc12345"})
    assert r.status_code == 400 and r.json()["detail"]["code"] == "sensitive"
    # 讲做法的句子照常记
    r = await client.post(f"/api/v1/client/teams/{tid}/notes", headers=a, json={"text": "登录 MES 要先选工厂，再查工单"})
    assert r.status_code == 201

    # 同部门都看得到；别的部门看不到团队，也看不到经验
    assert len((await client.get(f"/api/v1/client/teams/{tid}/notes", headers=b)).json()) == 2
    assert (await client.get(f"/api/v1/client/teams/{tid}/notes", headers=q)).status_code == 404

    # 删：写的人能删；团队创建人能删别人写的；同部门的其他人不能删别人写的
    own = (await client.get(f"/api/v1/client/teams/{tid}/notes", headers=b)).json()
    by_a = next(n for n in own if n["text"].startswith("登录"))
    assert by_a["deletable"] is False
    assert (await client.delete(f"/api/v1/client/teams/{tid}/notes/{by_a['id']}", headers=b)).status_code == 403
    assert (await client.delete(f"/api/v1/client/teams/{tid}/notes/{note['id']}", headers=a)).status_code == 204

    # IT：看所有、加、删
    r = await client.post(f"/api/v1/admin/teams/{tid}/notes", headers=ADMIN, json={"text": "月底最后一天的数据第二天上午才全"})
    assert r.status_code == 201 and r.json()["source"] == "it"
    notes = (await client.get(f"/api/v1/admin/teams/{tid}/notes", headers=ADMIN)).json()
    assert len(notes) == 2
    assert (await client.delete(f"/api/v1/admin/teams/{tid}/notes/{notes[0]['id']}", headers=ADMIN)).status_code == 204


async def test_templates_and_it_teams_with_leader(client):
    await setup(client)
    templates = (await client.get("/api/v1/admin/teams/templates", headers=ADMIN)).json()
    assert {t["key"] for t in templates} == {"delivery_delay", "monthly_report", "complaint_trace"}
    assert all(t["roles"] and t["examples"] for t in templates)
    t = templates[0]
    r = await client.post("/api/v1/admin/teams", headers=ADMIN, json={
        "name": t["name"], "description": t["description"], "icon": t["icon"], "leader_note": t["leader_note"],
        "members": [{"expert_id": "mes"}, {"expert_id": "qc"}], "examples": t["examples"], "leader_expert_id": "eq",
        "disabled_tools": t["disabled_tools"],
    })
    assert r.status_code == 201, r.text
    assert r.json()["examples"] == t["examples"] and r.json()["leader_expert_id"] == "eq"
    assert (await client.get("/api/v1/admin/teams/templates")).status_code in (401, 403)


async def test_devices_cannot_read_admin_stats(client, device_headers):
    assert (await client.get("/api/v1/admin/teams/stats", headers=device_headers)).status_code in (401, 403)


async def test_unpublished_leader_is_reported_unavailable(client):
    (_, a), _, _ = await setup(client)
    await publish(client, "lead", "部门组长")
    team = await make_team(client, a, leader_expert_id="lead")
    assert (await client.post("/api/v1/admin/experts/lead/offline", headers=ADMIN)).status_code == 200
    listed = (await client.get("/api/v1/client/teams", headers=a)).json()
    mine = next(t for t in listed if t["id"] == team["id"])
    assert mine["leader_expert_id"] == "lead" and mine["leader_available"] is False
