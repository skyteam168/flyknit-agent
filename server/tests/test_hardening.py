"""上线前补的几处防线：默认令牌、断流记账、上游中断、坏密钥、并发累加、超长字段、坏 zip、登录爆破。"""

import asyncio
import json

import httpx
import pytest
import respx
from sqlalchemy import select

from app.config import get_settings
from app.db import get_sessionmaker
from app.main import app
from app.models import AuditLog, ChatRecord, Device, Provider, UsageDaily
from app.services import usage_store
from conftest import ADMIN, setup_models
from test_releases import owner_headers, upload

pytestmark = pytest.mark.asyncio

CHAT = {"model": "chat", "messages": [{"role": "user", "content": "hi"}]}


# ---------- 默认管理员令牌 ----------

async def test_default_admin_token_is_disabled(client, monkeypatch):
    default = "change-me-admin-token"
    monkeypatch.setattr(get_settings(), "admin_token", default)
    r = await client.get("/api/v1/admin/providers", headers={"Authorization": f"Bearer {default}"})
    assert r.status_code == 403
    assert "默认的管理员令牌已停用" in r.json()["detail"]
    # 别的随便什么令牌还是老说法
    r = await client.get("/api/v1/admin/providers", headers={"Authorization": "Bearer nope"})
    assert r.json()["detail"] == "管理员令牌无效"


# ---------- 网关 ----------

async def _today_usage(device_id: int) -> int:
    async with get_sessionmaker()() as s:
        return await usage_store.used_today(s, device_id)


@respx.mock
async def test_user_stop_still_records_usage_and_archive(client, device_headers):
    """员工点「停止」：连接断开、转发被取消，已经产生的用量和那半句回答也得记下。"""
    await setup_models(client)

    async def slow_stream():
        yield (b'data: {"choices":[{"delta":{"content":"\xe5\x8d\x8a\xe5\x8f\xa5"}}],'
               b'"usage":{"prompt_tokens":70,"completion_tokens":30}}\n\n')
        await asyncio.sleep(30)  # 上游还在慢慢生成
        yield b"data: [DONE]\n\n"

    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, content=slow_stream(), headers={"content-type": "text/event-stream"})
    )
    body = json.dumps({**CHAT, "stream": True, "conversation_id": "conv-stop"}).encode()
    got_first_chunk = asyncio.Event()
    request_sent = False

    async def receive():
        nonlocal request_sent
        if not request_sent:
            request_sent = True
            return {"type": "http.request", "body": body, "more_body": False}
        await got_first_chunk.wait()
        return {"type": "http.disconnect"}

    async def send(message):
        if message["type"] == "http.response.body" and message.get("body"):
            got_first_chunk.set()

    scope = {
        "type": "http", "asgi": {"version": "3.0"}, "http_version": "1.1", "method": "POST",
        "scheme": "http", "path": "/api/v1/chat/completions", "raw_path": b"/api/v1/chat/completions",
        "query_string": b"", "root_path": "", "client": ("127.0.0.1", 1234), "server": ("test", 80),
        "headers": [(b"host", b"test"), (b"content-type", b"application/json"),
                    (b"authorization", device_headers["Authorization"].encode())],
    }
    await asyncio.wait_for(app(scope, receive, send), timeout=10)

    async with get_sessionmaker()() as s:
        device_id = await s.scalar(select(Device.id))
    assert await _today_usage(device_id) == 100
    async with get_sessionmaker()() as s:
        rec = await s.scalar(select(ChatRecord).where(ChatRecord.conversation_id == "conv-stop"))
    assert rec is not None and rec.assistant_content == "半句"


@respx.mock
async def test_upstream_drop_mid_stream_sends_error_chunk(client, device_headers):
    await setup_models(client)

    async def broken():
        yield b'data: {"choices":[{"delta":{"content":"hi"}}],"usage":{"prompt_tokens":5,"completion_tokens":1}}\n\n'
        raise httpx.ReadError("connection reset")

    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, content=broken(), headers={"content-type": "text/event-stream"})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json={**CHAT, "stream": True})
    assert r.status_code == 200
    last = r.text.strip().splitlines()[-1]
    assert json.loads(last.removeprefix("data: "))["error"]["type"] == "upstream_error"
    async with get_sessionmaker()() as s:
        device_id = await s.scalar(select(Device.id))
    assert await _today_usage(device_id) == 6


@respx.mock
async def test_upstream_read_failure_is_502(client, device_headers):
    await setup_models(client)

    async def broken():
        raise httpx.ReadTimeout("timed out")
        yield b""  # pragma: no cover

    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, content=broken(), headers={"content-type": "application/json"})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json=CHAT)
    assert r.status_code == 502
    assert "读取超时或连接中断" in r.json()["error"]["message"]


async def _break_provider_keys():
    async with get_sessionmaker()() as s:
        for p in await s.scalars(select(Provider)):
            p.api_key_enc = "gAAAAA-not-a-valid-token"
        await s.commit()


async def test_undecryptable_key_skips_model_instead_of_500(client, device_headers):
    m1, _ = await setup_models(client)
    await _break_provider_keys()
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json=CHAT)
    assert r.status_code == 503
    providers = (await client.get("/api/v1/admin/providers", headers=ADMIN)).json()
    assert providers[0]["api_key_masked"] == "（密钥无法解密，请重新填写）"
    r = await client.post(f"/api/v1/admin/models/{m1['id']}/test", headers=ADMIN)
    assert r.status_code == 200 and r.json()["ok"] is False


# ---------- 未处理的异常 ----------

async def test_unhandled_error_returns_chinese_500(client, monkeypatch):
    from app.services import settings_store

    async def boom(_session):
        raise RuntimeError("boom")

    monkeypatch.setattr(settings_store, "get_policy", boom)
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as c:
        r = await c.get("/api/v1/admin/policy", headers=ADMIN)
        assert r.status_code == 500
        assert r.json() == {"detail": "服务器内部错误，请联系 IT 管理员查看日志"}
        # HTTPException、参数校验照旧
        assert (await c.get("/api/v1/admin/providers")).status_code == 401
        assert (await c.get("/api/v1/admin/audit?limit=0", headers=ADMIN)).status_code == 422


# ---------- 用量累加 ----------

async def test_usage_increments_add_up(client, device_headers):
    async with get_sessionmaker()() as s:
        device_id = await s.scalar(select(Device.id))

    async def one(p, c):
        async with get_sessionmaker()() as s:
            await usage_store.record(s, device_id, "chat", p, c, model_id=1, model_name="Qwen")

    await one(10, 5)
    await asyncio.gather(one(100, 50), one(1000, 500))
    async with get_sessionmaker()() as s:
        rows = (await s.scalars(select(UsageDaily).where(UsageDaily.device_id == device_id))).all()
        assert len(rows) == 1
        assert (rows[0].prompt_tokens, rows[0].completion_tokens, rows[0].requests) == (1110, 555, 3)
        await usage_store.record_request(s, device_id, "asr")
        await usage_store.record_request(s, device_id, "asr")
    async with get_sessionmaker()() as s:
        asr = await s.scalar(select(UsageDaily).where(UsageDaily.scene == "asr"))
        assert asr.requests == 2 and asr.prompt_tokens == 0


# ---------- 超长字段 ----------

async def test_overlong_client_fields_are_truncated(client):
    r = await client.post("/api/v1/devices/register", json={
        "enrollment_key": "test-enroll", "machine_name": "PC-1", "user_name": "u" * 500,
        "os_version": "o" * 500, "client_version": "v" * 100, "ui_language": "zh-Hans-CN-x-very-long",
    })
    assert r.status_code == 200, r.text
    headers = {"Authorization": f"Bearer {r.json()['token']}"}
    r = await client.post("/api/v1/devices/heartbeat", headers=headers, json={
        "client_version": "9" * 100, "ui_language": "l" * 50, "mac_address": "m" * 100, "domain": "d" * 300,
    })
    assert r.status_code == 204, r.text
    r = await client.post("/api/v1/audit", headers=headers, json={"items": [{
        "conversation_id": "c" * 100, "tool_name": "t" * 300, "risk": "auto", "decision": "auto",
        "scene": "s" * 50, "status": "x" * 50,
    }]})
    assert r.status_code == 204, r.text
    async with get_sessionmaker()() as s:
        d = await s.scalar(select(Device))
        assert (len(d.client_version), len(d.ui_language), len(d.user_name)) == (50, 10, 200)
        a = await s.scalar(select(AuditLog))
        assert (len(a.conversation_id), len(a.tool_name), len(a.scene), len(a.status)) == (64, 100, 20, 20)


# ---------- 发布 ----------

async def test_release_upload_rejects_non_zip(client):
    headers = await owner_headers(client)
    r = await upload(client, headers, body=b"this is not a zip at all")
    assert r.status_code == 400
    assert r.json()["detail"] == "上传的不是 zip 文件"


# ---------- 管理端零碎 ----------

async def test_admin_bad_inputs_are_400_not_500(client):
    r = await client.put("/api/v1/admin/policy", headers=ADMIN, json={"blocked_patterns": "rm -rf"})
    assert r.status_code == 400
    r = await client.put("/api/v1/admin/policy", headers=ADMIN, json={"version": "abc"})
    assert r.status_code == 400
    assert (await client.get("/api/v1/admin/audit?offset=-1", headers=ADMIN)).status_code == 422


@respx.mock
async def test_sync_models_handles_bad_json_and_bad_regex(client):
    p = (await client.post("/api/v1/admin/providers", headers=ADMIN,
                           json={"name": "x", "base_url": "http://p.local/v1", "api_key": "k"})).json()
    respx.get("http://p.local/v1/models").mock(return_value=httpx.Response(200, text="<html>"))
    r = await client.post(f"/api/v1/admin/providers/{p['id']}/sync-models", headers=ADMIN)
    assert r.status_code == 502 and r.json()["detail"] == "模型服务返回的不是 JSON"
    respx.get("http://p.local/v1/models").mock(return_value=httpx.Response(200, json={"data": [{"id": "a"}]}))
    r = await client.post(f"/api/v1/admin/providers/{p['id']}/sync-models?include=(", headers=ADMIN)
    assert r.status_code == 400


# ---------- 登录爆破 ----------

async def test_login_is_rate_limited_per_username(client):
    await client.post("/api/v1/admin/users", headers=ADMIN,
                      json={"username": "it.guard", "password": "init-pass-123"})
    for _ in range(5):
        r = await client.post("/api/v1/admin/login", json={"username": "it.guard", "password": "wrong-pass"})
        assert r.status_code == 401
    # 第 6 次哪怕密码对了也先挡住
    r = await client.post("/api/v1/admin/login", json={"username": "it.guard", "password": "init-pass-123"})
    assert r.status_code == 429
    assert r.json()["detail"] == "登录失败次数过多，请 10 分钟后再试"


async def test_successful_login_resets_username_counter(client):
    await client.post("/api/v1/admin/users", headers=ADMIN,
                      json={"username": "it.ok", "password": "init-pass-123"})
    for _ in range(4):
        await client.post("/api/v1/admin/login", json={"username": "it.ok", "password": "wrong-pass"})
    assert (await client.post("/api/v1/admin/login",
                              json={"username": "it.ok", "password": "init-pass-123"})).status_code == 200
    for _ in range(4):
        r = await client.post("/api/v1/admin/login", json={"username": "it.ok", "password": "wrong-pass"})
        assert r.status_code == 401


async def test_login_is_rate_limited_per_ip(client):
    for i in range(20):
        await client.post("/api/v1/admin/login", json={"username": f"guess{i}", "password": "x"})
    r = await client.post("/api/v1/admin/login", json={"username": "someone-else", "password": "x"})
    assert r.status_code == 429
