"""无人值守任务：时间表、派发到执行机、权限上限与授权核对、跳过未完成的、结果作为定时报告只发给指定的人。"""

from datetime import datetime, timedelta, timezone

import pytest
from sqlalchemy import select

from app.db import get_sessionmaker
from app.models import ChannelMessage, Notice, UnattendedJob
from app.services import unattended_service as svc
from conftest import ADMIN
from test_instructions import device_id_of, dispatcher

# 2026-11-24 是周二；+07:00 的 08:30
NOW = datetime(2026, 11, 24, 1, 30, tzinfo=timezone.utc)


@pytest.mark.parametrize("schedule,expected_local", [
    ({"kind": "daily", "time": "08:00"}, "2026-11-25 08:00"),
    ({"kind": "daily", "time": "09:00"}, "2026-11-24 09:00"),
    ({"kind": "weekdays", "time": "08:00"}, "2026-11-25 08:00"),
    ({"kind": "weekly", "time": "09:00", "days": [1, 5]}, "2026-11-27 09:00"),
    ({"kind": "monthly", "time": "07:00", "day": 1}, "2026-12-01 07:00"),
    ({"kind": "hourly", "time": "08:00", "every": 5}, "2026-11-24 13:00"),
    ({"kind": "hourly", "time": "08:00", "every": 12}, "2026-11-24 20:00"),
])
def test_next_run_follows_the_schedule_in_local_time(schedule, expected_local):
    got = svc.next_after(schedule, 420, NOW).astimezone(timezone(timedelta(hours=7)))
    assert got.strftime("%Y-%m-%d %H:%M") == expected_local


def test_weekdays_skip_the_weekend_and_hourly_realigns_each_day():
    friday_evening = datetime(2026, 11, 27, 12, 0, tzinfo=timezone.utc)  # 周五 19:00 +07
    nxt = svc.next_after({"kind": "weekdays", "time": "08:00"}, 420, friday_evening).astimezone(timezone(timedelta(hours=7)))
    assert nxt.strftime("%a %H:%M") == "Mon 08:00"
    # 08:00 起每 5 小时：08 13 18 23、次日 04，然后在次日 08:00 重新对齐（不是接着 09 点）
    hourly = {"kind": "hourly", "time": "08:00", "every": 5}
    late = datetime(2026, 11, 24, 16, 30, tzinfo=timezone.utc)  # 23:30 +07
    assert svc.next_after(hourly, 420, late).astimezone(timezone(timedelta(hours=7))).strftime("%d %H:%M") == "25 04:00"
    early = datetime(2026, 11, 24, 21, 30, tzinfo=timezone.utc)  # 次日 04:30 +07
    assert svc.next_after(hourly, 420, early).astimezone(timezone(timedelta(hours=7))).strftime("%d %H:%M") == "25 08:00"


@pytest.mark.parametrize("bad", [
    {"kind": "yearly"}, {"kind": "daily", "time": "25:00"}, {"kind": "weekly", "time": "08:00", "days": []},
    {"kind": "monthly", "time": "08:00", "day": 31}, {"kind": "hourly", "time": "08:00", "every": 24},
])
def test_bad_schedules_are_rejected(bad):
    with pytest.raises(ValueError):
        svc.normalize_schedule(bad)


def test_markdown_report_becomes_safe_html():
    md = "# 上周周报\n\n**结论**：达成率 101%。\n\n| 车间 | 产量 |\n| --- | ---: |\n| 一车间 | 12480 |\n\n- 关注二车间\n- <script>alert(1)</script>\n"
    out = svc.markdown_to_html(md)
    assert "<h2>上周周报</h2>" in out
    assert "<strong>结论</strong>" in out
    assert "<table><thead><tr><th>车间</th><th>产量</th></tr></thead><tbody><tr><td>一车间</td><td>12480</td></tr></tbody></table>" in out
    assert "<ul><li>关注二车间</li>" in out
    assert "<script>" not in out and "&lt;script&gt;" in out


async def _job(client, who, **extra):
    body = {"name": "上周生产周报", "prompt": "查上周各车间产量，用表格列出", "device_id": await device_id_of(client),
            "schedule": {"kind": "daily", "time": "08:00"}, **extra}
    r = await client.post("/api/v1/admin/unattended", headers=who, json=body)
    assert r.status_code == 201, r.text
    return r.json()


async def test_only_dispatchers_create_jobs_and_permission_is_capped(client, device_headers):
    did = await device_id_of(client)
    body = {"name": "x", "prompt": "y", "device_id": did}
    # 共享令牌、没有下发权限的账号都不行
    assert (await client.post("/api/v1/admin/unattended", headers=ADMIN, json=body)).status_code in (401, 403)
    viewer = await dispatcher(client, "it.view", can_dispatch=False)
    assert (await client.post("/api/v1/admin/unattended", headers=viewer, json=body)).status_code == 403
    ops = await dispatcher(client)
    # 没人在场：不给完全权限；时间表不对返回中文原因
    assert (await client.post("/api/v1/admin/unattended", headers=ops, json={**body, "permission": "full"})).status_code == 422
    bad = await client.post("/api/v1/admin/unattended", headers=ops, json={**body, "schedule": {"kind": "daily", "time": "8点"}})
    assert bad.status_code == 400 and "HH:MM" in bad.json()["detail"]

    job = await _job(client, ops)
    assert job["permission"] == "readonly" and job["rehearsal"] is True  # 默认最保守
    assert job["schedule_text"] == "每天 08:00" and job["next_run_at"]
    listed = (await client.get("/api/v1/admin/unattended", headers=ADMIN)).json()
    assert listed["enabled"] is False and listed["items"][0]["name"] == "上周生产周报"
    # 总开关只有超级管理员（或部署用的共享令牌）能动
    assert (await client.put("/api/v1/admin/unattended/switch", headers=ops, json={"enabled": True})).status_code == 403


async def test_due_jobs_go_to_the_runner_and_results_reach_only_the_recipients(client, device_headers):
    ops = await dispatcher(client)
    runner = await device_id_of(client)
    other = (await client.post("/api/v1/devices/register",
                               json={"enrollment_key": "test-enroll", "machine_name": "PC-QC-01", "user_name": "le"})).json()
    other_headers = {"Authorization": f"Bearer {other['token']}"}
    job = await _job(client, ops, notify_devices=[runner])

    async with get_sessionmaker()() as s:
        row = await s.get(UnattendedJob, job["id"])
        row.next_run_at = datetime.now(timezone.utc) - timedelta(minutes=1)
        await s.commit()
        # 总开关关着：到点不派，时间顺延到以后
        assert await svc.tick(s) == []
        row = await s.get(UnattendedJob, job["id"])
        assert row.next_run_at.replace(tzinfo=timezone.utc) > datetime.now(timezone.utc)

    assert (await client.post(f"/api/v1/admin/unattended/{job['id']}/run", headers=ops)).status_code == 409  # 开关关着
    assert (await client.put("/api/v1/admin/unattended/switch", headers=ADMIN, json={"enabled": True})).status_code == 200

    async with get_sessionmaker()() as s:
        row = await s.get(UnattendedJob, job["id"])
        row.next_run_at = datetime.now(timezone.utc) - timedelta(minutes=1)
        await s.commit()
        sent = await svc.tick(s)
        assert len(sent) == 1
        # 上一次还没做完：这一次跳过，不堆积
        row = await s.get(UnattendedJob, job["id"])
        row.next_run_at = datetime.now(timezone.utc) - timedelta(minutes=1)
        await s.commit()
        assert await svc.tick(s) == []
        assert (await s.get(UnattendedJob, job["id"])).skipped_at is not None

    got = (await client.get("/api/v1/client/channels/messages", headers=device_headers)).json()["messages"]
    assert len(got) == 1
    task = got[0]
    assert task["channel"] == "unattended" and task["permission"] == "readonly" and task["rehearsal"] is True
    assert task["text"].startswith("查上周各车间产量") and "无人值守" in task["text"]
    assert (await client.get("/api/v1/client/channels/messages", headers=other_headers)).json()["messages"] == []

    # 授权核对：别的电脑、超出上限的都拒收；按上限来的收下
    mid = task["id"]
    wrong = await client.post("/api/v1/client/grants", headers=other_headers, json={
        "id": "g-unatt-00001", "origin": "unattended", "reference": str(mid), "permission": "readonly"})
    assert wrong.status_code == 400
    over = await client.post("/api/v1/client/grants", headers=device_headers, json={
        "id": "g-unatt-00002", "origin": "unattended", "reference": str(mid), "permission": "workspace"})
    assert over.status_code == 400
    ok = await client.post("/api/v1/client/grants", headers=device_headers, json={
        "id": "g-unatt-00003", "origin": "unattended", "reference": str(mid), "permission": "readonly"})
    assert ok.status_code == 201 and ok.json()["verified"] is True and ok.json()["requested_by"] == "无人值守:上周生产周报"

    # 回报结果 → 定时报告只发给收件人
    answer = "达成率 **101%**。\n\n| 车间 | 产量 |\n| --- | --- |\n| 一车间 | 12480 |"
    r = await client.post(f"/api/v1/client/channels/messages/{mid}/finish", headers=device_headers,
                          json={"status": "succeeded", "answer": answer, "conversation_id": "conv-u1"})
    assert r.json() == {"ok": True}
    mine = (await client.get("/api/v1/client/notices", headers=device_headers)).json()["items"]
    report = [n for n in mine if n["kind"] == "report"]
    assert len(report) == 1 and report[0]["title"].startswith("【预演】上周生产周报")
    detail = (await client.get(f"/api/v1/client/notices/{report[0]['id']}", headers=device_headers)).json()
    assert "<table>" in detail["body_html"] and "<strong>101%</strong>" in detail["body_html"]
    theirs = (await client.get("/api/v1/client/notices", headers=other_headers)).json()["items"]
    assert not [n for n in theirs if n["kind"] == "report"]

    runs = (await client.get("/api/v1/admin/unattended", headers=ADMIN)).json()["runs"]
    assert runs[0]["status"] == "succeeded" and runs[0]["delivered"] is True


async def test_no_recipients_means_no_notice_not_everyone(client, device_headers):
    ops = await dispatcher(client)
    job = await _job(client, ops)
    await client.put("/api/v1/admin/unattended/switch", headers=ADMIN, json={"enabled": True})
    mid = (await client.post(f"/api/v1/admin/unattended/{job['id']}/run", headers=ops)).json()["message_id"]
    await client.get("/api/v1/client/channels/messages", headers=device_headers)
    await client.post(f"/api/v1/client/channels/messages/{mid}/finish", headers=device_headers,
                      json={"status": "failed", "error": "中台连接器没连上"})
    async with get_sessionmaker()() as s:
        assert (await s.scalars(select(Notice).where(Notice.kind == "report"))).all() == []
        assert (await s.get(ChannelMessage, mid)).replied is False


async def test_failed_run_reports_the_reason_as_important(client, device_headers):
    ops = await dispatcher(client)
    runner = await device_id_of(client)
    job = await _job(client, ops, notify_devices=[runner], rehearsal=False)
    await client.put("/api/v1/admin/unattended/switch", headers=ADMIN, json={"enabled": True})
    mid = (await client.post(f"/api/v1/admin/unattended/{job['id']}/run", headers=ops)).json()["message_id"]
    await client.get("/api/v1/client/channels/messages", headers=device_headers)
    await client.post(f"/api/v1/client/channels/messages/{mid}/finish", headers=device_headers,
                      json={"status": "failed", "error": "中台连接器没连上"})
    items = (await client.get("/api/v1/client/notices", headers=device_headers)).json()["items"]
    report = next(n for n in items if n["kind"] == "report")
    assert report["important"] is True and not report["title"].startswith("【预演】")
    assert "中台连接器没连上" in report["summary"]
