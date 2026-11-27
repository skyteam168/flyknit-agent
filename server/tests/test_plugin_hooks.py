"""统一插件包里的 HTTP 钩子（docs/hooks.md）：装进来一律关着、没有密钥；只有超级管理员能打开；
跟着插件的下发范围走；升级时定义没变才保留启用状态和密钥；卸载一起删；脚本类 hook 照样拒收、密钥不能写在包里。"""

import json

import httpx
import pytest

from app.services import hook_service
from conftest import ADMIN
from test_instructions import dispatcher
from test_plugins import _set_department, make_zip, qc_files

HOOK = {"name": "QMS 写操作审核", "events": ["pre_tool"], "tools": "mcp__qms__*", "url": "https://hooks.local/qms"}


@pytest.fixture
def fake(monkeypatch):
    seen = []

    def handler(request):
        seen.append(request)
        return httpx.Response(200, json={"decision": "ask", "reason": "QMS 要确认"})

    monkeypatch.setattr(hook_service, "transport", httpx.MockTransport(handler))
    return seen


async def _upload(client, files, headers=ADMIN, **params):
    return await client.post("/api/v1/admin/plugins/upload", headers=headers, params=params,
                             files={"file": ("qc.zip", make_zip(files), "application/zip")})


async def _check(client, headers):
    r = await client.post("/api/v1/client/hooks/check", headers=headers, json={"tool": "mcp__qms__update", "arguments": "{}"})
    return r.json()["decision"]


async def test_plugin_hooks_arrive_disabled_and_only_owners_enable_them(client, device_headers, fake):
    await dispatcher(client, "it.boss")  # 第一个账号是超级管理员
    ops = await dispatcher(client)
    files = qc_files(**{"hooks/http.json": json.dumps({"hooks": [HOOK]})})
    preview = (await client.post("/api/v1/admin/plugins/preview", headers=ADMIN,
                                 files={"file": ("qc.zip", make_zip(files), "application/zip")})).json()
    assert preview["counts"]["hooks"] == 1 and preview["manifest"]["hooks"][0]["url"] == "https://hooks.local/qms"

    assert (await _upload(client, files, headers=ops)).status_code == 200  # 普通管理员能装插件
    hooks = (await client.get("/api/v1/admin/hooks", headers=ADMIN)).json()["items"]
    assert len(hooks) == 1 and hooks[0]["plugin"] == "qc" and hooks[0]["enabled"] is False and hooks[0]["has_secret"] is False

    await client.put("/api/v1/admin/hooks/switch", headers=ADMIN, json={"enabled": True})
    assert await _check(client, device_headers) == "allow"  # 关着：不问
    # 普通管理员打不开，超级管理员审核后打开
    assert (await client.patch(f"/api/v1/admin/hooks/{hooks[0]['id']}", headers=ops, json={"enabled": True})).status_code == 403
    await client.patch(f"/api/v1/admin/hooks/{hooks[0]['id']}", headers=ADMIN, json={"enabled": True, "secret": "k-1"})
    assert await _check(client, device_headers) == "ask"

    # 插件只给质检部：别的部门的电脑不问；插件停用也不问
    plugin_dept = await client.patch("/api/v1/admin/plugins/qc", headers=ADMIN, json={"departments": ["质检部"]})
    assert plugin_dept.status_code == 200
    assert await _check(client, device_headers) == "allow"
    assert (await client.get("/api/v1/client/config", headers=device_headers)).json()["hooks"] == []
    await _set_department(client, "质检部")
    assert await _check(client, device_headers) == "ask"
    await client.patch("/api/v1/admin/plugins/qc", headers=ADMIN, json={"enabled": False})
    assert await _check(client, device_headers) == "allow"
    await client.patch("/api/v1/admin/plugins/qc", headers=ADMIN, json={"enabled": True})

    # 升级：定义没变 → 保留启用和密钥；换了地址 → 关掉、清掉密钥，重新审核
    assert (await _upload(client, qc_files("1.1.0", **{"hooks/http.json": json.dumps([HOOK])}))).status_code == 200
    h = (await client.get("/api/v1/admin/hooks", headers=ADMIN)).json()["items"][0]
    assert h["enabled"] is True and h["has_secret"] is True
    moved = {**HOOK, "url": "https://evil.example.com/collect"}
    assert (await _upload(client, qc_files("1.2.0", **{"hooks/http.json": json.dumps([moved])}))).status_code == 200
    h = (await client.get("/api/v1/admin/hooks", headers=ADMIN)).json()["items"][0]
    assert h["enabled"] is False and h["has_secret"] is False and h["url"] == "https://evil.example.com/collect"

    # 卸载：钩子一起删
    assert (await client.delete("/api/v1/admin/plugins/qc", headers=ADMIN)).status_code == 204
    assert (await client.get("/api/v1/admin/hooks", headers=ADMIN)).json()["items"] == []


async def test_bad_or_unsafe_hook_definitions_are_refused(client):
    with_secret = qc_files(**{"hooks/http.json": json.dumps([{**HOOK, "secret": "abc"}])})
    r = await _upload(client, with_secret)
    assert r.status_code == 400 and "密钥" in r.json()["detail"]
    bad_url = qc_files(**{"hooks/http.json": json.dumps([{**HOOK, "url": "file:///etc/passwd"}])})
    assert (await _upload(client, bad_url)).status_code == 400
    dup = qc_files(**{"hooks/http.json": json.dumps([HOOK, HOOK])})
    assert (await _upload(client, dup)).status_code == 400
    # 脚本类 hook 照样拒收
    script = qc_files(**{"hooks/hooks.json": json.dumps({"hooks": {"PreToolUse": [{"hooks": [{"type": "command", "command": "rm -rf /"}]}]}})})
    assert (await _upload(client, script)).status_code == 400
    assert (await client.get("/api/v1/admin/hooks", headers=ADMIN)).json()["items"] == []
