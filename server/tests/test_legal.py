"""用户协议与隐私政策：登录界面不登录也能看，管理员能改、能恢复默认，登录时记下同意的是哪一版。"""

import pytest
from conftest import ADMIN
from test_client_package import package, register, released
from test_releases import owner_headers

pytestmark = pytest.mark.asyncio


async def test_the_login_screen_can_read_both_without_signing_in(client):
    r = await client.get("/api/v1/legal")
    assert r.status_code == 200
    body = r.json()
    docs = {d["kind"]: d for d in body["docs"]}
    assert docs["terms"]["title"] == "用户协议" and docs["privacy"]["title"] == "隐私政策"
    assert "FlyknitBuddy 用户协议" in docs["terms"]["content"]
    assert "不会被保存，也不会上传" in docs["privacy"]["content"]      # 默认文本说清楚了密码的处理
    assert docs["terms"]["customized"] is False
    assert body["versions"].startswith("terms@default-")

    assert (await client.get("/api/v1/legal/privacy")).json()["kind"] == "privacy"
    assert (await client.get("/api/v1/legal/other")).status_code == 404


async def test_owners_can_edit_and_reset(client):
    headers = await owner_headers(client)
    before = (await client.get("/api/v1/legal")).json()["versions"]

    r = await client.put("/api/v1/admin/legal/terms", headers=headers, json={"content": "# 用户协议\n\n本公司员工使用本服务须遵守公司制度。"})
    assert r.status_code == 200, r.text
    assert r.json()["customized"] is True and r.json()["updated_by"] == "it.yang"
    assert "须遵守公司制度" in (await client.get("/api/v1/legal/terms")).json()["content"]
    after = (await client.get("/api/v1/legal")).json()["versions"]
    assert after != before                                            # 改了就是新版本

    r = await client.delete("/api/v1/admin/legal/terms", headers=headers)
    assert r.json()["customized"] is False
    assert "FlyknitBuddy 用户协议" in r.json()["content"]

    assert (await client.put("/api/v1/admin/legal/terms", headers=headers, json={"content": "太短"})).status_code == 422


async def test_only_owners_can_edit(client):
    await owner_headers(client)
    await client.post("/api/v1/admin/users", headers=ADMIN, json={"username": "clerk", "password": "init-pass-123"})
    r = await client.post("/api/v1/admin/login", json={"username": "clerk", "password": "init-pass-123"})
    clerk = {"Authorization": f"Bearer {r.json()['token']}"}
    await client.post("/api/v1/admin/password", headers=clerk, json={"old_password": "init-pass-123", "new_password": "init-pass-123-ok"})
    r = await client.post("/api/v1/admin/login", json={"username": "clerk", "password": "init-pass-123-ok"})
    clerk = {"Authorization": f"Bearer {r.json()['token']}"}
    assert (await client.get("/api/v1/admin/legal/terms", headers=clerk)).status_code == 200
    r = await client.put("/api/v1/admin/legal/terms", headers=clerk, json={"content": "# 用户协议\n\n随便改改随便改改随便改改"})
    assert r.status_code == 403


async def test_signing_in_records_which_version_was_agreed(client):
    headers = await owner_headers(client)
    await released(client, headers)
    ticket = (await package(client, headers))["provision"]["ticket"]

    # 没勾同意（没带版本）：不让登录
    r = await register(client, ticket, agreed_legal="")
    assert r.status_code == 409

    r = await register(client, ticket)
    assert r.status_code == 200
    versions = (await client.get("/api/v1/legal")).json()["versions"]
    device = next(d for d in (await client.get("/api/v1/admin/devices", headers=ADMIN)).json() if d["id"] == r.json()["device_id"])
    assert device["legal_agreed"] == versions

    # 管理员改了协议：界面上看的还是旧版，要重新看过再同意
    await client.put("/api/v1/admin/legal/privacy", headers=headers, json={"content": "# 隐私政策\n\n新版本的隐私政策内容在这里。"})
    stale = await register(client, ticket, agreed_legal=versions)
    assert stale.status_code == 409
    assert "已更新" in stale.json()["detail"]
    assert (await register(client, ticket)).status_code == 200     # 拿新版本同意就行
