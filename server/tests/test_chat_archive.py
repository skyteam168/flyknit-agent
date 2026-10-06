"""聊天记录归档：网关落库、派生调用不入库、访问控制、留痕、过期清理。

纯函数与备份的用例在 test_housekeeping.py。
"""

from datetime import datetime, timedelta, timezone

import httpx
import pytest
import respx
import sqlalchemy as sa

from app.services import chat_archive, housekeeping
from conftest import ADMIN, setup_models

pytestmark = pytest.mark.asyncio


def sse(*chunks: str, usage: str = '{"prompt_tokens":120,"completion_tokens":30}') -> bytes:
    lines = [f'data: {{"choices":[{{"delta":{{"content":"{c}"}}}}]}}\n\n' for c in chunks]
    lines.append(f'data: {{"choices":[],"usage":{usage}}}\n\n')
    lines.append("data: [DONE]\n\n")
    return "".join(lines).encode()


async def send(client, headers, text="把九月的日报整理一下", conversation_id="conv-1", stream=False, **extra):
    body = {
        "model": "agent",
        "messages": [{"role": "user", "content": text}],
        "stream": stream,
        **extra,
    }
    if conversation_id is not None:
        body["conversation_id"] = conversation_id
    return await client.post("/api/v1/chat/completions", headers=headers, json=body)


# ---------- 网关归档 ----------

@respx.mock
async def test_archives_a_non_streaming_turn(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={
            "choices": [{"message": {"role": "assistant", "content": "已整理到 D:\\日报"}}],
            "usage": {"prompt_tokens": 120, "completion_tokens": 30},
        })
    )
    assert (await send(client, device_headers)).status_code == 200

    rows = (await client.get("/api/v1/admin/chats", headers=await reader(client))).json()
    assert len(rows) == 1
    assert rows[0]["turns"] == 1
    assert rows[0]["conversation_id"] == "conv-1"
    # 列表页不该带正文
    assert "user_content" not in rows[0]


@respx.mock
async def test_archives_a_streaming_turn(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, content=sse("已经", "整理", "好了"),
                                    headers={"content-type": "text/event-stream"})
    )
    r = await send(client, device_headers, stream=True)
    assert r.status_code == 200
    await r.aread()

    detail = (await client.get("/api/v1/admin/chats/conv-1", headers=await reader(client))).json()
    assert detail[0]["assistant_content"] == "已经整理好了"
    assert detail[0]["user_content"] == "把九月的日报整理一下"
    assert detail[0]["prompt_tokens"] == 120


@respx.mock
async def test_derived_calls_are_not_archived(client, device_headers):
    """压缩上下文、复盘、生成标题不带 conversation_id，不是用户说的话。"""
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "日报整理"}}]})
    )
    assert (await send(client, device_headers, conversation_id=None)).status_code == 200

    rows = (await client.get("/api/v1/admin/chats", headers=await reader(client))).json()
    assert rows == []


@respx.mock
async def test_each_turn_is_one_row_not_the_whole_history(client, device_headers):
    """十轮对话该是十行，不是五十五行。"""
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "好"}}]})
    )
    history = []
    for i in range(5):
        history.append({"role": "user", "content": f"第 {i} 轮"})
        await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "agent", "conversation_id": "conv-n", "messages": list(history)})
        history.append({"role": "assistant", "content": "好"})

    detail = (await client.get("/api/v1/admin/chats/conv-n", headers=await reader(client))).json()
    assert len(detail) == 5
    assert [d["user_content"] for d in detail] == [f"第 {i} 轮" for i in range(5)]


@respx.mock
async def test_upstream_failure_is_not_archived(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(500, json={"error": {"message": "boom"}})
    )
    respx.post("http://backup.local/v1/chat/completions").mock(
        return_value=httpx.Response(500, json={"error": {"message": "boom"}})
    )
    await send(client, device_headers)
    rows = (await client.get("/api/v1/admin/chats", headers=await reader(client))).json()
    assert rows == []


# ---------- 访问控制 ----------

async def make_user(client, username="it.zhang", password="flyknit-2026", can_read=True):
    r = await client.post("/api/v1/admin/users", headers=ADMIN, json={
        "username": username, "password": password, "display_name": "张工", "can_read_chats": can_read,
    })
    assert r.status_code == 201, r.text
    return r.json()


async def login(client, username="it.zhang", password="flyknit-2026"):
    r = await client.post("/api/v1/admin/login", json={"username": username, "password": password})
    assert r.status_code == 200, r.text
    return r.json()


async def reader(client):
    """建一个能看聊天、且已经改过初始密码的账号，返回它的请求头。"""
    if not getattr(reader, "_headers", None):
        await make_user(client)
        token = (await login(client))["token"]
        headers = {"Authorization": f"Bearer {token}"}
        await client.post("/api/v1/admin/password", headers=headers,
                          json={"old_password": "flyknit-2026", "new_password": "flyknit-2027"})
        token = (await login(client, password="flyknit-2027"))["token"]
        reader._headers = {"Authorization": f"Bearer {token}"}
    return reader._headers


@pytest.fixture(autouse=True)
def _reset_reader():
    reader._headers = None
    yield
    reader._headers = None


async def test_shared_admin_token_cannot_read_chat_content(client):
    """配置类接口继续收共享 token，但看正文必须是具名账号。"""
    r = await client.get("/api/v1/admin/chats/conv-1", headers=ADMIN)
    assert r.status_code == 401


async def test_account_without_permission_is_refused(client):
    await make_user(client, username="it.li", password="flyknit-2026", can_read=False)
    token = (await login(client, "it.li"))["token"]
    headers = {"Authorization": f"Bearer {token}"}
    await client.post("/api/v1/admin/password", headers=headers,
                      json={"old_password": "flyknit-2026", "new_password": "flyknit-2027"})
    token = (await login(client, "it.li", "flyknit-2027"))["token"]

    r = await client.get("/api/v1/admin/chats/conv-1", headers={"Authorization": f"Bearer {token}"})
    assert r.status_code == 403
    assert "没有查看聊天内容的权限" in r.json()["detail"]


async def test_must_change_the_initial_password_before_reading(client):
    await make_user(client, username="it.wang")
    token = (await login(client, "it.wang"))["token"]
    r = await client.get("/api/v1/admin/chats/conv-1", headers={"Authorization": f"Bearer {token}"})
    assert r.status_code == 403
    assert "初始密码" in r.json()["detail"]


async def test_wrong_password_and_unknown_user_both_refused(client):
    await make_user(client)
    assert (await client.post("/api/v1/admin/login", json={"username": "it.zhang", "password": "nope"})).status_code == 401
    assert (await client.post("/api/v1/admin/login", json={"username": "ghost", "password": "nope"})).status_code == 401


@respx.mock
async def test_reading_content_is_recorded(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "好"}}]})
    )
    await send(client, device_headers)
    headers = await reader(client)

    # 只看列表不留痕
    await client.get("/api/v1/admin/chats", headers=headers)
    assert (await client.get("/api/v1/admin/chat-access", headers=ADMIN)).json() == []

    # 点开正文才留痕
    assert (await client.get("/api/v1/admin/chats/conv-1", headers=headers)).status_code == 200
    access = (await client.get("/api/v1/admin/chat-access", headers=ADMIN)).json()
    assert len(access) == 1
    assert access[0]["username"] == "it.zhang"
    assert access[0]["action"] == "read_chat"
    assert access[0]["target"] == "conv-1"


async def test_changing_password_invalidates_other_sessions(client):
    await make_user(client)
    first = (await login(client))["token"]
    headers = {"Authorization": f"Bearer {first}"}
    await client.post("/api/v1/admin/password", headers=headers,
                      json={"old_password": "flyknit-2026", "new_password": "flyknit-2027"})
    # 旧令牌作废
    assert (await client.get("/api/v1/admin/chats", headers=headers)).status_code == 401


async def test_duplicate_username_refused(client):
    await make_user(client)
    r = await client.post("/api/v1/admin/users", headers=ADMIN,
                          json={"username": "it.zhang", "password": "another-one"})
    assert r.status_code == 409


# ---------- 清理与备份 ----------

@respx.mock
async def test_purge_drops_records_past_the_window(client, device_headers):
    from app.db import get_sessionmaker
    from app.models import ChatRecord

    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "好"}}]})
    )
    await send(client, device_headers)

    async with get_sessionmaker()() as s:
        row = await s.scalar(sa.select(ChatRecord))
        row.created_at = datetime.now(timezone.utc) - timedelta(days=200)
        await s.commit()
        removed = await housekeeping.purge(s, {"chat_days": 90})

    assert removed["chat"] == 1
    assert (await client.get("/api/v1/admin/chats", headers=await reader(client))).json() == []
