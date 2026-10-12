"""专家派遣：登记的授权和次数、状态只走一次、懒超时，网关的几道闸和用量累加，审计字段，后台接口。"""

import asyncio
import json
from datetime import datetime, timedelta, timezone

import httpx
import pytest
import respx
import sqlalchemy as sa

from app import db
from app.models import AuditLog, Subtask
from app.services import config_events, subtask_store
from conftest import ADMIN, setup_models
from test_privileges import ready

pytestmark = pytest.mark.asyncio

EXPERT = {
    "id": "mes_analyst",
    "name": "MES 生产分析专家",
    "instructions": "你是 MES 生产分析专家。",
    "description": "负责 MES 数据分析",
}
UPSTREAM = "http://primary.local/v1/chat/completions"
ASK_EXPERT = {"type": "function", "function": {"name": "ask_expert", "parameters": {"type": "object"}}}
READ_FILE = {"type": "function", "function": {"name": "read_file", "parameters": {"type": "object"}}}


async def enable(client, on=True):
    r = await client.put("/api/v1/admin/experts/delegation", headers=ADMIN, json={"enabled": on})
    assert r.status_code == 200, r.text
    assert r.json() == {"enabled": on}


async def publish(client, **fields):
    r = await client.post("/api/v1/admin/experts", headers=ADMIN, json={**EXPERT, **fields})
    assert r.status_code == 201, r.text
    r = await client.post(f"/api/v1/admin/experts/{EXPERT['id']}/publish", headers=ADMIN, json={})
    assert r.status_code == 200, r.text


async def create(client, headers, run_id="run-1", **extra):
    body = {"run_id": run_id, "conversation_id": "conv-1", "expert_id": EXPERT["id"], "expert_version": 1,
            "task": "查一下昨天 A 车间产量", **extra}
    return await client.post("/api/v1/client/subtasks", headers=headers, json=body)


async def ready_subtask(client, headers, **extra) -> dict:
    await enable(client)
    await publish(client)
    r = await create(client, headers, **extra)
    assert r.status_code == 201, r.text
    return r.json()


async def rows() -> list[Subtask]:
    async with db.get_sessionmaker()() as s:
        return list(await s.scalars(sa.select(Subtask).order_by(Subtask.seq)))


async def patch_row(subtask_id: str, **values) -> None:
    async with db.get_sessionmaker()() as s:
        await s.execute(sa.update(Subtask).where(Subtask.id == subtask_id).values(**values))
        await s.commit()


async def other_device(client) -> dict:
    r = await client.post("/api/v1/devices/register",
                          json={"enrollment_key": "test-enroll", "machine_name": "PC-002", "user_name": "tran"})
    return {"Authorization": f"Bearer {r.json()['token']}"}


# ---------- 登记 ----------
async def test_disabled_by_default_and_rejection_creates_nothing(client, device_headers):
    await publish(client)
    r = await client.get("/api/v1/admin/experts/delegation", headers=ADMIN)
    assert r.json() == {"enabled": False}
    r = await create(client, device_headers)
    assert r.status_code == 403
    assert r.json()["detail"]["code"] == "delegation_disabled"
    assert await rows() == []
    # 员工端拿到的安全设置里有这一项，默认关、锁住
    cfg = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert cfg["security"]["expert_delegation"]["value"] is False
    assert cfg["security"]["expert_delegation"]["locked"] is True


async def test_unpublished_or_not_delegatable_expert_is_unavailable(client, device_headers):
    await enable(client)
    r = await create(client, device_headers)  # 不存在
    assert r.status_code == 403 and r.json()["detail"]["code"] == "expert_unavailable"

    await client.post("/api/v1/admin/experts", headers=ADMIN, json=EXPERT)  # 只是草稿
    r = await create(client, device_headers)
    assert r.status_code == 403 and r.json()["detail"]["code"] == "expert_unavailable"

    await client.put(f"/api/v1/admin/experts/{EXPERT['id']}", headers=ADMIN, json={**EXPERT, "allow_delegate": False})
    await client.post(f"/api/v1/admin/experts/{EXPERT['id']}/publish", headers=ADMIN, json={})
    r = await create(client, device_headers)
    assert r.status_code == 403 and r.json()["detail"]["code"] == "expert_unavailable"

    await client.post(f"/api/v1/admin/experts/{EXPERT['id']}/offline", headers=ADMIN)
    r = await create(client, device_headers)
    assert r.status_code == 403
    assert await rows() == []


async def test_device_override_controls_delegation(client, device_headers):
    """全厂关着、单给这台开，也能派。"""
    await publish(client)
    device_id = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()[0]["id"]
    r = await client.put(f"/api/v1/admin/devices/{device_id}/policy", headers=ADMIN,
                         json={"overrides": {"expert_delegation": True}, "locks": {}, "note": ""})
    assert r.status_code == 200, r.text
    assert (await create(client, device_headers)).status_code == 201


async def test_create_response_and_clamping(client, device_headers):
    st = await ready_subtask(client, device_headers, token_cap=10, timeout_seconds=99999)
    assert st["id"].startswith("st_") and len(st["id"]) == 3 + 32
    assert st["seq"] == 1 and st["status"] == "running"
    assert st["expert"] == {"id": "mes_analyst", "version": 1, "published_version": 1, "name": "MES 生产分析专家"}
    assert st["token_cap"] == 1000
    deadline = datetime.fromisoformat(st["deadline_at"].replace("Z", "+00:00"))
    assert timedelta(seconds=850) < deadline - datetime.now(timezone.utc) <= timedelta(seconds=900)
    [row] = await rows()
    assert row.machine_name == "PC-001" and row.user_name == "nguyen" and row.conversation_id == "conv-1"
    assert row.task == "查一下昨天 A 车间产量" and row.expert_version == 1 and row.published_version == 1

    r = await create(client, device_headers, run_id="run-2", token_cap=10**9, timeout_seconds=1)
    assert r.json()["token_cap"] == 400_000
    deadline = datetime.fromisoformat(r.json()["deadline_at"].replace("Z", "+00:00"))
    assert deadline - datetime.now(timezone.utc) > timedelta(seconds=50)


async def test_three_per_run_and_failures_count(client, device_headers):
    st = await ready_subtask(client, device_headers)
    # 失败的派遣也算一次
    await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=device_headers, json={"status": "failed"})
    assert (await create(client, device_headers)).json()["seq"] == 2
    assert (await create(client, device_headers)).json()["seq"] == 3
    r = await create(client, device_headers)
    assert r.status_code == 409 and r.json()["detail"]["code"] == "delegation_limit"
    assert len(await rows()) == 3
    # 别的 run、别的电脑各算各的
    assert (await create(client, device_headers, run_id="run-2")).json()["seq"] == 1
    assert (await create(client, await other_device(client))).json()["seq"] == 1


async def test_concurrent_creation_never_exceeds_three(client, device_headers):
    await enable(client)
    await publish(client)
    results = await asyncio.gather(*(create(client, device_headers) for _ in range(5)))
    codes = sorted(r.status_code for r in results)
    assert codes == [201, 201, 201, 409, 409]
    assert sorted(r.json()["seq"] for r in results if r.status_code == 201) == [1, 2, 3]
    assert all(r.json()["detail"]["code"] == "delegation_limit" for r in results if r.status_code == 409)
    assert [r.seq for r in await rows()] == [1, 2, 3]


# ---------- 状态 ----------
async def test_finish_is_idempotent(client, device_headers):
    st = await ready_subtask(client, device_headers)
    url = f"/api/v1/client/subtasks/{st['id']}/finish"
    r = await client.post(url, headers=device_headers, json={
        "status": "completed", "summary": "产量下降是因为停机", "result": {"findings": [1, 2]},
        "tool_calls": 7, "local_tokens": 12345})
    assert r.status_code == 200, r.text
    assert r.json() == {"id": st["id"], "status": "completed", "final": True, "already_final": False,
                        "tokens": {"prompt": 0, "completion": 0, "estimated_calls": 0}, "error_code": ""}

    r = await client.post(url, headers=device_headers, json={"status": "failed", "summary": "覆盖", "error_code": "x"})
    assert r.status_code == 200
    assert r.json()["status"] == "completed" and r.json()["already_final"] is True
    [row] = await rows()
    assert row.summary == "产量下降是因为停机" and json.loads(row.result) == {"findings": [1, 2]}
    assert row.tool_calls == 7 and row.local_tokens == 12345 and row.finished_at is not None


async def test_cancel_then_finish_stays_cancelled(client, device_headers):
    st = await ready_subtask(client, device_headers)
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/cancel", headers=device_headers, json={"reason": "user"})
    assert r.json()["status"] == "cancelled" and r.json()["already_final"] is False
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=device_headers,
                          json={"status": "completed", "summary": "晚到的交卷"})
    assert r.json()["status"] == "cancelled" and r.json()["already_final"] is True
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/cancel", headers=device_headers, json={"reason": "timeout"})
    assert r.json()["status"] == "cancelled" and r.json()["already_final"] is True
    [row] = await rows()
    assert row.summary == "" and row.cancel_reason == "user"


async def test_cancel_for_timeout_marks_timed_out(client, device_headers):
    st = await ready_subtask(client, device_headers)
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/cancel", headers=device_headers,
                          json={"reason": "timeout"})
    assert r.json()["status"] == "timed_out"


async def test_lazy_timeout(client, device_headers):
    st = await ready_subtask(client, device_headers)
    await patch_row(st["id"], deadline_at=datetime.now(timezone.utc) - timedelta(seconds=1))
    r = await client.get(f"/api/v1/client/subtasks/{st['id']}", headers=device_headers)
    assert r.json()["status"] == "timed_out" and r.json()["final"] is True
    assert r.json()["error_code"] == "timeout"
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=device_headers,
                          json={"status": "completed"})
    assert r.json()["status"] == "timed_out" and r.json()["already_final"] is True


async def test_lazy_timeout_on_finish(client, device_headers):
    """没人读过，直接交卷也判超时：服务端的 deadline 说了算。"""
    st = await ready_subtask(client, device_headers)
    await patch_row(st["id"], deadline_at=datetime.now(timezone.utc) - timedelta(seconds=1))
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=device_headers,
                          json={"status": "completed"})
    assert r.json()["status"] == "timed_out" and r.json()["already_final"] is True


async def test_other_devices_cannot_see_or_touch(client, device_headers):
    st = await ready_subtask(client, device_headers)
    other = await other_device(client)
    assert (await client.get(f"/api/v1/client/subtasks/{st['id']}", headers=other)).status_code == 404
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=other, json={"status": "failed"})
    assert r.status_code == 404
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/cancel", headers=other, json={"reason": "user"})
    assert r.status_code == 404
    assert (await client.get("/api/v1/client/subtasks/st_nope", headers=device_headers)).status_code == 404
    assert (await rows())[0].status == "running"


async def test_large_result_is_truncated_but_still_json(client, device_headers):
    st = await ready_subtask(client, device_headers)
    big = {"text": "很长的“引号”\\" * 30000}
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=device_headers,
                          json={"status": "partial", "result": big})
    assert r.status_code == 200
    [row] = await rows()
    assert row.result_truncated is True
    assert len(row.result.encode("utf-8")) <= subtask_store.RESULT_MAX_BYTES
    saved = json.loads(row.result)
    assert saved["truncated"] is True and saved["partial"].startswith('{"text": "很长的')


# ---------- 网关 ----------
async def chat(client, headers, subtask_id=None, stream=False, tools=None):
    body = {"model": "agent", "messages": [{"role": "user", "content": "查产量" * 30}], "stream": stream}
    if subtask_id is not None:
        body["flyknit_subtask_id"] = subtask_id
    if tools is not None:
        body["tools"] = tools
    return await client.post("/api/v1/chat/completions", headers=headers, json=body)


def reply(usage=True):
    payload = {"choices": [{"message": {"role": "assistant", "content": "好的" * 50}}]}
    if usage:
        payload["usage"] = {"prompt_tokens": 1200, "completion_tokens": 300}
    return httpx.Response(200, json=payload)


@respx.mock
async def test_gateway_accumulates_usage_and_strips_fields(client, device_headers):
    st = await ready_subtask(client, device_headers)
    await setup_models(client)
    route = respx.post(UPSTREAM).mock(return_value=reply())
    r = await chat(client, device_headers, st["id"], tools=[ASK_EXPERT, READ_FILE])
    assert r.status_code == 200, r.text
    sent = json.loads(route.calls.last.request.content)
    assert "flyknit_subtask_id" not in sent
    assert [t["function"]["name"] for t in sent["tools"]] == ["read_file"]  # 专家不能再派遣
    await chat(client, device_headers, st["id"])
    state = (await client.get(f"/api/v1/client/subtasks/{st['id']}", headers=device_headers)).json()
    assert state["tokens"] == {"prompt": 2400, "completion": 600, "estimated_calls": 0}


@respx.mock
async def test_subtask_calls_are_not_archived_as_employee_chat(client, device_headers):
    from app.models import ChatRecord

    st = await ready_subtask(client, device_headers)
    await setup_models(client)
    respx.post(UPSTREAM).mock(return_value=reply())
    body = {"model": "agent", "conversation_id": "conv-1", "flyknit_subtask_id": st["id"],
            "messages": [{"role": "user", "content": "<子任务>查 A 车间产量</子任务>"}]}
    assert (await client.post("/api/v1/chat/completions", headers=device_headers, json=body)).status_code == 200
    body.pop("flyknit_subtask_id")
    body["messages"] = [{"role": "user", "content": "员工自己问的"}]
    assert (await client.post("/api/v1/chat/completions", headers=device_headers, json=body)).status_code == 200
    async with db.get_sessionmaker()() as s:
        rows = (await s.scalars(sa.select(ChatRecord))).all()
    # 任务书是主助手写的，不是员工说的话：只归档员工那一句
    assert [r.user_content for r in rows] == ["员工自己问的"]


@respx.mock
async def test_gateway_estimates_missing_usage(client, device_headers):
    st = await ready_subtask(client, device_headers)
    await setup_models(client)
    respx.post(UPSTREAM).mock(return_value=reply(usage=False))
    assert (await chat(client, device_headers, st["id"])).status_code == 200
    state = (await client.get(f"/api/v1/client/subtasks/{st['id']}", headers=device_headers)).json()
    # 提示 90 字 / 3，回答 100 字 / 2
    assert state["tokens"] == {"prompt": 30, "completion": 50, "estimated_calls": 1}


@respx.mock
async def test_gateway_stream_usage_and_estimate(client, device_headers):
    st = await ready_subtask(client, device_headers)
    await setup_models(client)
    with_usage = (
        b'data: {"choices":[{"delta":{"content":"abcd"}}]}\n\n'
        b'data: {"choices":[],"usage":{"prompt_tokens":500,"completion_tokens":20}}\n\n'
        b"data: [DONE]\n\n"
    )
    without = (
        b'data: {"choices":[{"delta":{"content":"abcd"}}]}\n\n'
        b'data: {"choices":[{"delta":{"tool_calls":[{"function":{"name":"read","arguments":"{\\"p\\":1}"}}]}}]}\n\n'
        b"data: [DONE]\n\n"
    )
    route = respx.post(UPSTREAM)
    route.side_effect = [
        httpx.Response(200, content=with_usage, headers={"content-type": "text/event-stream"}),
        httpx.Response(200, content=without, headers={"content-type": "text/event-stream"}),
    ]
    r = await chat(client, device_headers, st["id"], stream=True)
    assert r.status_code == 200 and r.content == with_usage
    r = await chat(client, device_headers, st["id"], stream=True)
    assert r.content == without
    assert "flyknit_subtask_id" not in json.loads(route.calls.last.request.content)
    state = (await client.get(f"/api/v1/client/subtasks/{st['id']}", headers=device_headers)).json()
    # 第二次：提示 90 字 / 3 = 30；回答 abcd + read + {"p":1} = 4 + 4 + 7 = 15 字 / 2 = 8
    assert state["tokens"] == {"prompt": 530, "completion": 28, "estimated_calls": 1}


@respx.mock
async def test_gateway_closed_and_unknown(client, device_headers):
    st = await ready_subtask(client, device_headers)
    await setup_models(client)
    route = respx.post(UPSTREAM).mock(return_value=reply())

    r = await chat(client, await other_device(client), st["id"])
    assert r.status_code == 403 and r.json()["error"]["type"] == "subtask_unknown"
    r = await chat(client, device_headers, "st_" + "0" * 32)
    assert r.status_code == 403 and r.json()["error"]["type"] == "subtask_unknown"

    await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=device_headers, json={"status": "completed"})
    r = await chat(client, device_headers, st["id"])
    assert r.status_code == 409 and r.json()["error"]["type"] == "subtask_closed"
    assert not route.called


@respx.mock
async def test_gateway_expired_subtask_is_timed_out(client, device_headers):
    st = await ready_subtask(client, device_headers)
    await setup_models(client)
    route = respx.post(UPSTREAM).mock(return_value=reply())
    await patch_row(st["id"], deadline_at=datetime.now(timezone.utc) - timedelta(seconds=1))
    r = await chat(client, device_headers, st["id"])
    assert r.status_code == 409 and r.json()["error"]["type"] == "subtask_closed"
    assert (await rows())[0].status == "timed_out"
    assert not route.called


@respx.mock
async def test_gateway_disabled_cancels_subtask(client, device_headers):
    st = await ready_subtask(client, device_headers)
    await setup_models(client)
    route = respx.post(UPSTREAM).mock(return_value=reply())
    await enable(client, False)
    r = await chat(client, device_headers, st["id"])
    assert r.status_code == 409 and r.json()["error"]["type"] == "subtask_closed"
    [row] = await rows()
    assert row.status == "cancelled" and row.error_code == "disabled"
    # 已经取消的，员工端晚到的交卷不覆盖
    r = await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=device_headers,
                          json={"status": "completed"})
    assert r.json()["status"] == "cancelled" and r.json()["already_final"] is True
    assert not route.called


@respx.mock
async def test_gateway_budget(client, device_headers):
    st = await ready_subtask(client, device_headers, token_cap=1000)
    await setup_models(client)
    route = respx.post(UPSTREAM).mock(return_value=reply())
    assert (await chat(client, device_headers, st["id"])).status_code == 200  # 1500 ≥ 1000：用完了
    r = await chat(client, device_headers, st["id"])
    assert r.status_code == 429 and r.json()["error"]["type"] == "subtask_budget"
    assert route.call_count == 1
    assert (await rows())[0].status == "running"  # 预算用完只拦请求，结束由员工端交卷


@respx.mock
async def test_ask_expert_stripped_only_when_disabled(client, device_headers):
    await setup_models(client)
    route = respx.post(UPSTREAM).mock(return_value=reply())
    await chat(client, device_headers, tools=[ASK_EXPERT, READ_FILE])
    assert [t["function"]["name"] for t in json.loads(route.calls.last.request.content)["tools"]] == ["read_file"]

    # 只剩它一个时连 tools 和 tool_choice 一起去掉
    await client.post("/api/v1/chat/completions", headers=device_headers, json={
        "model": "agent", "messages": [{"role": "user", "content": "hi"}], "tools": [ASK_EXPERT],
        "tool_choice": {"type": "function", "function": {"name": "ask_expert"}}})
    sent = json.loads(route.calls.last.request.content)
    assert "tools" not in sent and "tool_choice" not in sent

    await enable(client)
    await chat(client, device_headers, tools=[ASK_EXPERT, READ_FILE])
    sent = json.loads(route.calls.last.request.content)
    assert [t["function"]["name"] for t in sent["tools"]] == ["ask_expert", "read_file"]


@respx.mock
async def test_usage_accounting_failure_does_not_break_chat(client, device_headers, monkeypatch):
    st = await ready_subtask(client, device_headers)
    await setup_models(client)
    respx.post(UPSTREAM).mock(return_value=reply())

    async def boom(*_args, **_kwargs):
        raise RuntimeError("db down")

    monkeypatch.setattr(subtask_store, "add_usage", boom)
    r = await chat(client, device_headers, st["id"])
    assert r.status_code == 200 and r.json()["choices"][0]["message"]["content"].startswith("好的")


# ---------- 审计 ----------
async def test_audit_carries_subtask_and_actor(client, device_headers):
    item = {"tool_name": "read_file", "risk": "auto", "decision": "auto", "summary": "[MES 生产分析专家] 读文件"}
    r = await client.post("/api/v1/audit", headers=device_headers, json={"items": [
        {**item, "subtask_id": "st_" + "a" * 32, "actor": "expert:mes_analyst"},
        {**item, "summary": "主助手读文件"},
    ]})
    assert r.status_code == 204, r.text
    async with db.get_sessionmaker()() as s:
        found = list(await s.scalars(sa.select(AuditLog).order_by(AuditLog.id)))
    assert (found[0].subtask_id, found[0].actor) == ("st_" + "a" * 32, "expert:mes_analyst")
    assert (found[1].subtask_id, found[1].actor) == ("", "")
    listed = (await client.get("/api/v1/admin/audit", headers=ADMIN)).json()
    assert {a["actor"] for a in listed} == {"expert:mes_analyst", ""}


# ---------- 后台 ----------
async def test_only_owner_can_switch_delegation(client):
    _, owner = await ready(client, "owner")
    _, clerk = await ready(client, "clerk", can_dispatch=True, can_read_chats=True)
    rev = config_events.current()
    r = await client.put("/api/v1/admin/experts/delegation", headers=clerk, json={"enabled": True})
    assert r.status_code == 403
    assert (await client.get("/api/v1/admin/experts/delegation", headers=clerk)).json() == {"enabled": False}
    r = await client.put("/api/v1/admin/experts/delegation", headers=owner, json={"enabled": True})
    assert r.status_code == 200 and r.json() == {"enabled": True}
    assert config_events.current() > rev  # 在线的员工端马上来拉
    # 写的是安全中心的全厂设置，别的项不受影响
    security = (await client.get("/api/v1/admin/security", headers=owner)).json()
    assert security["values"]["expert_delegation"] is True
    assert security["effective"]["sandbox"]["value"] is True


async def test_admin_lists_subtasks_with_filters(client, device_headers, chat_reader_headers):
    st = await ready_subtask(client, device_headers)
    await client.post(f"/api/v1/client/subtasks/{st['id']}/finish", headers=device_headers,
                      json={"status": "completed", "summary": "结论", "error": ""})
    second = (await create(client, device_headers)).json()
    await patch_row(second["id"], deadline_at=datetime.now(timezone.utc) - timedelta(seconds=1))

    # 共享令牌：管控数据都在，任务说明和摘要（聊天内容）不给
    listed = (await client.get("/api/v1/admin/subtasks", headers=ADMIN)).json()
    assert [x["status"] for x in listed] == ["timed_out", "completed"]  # 过期的也判成超时
    done = listed[1]
    assert done["machine_name"] == "PC-001" and done["user_name"] == "nguyen"
    assert done["expert_name"] == "MES 生产分析专家" and done["duration_ms"] is not None
    assert done["summary"] == "" and done["task"] == ""

    listed = (await client.get("/api/v1/admin/subtasks", headers=chat_reader_headers)).json()
    assert listed[1]["summary"] == "结论" and listed[1]["task"] == "查一下昨天 A 车间产量"

    r = await client.get("/api/v1/admin/subtasks?status=completed", headers=ADMIN)
    assert [x["id"] for x in r.json()] == [st["id"]]
    assert (await client.get("/api/v1/admin/subtasks?expert_id=other", headers=ADMIN)).json() == []
    assert (await client.get("/api/v1/admin/subtasks?limit=501", headers=ADMIN)).status_code == 422
    assert (await client.get("/api/v1/admin/subtasks", headers=device_headers)).status_code == 403


async def test_subtasks_follow_audit_retention(client, device_headers):
    from app.services import housekeeping

    st = await ready_subtask(client, device_headers)
    await patch_row(st["id"], created_at=datetime.now(timezone.utc) - timedelta(days=200))
    async with db.get_sessionmaker()() as s:
        removed = await housekeeping.purge(s, {"audit_days": 180})
    assert removed["subtasks"] == 1
    assert await rows() == []
