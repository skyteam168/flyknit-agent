"""回归评测（docs/evals.md）：题集校验、谁能跑、派给执行机（只预演、带专家）、授权核对、检查项和模型打分、和上一次比。"""

from datetime import datetime, timedelta, timezone

import pytest

from app.db import get_sessionmaker
from app.models import ChannelMessage, Expert
from app.services import eval_service
from conftest import ADMIN
from test_instructions import device_id_of, dispatcher

CASES = [
    {"id": "yield", "prompt": "上周一车间的产量是多少？", "must_include": ["12480"], "must_not_include": ["不知道"]},
    {"id": "summary", "prompt": "用三句话总结上周生产情况", "rubric": "要提到产量、达成率、不良率三项，不能编造数字"},
    {"id": "format", "prompt": "列出各车间产量表", "regex": r"\|\s*车间\s*\|"},
]


@pytest.fixture
def fake_judge(monkeypatch):
    calls = []

    async def judge(session, case, answer):
        calls.append((case["id"], answer))
        return (85, "三项都提到了") if "不良率" in answer else (40, "漏了不良率")

    monkeypatch.setattr(eval_service, "judge", judge)
    return calls


async def _suite(client, who, **extra):
    body = {"name": "MES 专家周报题", "device_id": await device_id_of(client), "cases": CASES, **extra}
    r = await client.post("/api/v1/admin/evals", headers=who, json=body)
    assert r.status_code == 201, r.text
    return r.json()


async def _answer(client, headers, answers: dict[str, tuple[str, str]]):
    """执行机领题并按题目回报：{问题开头: (status, answer)}。"""
    tasks = (await client.get("/api/v1/client/channels/messages", headers=headers)).json()["messages"]
    for t in tasks:
        status, answer = next(v for k, v in answers.items() if t["text"].startswith(k))
        r = await client.post(f"/api/v1/client/channels/messages/{t['id']}/finish", headers=headers,
                              json={"status": status, "answer": answer, "error": "" if status == "succeeded" else "连接器没连上"})
        assert r.json() == {"ok": True}
    await eval_service.wait_idle()
    return tasks


GOOD = {
    "上周一车间": ("succeeded", "一车间上周产量 12480 件。"),
    "用三句话": ("succeeded", "产量 3.1 万件；达成率 101%；不良率 1.2%。"),
    "列出各车间": ("succeeded", "| 车间 | 产量 |\n| --- | --- |\n| 一车间 | 12480 |"),
}


async def test_cases_need_a_way_to_judge_and_only_dispatchers_run(client, device_headers):
    await dispatcher(client, "it.boss")
    viewer = await dispatcher(client, "it.view", can_dispatch=False)
    ops = await dispatcher(client)
    body = {"name": "x", "cases": [{"prompt": "随便问问"}]}
    r = await client.post("/api/v1/admin/evals", headers=ops, json=body)
    assert r.status_code == 400 and "判分方式" in r.json()["detail"]
    bad_regex = {"name": "x", "cases": [{"prompt": "问", "regex": "("}]}
    assert (await client.post("/api/v1/admin/evals", headers=ops, json=bad_regex)).status_code == 400
    assert (await client.post("/api/v1/admin/evals", headers=ops, json={**body, "cases": CASES, "expert_id": "nobody"})).status_code == 400
    assert (await client.post("/api/v1/admin/evals", headers=viewer, json={**body, "cases": CASES})).status_code == 403
    assert (await client.post("/api/v1/admin/evals", headers=ADMIN, json={**body, "cases": CASES})).status_code in (401, 403)

    suite = await _suite(client, ops)
    assert [c["id"] for c in suite["cases"]] == ["yield", "summary", "format"]
    assert suite["permission"] == "readonly" and suite["last_run"] is None
    assert (await client.get("/api/v1/admin/evals", headers=viewer)).json()["items"][0]["name"] == "MES 专家周报题"
    assert (await client.post(f"/api/v1/admin/evals/{suite['id']}/run", headers=viewer, json={})).status_code == 403


async def test_run_dispatches_rehearsals_and_scores_against_the_last_run(client, device_headers, fake_judge):
    ops = await dispatcher(client)
    async with get_sessionmaker()() as s:
        s.add(Expert(id="mes_analyst", name="MES 生产分析专家"))
        await s.commit()
    suite = await _suite(client, ops, expert_id="mes_analyst")
    assert suite["expert_name"] == "MES 生产分析专家"

    run = (await client.post(f"/api/v1/admin/evals/{suite['id']}/run", headers=ops, json={"note": "专家 v1"})).json()
    assert run["status"] == "running" and run["total"] == 3
    # 上一次没跑完不能再跑
    assert (await client.post(f"/api/v1/admin/evals/{suite['id']}/run", headers=ops, json={})).status_code == 409

    tasks = (await client.get("/api/v1/client/channels/messages", headers=device_headers)).json()["messages"]
    assert len(tasks) == 3
    assert all(t["channel"] == "eval" and t["rehearsal"] is True and t["permission"] == "readonly" and t["expert_id"] == "mes_analyst" for t in tasks)
    # 授权核对：超出上限的拒收，按上限来的收下
    mid = tasks[0]["id"]
    over = await client.post("/api/v1/client/grants", headers=device_headers, json={
        "id": "g-eval-0001", "origin": "eval", "reference": str(mid), "permission": "workspace"})
    assert over.status_code == 400
    ok = await client.post("/api/v1/client/grants", headers=device_headers, json={
        "id": "g-eval-0002", "origin": "eval", "reference": str(mid), "permission": "readonly"})
    assert ok.status_code == 201 and ok.json()["verified"] is True and ok.json()["requested_by"] == "回归评测:MES 专家周报题"

    # 已经领走的再回报
    async with get_sessionmaker()() as s:
        for t in tasks:
            (await s.get(ChannelMessage, t["id"])).status = "pending"
        await s.commit()
    await _answer(client, device_headers, GOOD)
    detail = (await client.get(f"/api/v1/admin/evals/runs/{run['id']}", headers=ADMIN)).json()
    assert detail["status"] == "done" and detail["passed"] == 3 and detail["score"] == round((100 + 85 + 100) / 3)
    assert [r["case_id"] for r in detail["results"]] == ["yield", "summary", "format"]
    summary = detail["results"][1]
    assert summary["judge_score"] == 85 and summary["passed"] is True and "三项都提到了" in summary["judge_reason"]
    assert fake_judge == [("summary", GOOD["用三句话"][1])]

    # 第二次：两题变差 → 列为退步
    run2 = (await client.post(f"/api/v1/admin/evals/{suite['id']}/run", headers=ops, json={"note": "专家 v2"})).json()
    await _answer(client, device_headers, {
        "上周一车间": ("succeeded", "我不知道，大概 12480 吧"),
        "用三句话": ("succeeded", "产量 3.1 万件；达成率 101%。"),
        "列出各车间": ("failed", ""),
    })
    detail = (await client.get(f"/api/v1/admin/evals/runs/{run2['id']}", headers=ADMIN)).json()
    assert detail["passed"] == 0 and sorted(detail["regressions"]) == ["format", "summary", "yield"]
    checks = {r["case_id"]: r["checks"] for r in detail["results"]}
    assert {c["name"]: c["ok"] for c in checks["yield"]} == {"完成": True, "包含「12480」": True, "不包含「不知道」": False}
    assert checks["format"][0] == {"name": "完成", "ok": False, "detail": "连接器没连上"}
    listed = (await client.get("/api/v1/admin/evals", headers=ADMIN)).json()["items"][0]["last_run"]
    assert listed["id"] == run2["id"] and listed["status"] == "done"


async def test_expired_questions_count_as_failed_and_close_the_run(client, device_headers, fake_judge):
    ops = await dispatcher(client)
    suite = await _suite(client, ops)
    run = (await client.post(f"/api/v1/admin/evals/{suite['id']}/run", headers=ops, json={})).json()
    async with get_sessionmaker()() as s:
        msgs = (await s.execute(ChannelMessage.__table__.select().where(ChannelMessage.channel == "eval"))).all()
        for m in msgs:
            row = await s.get(ChannelMessage, m.id)
            row.status = "expired"
            row.finished_at = datetime.now(timezone.utc) - timedelta(minutes=1)
        await s.commit()
    detail = (await client.get(f"/api/v1/admin/evals/runs/{run['id']}", headers=ADMIN)).json()
    assert detail["status"] == "done" and detail["passed"] == 0 and detail["score"] == 0
    assert all("没开机" in r["error"] for r in detail["results"])
    assert fake_judge == []  # 没回答的不打分
