"""钩子（docs/hooks.md）：谁能配、总开关、下发不带地址密钥、服务端自己匹配和算范围、只能收紧、出错按 on_error、签名、通知。"""

import asyncio
import hashlib
import hmac
import json

import httpx
import pytest
from sqlalchemy import select

from app.db import get_sessionmaker
from app.models import HookCall
from app.services import hook_service
from conftest import ADMIN
from test_instructions import device_id_of, dispatcher


class FakeHooks:
    """假的钩子服务：按地址返回不同的回答，记下收到的请求。"""

    def __init__(self):
        self.seen: list[httpx.Request] = []
        self.answers: dict[str, object] = {}

    def handler(self, request: httpx.Request) -> httpx.Response:
        self.seen.append(request)
        answer = self.answers.get(request.url.path, {"decision": "allow"})
        if answer == "timeout":
            raise httpx.ReadTimeout("slow", request=request)
        if isinstance(answer, int):
            return httpx.Response(answer)
        if isinstance(answer, str):
            return httpx.Response(200, text=answer)
        return httpx.Response(200, json=answer)

    def bodies(self, path: str) -> list[dict]:
        return [json.loads(r.content) for r in self.seen if r.url.path == path]


@pytest.fixture
def fake(monkeypatch):
    f = FakeHooks()
    monkeypatch.setattr(hook_service, "transport", httpx.MockTransport(f.handler))
    return f


async def _hook(client, **extra):
    body = {"name": "ERP 写操作审核", "events": ["pre_tool"], "tools": "mcp__erp__*", "url": "https://hooks.local/erp", **extra}
    r = await client.post("/api/v1/admin/hooks", headers=ADMIN, json=body)
    assert r.status_code == 201, r.text
    return r.json()


async def _on(client):
    assert (await client.put("/api/v1/admin/hooks/switch", headers=ADMIN, json={"enabled": True})).status_code == 200


async def _check(client, headers, tool="mcp__erp__create_order", args='{"qty": 5}'):
    r = await client.post("/api/v1/client/hooks/check", headers=headers, json={"tool": tool, "arguments": args, "conversation_id": "c1"})
    assert r.status_code == 200, r.text
    return r.json()


async def test_only_owners_configure_hooks_and_config_never_carries_url_or_secret(client, device_headers, fake):
    body = {"name": "x", "url": "https://hooks.local/x"}
    await dispatcher(client, "it.boss")  # 第一个账号自动是超级管理员
    ops = await dispatcher(client)
    assert (await client.post("/api/v1/admin/hooks", headers=ops, json=body)).status_code == 403
    assert (await client.put("/api/v1/admin/hooks/switch", headers=ops, json={"enabled": True})).status_code == 403
    bad = await client.post("/api/v1/admin/hooks", headers=ADMIN, json={**body, "url": "ftp://x"})
    assert bad.status_code == 422
    assert (await client.post("/api/v1/admin/hooks", headers=ADMIN, json={**body, "events": []})).status_code == 422
    assert (await client.post("/api/v1/admin/hooks", headers=ADMIN, json={**body, "match": "("})).status_code == 422

    hook = await _hook(client, secret="s3cret-value")
    assert hook["has_secret"] is True and "secret" not in hook and "secret_enc" not in hook
    listed = (await client.get("/api/v1/admin/hooks", headers=ops)).json()  # 能看，但改不了
    assert listed["enabled"] is False and listed["items"][0]["name"] == "ERP 写操作审核"
    assert "s3cret" not in json.dumps(listed)

    # 总开关关着：员工端配置里没有钩子，检查一律 allow，钩子收不到任何请求
    cfg = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert cfg["hooks"] == []
    assert (await _check(client, device_headers))["decision"] == "allow"
    assert fake.seen == []

    await _on(client)
    cfg = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert cfg["hooks"] == [{"id": hook["id"], "events": ["pre_tool"], "tools": "mcp__erp__*", "match": "", "on_error": "ask", "timeout_ms": 3000}]
    assert "hooks.local" not in json.dumps(cfg) and "s3cret" not in json.dumps(cfg)


async def test_check_takes_the_strictest_answer_and_server_does_its_own_matching(client, device_headers, fake):
    await _on(client)
    await _hook(client, name="审核", url="https://hooks.local/review")
    await _hook(client, name="财务", url="https://hooks.local/finance", match="amount")
    fake.answers["/review"] = {"decision": "ask", "reason": "建单要主管确认"}
    fake.answers["/finance"] = {"decision": "deny", "reason": "金额超过授权"}

    # 只有审核匹配（参数里没有 amount）
    r = await _check(client, device_headers)
    assert r["decision"] == "ask" and "建单要主管确认" in r["reason"]
    # 两个都匹配：取最严的
    r = await _check(client, device_headers, args='{"amount": 99999}')
    assert r["decision"] == "deny" and "金额超过授权" in r["reason"]
    # 不匹配的工具：一个钩子都不问（员工端报什么服务端都自己再匹配一遍）
    before = len(fake.seen)
    assert (await _check(client, device_headers, tool="read_file"))["decision"] == "allow"
    assert len(fake.seen) == before

    # 发出去的内容：事件、工具、参数、设备；调用记录写进后台
    sent = fake.bodies("/review")[0]
    assert sent["event"] == "pre_tool" and sent["tool"] == {"name": "mcp__erp__create_order", "arguments": {"qty": 5}}
    assert sent["device"]["machine_name"] == "PC-001" and sent["conversation_id"] == "c1"
    calls = (await client.get("/api/v1/admin/hooks", headers=ADMIN)).json()["calls"]
    assert {c["decision"] for c in calls} == {"ask", "deny"}


async def test_errors_follow_on_error_and_never_loosen(client, device_headers, fake):
    await _on(client)
    await _hook(client, name="慢", url="https://hooks.local/slow")
    fake.answers["/slow"] = "timeout"
    r = await _check(client, device_headers)
    assert r["decision"] == "ask" and "超时" in r["reason"]  # 默认 on_error=ask

    for answer in (500, "not json", {"decision": "approve-everything"}):
        fake.answers["/slow"] = answer
        assert (await _check(client, device_headers))["decision"] == "ask"

    hook_id = (await client.get("/api/v1/admin/hooks", headers=ADMIN)).json()["items"][0]["id"]
    await client.patch(f"/api/v1/admin/hooks/{hook_id}", headers=ADMIN, json={"on_error": "deny"})
    fake.answers["/slow"] = 502
    assert (await _check(client, device_headers))["decision"] == "deny"


async def test_scope_signature_and_long_arguments(client, device_headers, fake):
    await _on(client)
    did = await device_id_of(client)
    # 只给别的部门：这台电脑不问
    hook = await _hook(client, url="https://hooks.local/scoped", secret="k-123", scope_departments=["财务部"])
    assert (await _check(client, device_headers))["decision"] == "allow" and fake.seen == []
    await client.patch(f"/api/v1/admin/hooks/{hook['id']}", headers=ADMIN, json={"scope_departments": [], "scope_devices": [did]})
    long_text = "x" * 50000
    await _check(client, device_headers, args=json.dumps({"note": long_text}))
    req = fake.seen[-1]
    expected = "sha256=" + hmac.new(b"k-123", req.content, hashlib.sha256).hexdigest()
    assert req.headers["X-Flyknit-Signature"] == expected and req.headers["X-Flyknit-Event"] == "pre_tool"
    note = json.loads(req.content)["tool"]["arguments"]["note"]
    assert len(note) < 2100 and "已截断" in note

    # 测试按钮：发示例请求，带 test 标记，不进调用记录
    fake.answers["/scoped"] = {"decision": "deny", "reason": "测试"}
    t = (await client.post(f"/api/v1/admin/hooks/{hook['id']}/test", headers=ADMIN)).json()
    assert t == {"ok": True, "decision": "deny", "reason": "测试", "error": "", "latency_ms": t["latency_ms"]}
    assert json.loads(fake.seen[-1].content)["test"] is True


async def test_post_tool_and_stop_are_notifications(client, device_headers, fake):
    await _on(client)
    await _hook(client, name="记录", events=["post_tool", "stop"], tools="*", url="https://hooks.local/log")
    r = await client.post("/api/v1/client/hooks/notify", headers=device_headers,
                          json={"event": "post_tool", "tool": "write_file", "arguments": '{"path": "a.txt"}', "ok": True, "summary": "已写入"})
    assert r.status_code == 202
    r = await client.post("/api/v1/client/hooks/notify", headers=device_headers,
                          json={"event": "stop", "status": "succeeded", "summary": "整理完了", "conversation_id": "c9"})
    assert r.status_code == 202
    for _ in range(50):
        if len(fake.bodies("/log")) >= 2:
            break
        await asyncio.sleep(0.02)
    events = {b["event"]: b for b in fake.bodies("/log")}
    assert events["post_tool"]["result"] == {"ok": True, "summary": "已写入"}
    assert events["stop"]["stop"] == {"status": "succeeded", "summary": "整理完了"}
    async with get_sessionmaker()() as s:
        rows = (await s.scalars(select(HookCall))).all()
        assert {r.event for r in rows} == {"post_tool", "stop"} and {r.decision for r in rows} == {"sent"}
    # pre_tool 的钩子不收 post_tool
    assert (await _check(client, device_headers))["decision"] == "allow"
