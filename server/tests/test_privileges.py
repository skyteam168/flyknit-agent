"""管理端的权限分级。

这组测试是从实际打出来的洞倒推回来的：原先 require_admin 只验「有没有有效会话」，
不看任何权限位，于是一个只读账号可以给自己开下发权限、重置别人的密码、解掉某台
机器的工作区隔离——而且初始密码都还没改。

分三档：
  具名账号                     看自己、改自己的密码
  can_read_chats/can_dispatch  看聊天正文 / 下发运维任务（由超级管理员授予）
  is_owner                     建号改权限、改安全策略（能生出上面那两项，所以必须更难）
"""

import pytest
from conftest import ADMIN

from app import db
from app.models import AuditLog

pytestmark = pytest.mark.asyncio

INIT = "init-pass-123"


async def make(client, username, **flags):
    """建号。初始密码改掉之前，这个账号除了改密码什么也做不了。"""
    body = {"username": username, "password": INIT, "display_name": username}
    body.update(flags)
    r = await client.post("/api/v1/admin/users", headers=ADMIN, json=body)
    assert r.status_code == 201, r.text
    return r.json()


async def sign_in(client, username, password=INIT) -> dict:
    r = await client.post("/api/v1/admin/login", json={"username": username, "password": password})
    assert r.status_code == 200, r.text
    return {"Authorization": f"Bearer {r.json()['token']}"}


async def ready(client, username, **flags) -> tuple[dict, dict]:
    """建号 + 登录 + 改掉初始密码，拿到一个能正常干活的账号。"""
    user = await make(client, username, **flags)
    headers = await sign_in(client, username)
    r = await client.post("/api/v1/admin/password", headers=headers,
                          json={"old_password": INIT, "new_password": INIT + "-ok"})
    assert r.status_code == 204, r.text
    return user, await sign_in(client, username, INIT + "-ok")


# ---------- 自我提权 ----------

async def test_an_account_cannot_grant_itself_more_power(client):
    await ready(client, "owner")  # 第一个号自动是超级管理员
    me, headers = await ready(client, "clerk")
    assert me["can_read_chats"] is False and me["can_dispatch"] is False

    r = await client.patch(f"/api/v1/admin/users/{me['id']}", headers=headers,
                           json={"can_read_chats": True, "can_dispatch": True})
    assert r.status_code == 403, "普通账号不该能给自己提权"

    r = await client.patch(f"/api/v1/admin/users/{me['id']}", headers=headers, json={"is_owner": True})
    assert r.status_code == 403, "更不该能把自己提成超级管理员"


async def test_an_account_cannot_reset_someone_elses_password(client):
    boss, _ = await ready(client, "owner")
    _, headers = await ready(client, "clerk")
    r = await client.patch(f"/api/v1/admin/users/{boss['id']}", headers=headers,
                           json={"password": "i-own-you-now"})
    assert r.status_code == 403, "低权账号不该能重置别人的密码"


async def test_an_account_cannot_create_a_more_powerful_one(client):
    await ready(client, "owner")
    _, headers = await ready(client, "clerk")
    r = await client.post("/api/v1/admin/users", headers=headers,
                          json={"username": "puppet", "password": INIT, "can_dispatch": True})
    assert r.status_code == 403, "不能绕一圈——自己提不了权就造一个权限更大的号"


# ---------- 安全策略 ----------

async def test_only_an_owner_can_unlock_one_machine(client, device_headers):
    _, owner = await ready(client, "owner")
    _, clerk = await ready(client, "clerk", can_dispatch=True, can_read_chats=True)
    device_id = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()[0]["id"]
    body = {"overrides": {"sandbox": False}, "locks": {"sandbox": False}, "note": ""}

    # 能下发任务不等于能改这台机器的安全策略——后者严重得多
    r = await client.put(f"/api/v1/admin/devices/{device_id}/policy", headers=clerk, json=body)
    assert r.status_code == 403

    r = await client.put(f"/api/v1/admin/devices/{device_id}/policy", headers=owner, json=body)
    assert r.status_code == 200, r.text
    assert r.json()["effective"]["sandbox"]["value"] is False


async def test_only_an_owner_can_move_the_whole_factory(client):
    _, owner = await ready(client, "owner")
    _, clerk = await ready(client, "clerk", can_dispatch=True)
    body = {"values": {"delete_protection": False}, "locks": {}}

    assert (await client.put("/api/v1/admin/security", headers=clerk, json=body)).status_code == 403
    assert (await client.put("/api/v1/admin/security", headers=owner, json=body)).status_code == 200


# ---------- 初始密码这道闸 ----------

async def test_an_unchanged_initial_password_blocks_everything_but_changing_it(client):
    await ready(client, "owner")
    await make(client, "fresh", can_dispatch=True, can_read_chats=True)
    headers = await sign_in(client, "fresh")

    # 建号的人知道这个密码，所以在改掉之前，这个账号说不清是谁在用
    assert (await client.get("/api/v1/admin/providers", headers=headers)).status_code == 403
    # 但「我是谁」和「改密码」必须能用，否则密码永远改不掉
    assert (await client.get("/api/v1/admin/me", headers=headers)).status_code == 200
    r = await client.post("/api/v1/admin/password", headers=headers,
                          json={"old_password": INIT, "new_password": "now-its-mine-1"})
    assert r.status_code == 204

    headers = await sign_in(client, "fresh", "now-its-mine-1")
    assert (await client.get("/api/v1/admin/providers", headers=headers)).status_code == 200


# ---------- 别把自己锁在门外 ----------

async def test_the_last_owner_cannot_disappear(client):
    me, headers = await ready(client, "owner")
    assert me["is_owner"] is True, "第一个账号应当自动成为超级管理员"

    r = await client.patch(f"/api/v1/admin/users/{me['id']}", headers=headers, json={"disabled": True})
    assert r.status_code == 400
    r = await client.patch(f"/api/v1/admin/users/{me['id']}", headers=headers, json={"is_owner": False})
    assert r.status_code == 400
    # 共享令牌也不行：救急入口不该变成断电开关
    r = await client.patch(f"/api/v1/admin/users/{me['id']}", headers=ADMIN, json={"is_owner": False})
    assert r.status_code == 400


async def test_handing_over_works_once_there_is_a_second_owner(client):
    first, headers = await ready(client, "owner")
    second, _ = await ready(client, "successor")

    r = await client.patch(f"/api/v1/admin/users/{second['id']}", headers=headers, json={"is_owner": True})
    assert r.status_code == 200 and r.json()["is_owner"] is True
    # 有人接班了就可以退下来
    r = await client.patch(f"/api/v1/admin/users/{first['id']}", headers=ADMIN, json={"is_owner": False})
    assert r.status_code == 200 and r.json()["is_owner"] is False


async def test_the_shared_token_still_works_for_deployment(client):
    """共享 admin_token 是破窗入口：新部署时一个账号都没有，全靠它。"""
    r = await client.post("/api/v1/admin/users", headers=ADMIN,
                          json={"username": "it.yang", "password": INIT})
    assert r.status_code == 201
    assert r.json()["is_owner"] is True, "第一个账号自动是超级管理员"
    assert (await client.put("/api/v1/admin/security", headers=ADMIN,
                             json={"values": {}, "locks": {}})).status_code == 200


# ---------- 聊天内容不能从旁边的接口漏出去 ----------

SECRET_IN_ARGS = '{"path":"D:\\\\工资表\\\\2026年9月核定.xlsx","content":"组长 李某 18500"}'


async def seed_audit():
    async with db.get_sessionmaker()() as s:
        s.add(AuditLog(
            machine_name="PC-001", user_name="nguyen", conversation_id="conv-1",
            tool_name="write_file", arguments=SECRET_IN_ARGS,
            scene="agent", risk="confirm", decision="approved", status="ok",
            summary="写入 2026年9月核定.xlsx",
        ))
        await s.commit()


async def test_audit_arguments_are_not_a_side_door_into_chat_content(client):
    """
    审计表里存着工具调用的原始参数——写了什么文件、内容是什么、跑了什么命令。
    这就是聊天内容的一部分。正文接口把关很严（要授权、要留痕），审计这条路
    不能把同样的东西白送出去。
    """
    await ready(client, "owner")
    _, clerk = await ready(client, "clerk")      # 没有 can_read_chats
    _, reader = await ready(client, "reader", can_read_chats=True)
    await seed_audit()

    r = await client.get("/api/v1/admin/audit", headers=clerk)
    assert r.status_code == 200
    rows = r.json()
    assert len(rows) == 1, "这条测试要有数据才算数"
    assert "工资表" not in r.text, "没有查看聊天权限的账号不该看到工具调用的原始参数"
    # 但该看到的要看到：什么时候、哪台机器、用了什么工具、判定如何
    assert rows[0]["tool_name"] == "write_file" and rows[0]["decision"] == "approved"
    assert rows[0]["machine_name"] == "PC-001"

    # 有权限的账号照常看到全文
    r = await client.get("/api/v1/admin/audit", headers=reader)
    assert "工资表" in r.text


async def test_audit_export_does_not_leak_either(client):
    await ready(client, "owner")
    _, clerk = await ready(client, "clerk")
    await seed_audit()
    r = await client.get("/api/v1/admin/audit/export", headers=clerk)
    assert r.status_code == 200
    assert "工资表" not in r.text, "导出 CSV 是同一条内容，不能绕过权限"
