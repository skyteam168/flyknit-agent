"""MCP 连接器：后台引入厂商、员工端列出、配置导入、试连。"""

import json

import httpx
import pytest
import respx

from app.services import mcp_catalog

ADMIN = {"Authorization": "Bearer test-admin"}

TENCENT_DOCS = {
    "id": "tencent-docs",
    "name": "腾讯文档",
    "description": "创建、编辑和协作腾讯文档。",
    "detail": "用自然语言管理在线表格、文档和幻灯片。",
    "publisher": "腾讯",
    "transport": "http",
    "url": "https://docs.qq.com/openapi/mcp",
    "headers": {"Authorization": "Bearer ${API_KEY}"},
    "auth": "fields",
    "fields": [{"key": "API_KEY", "label": "API Key", "secret": True, "required": True}],
    "examples": ["帮我新建一个在线表格", "  ", "总结我最近编辑的文档"],
}


async def create(client, **overrides):
    body = {**TENCENT_DOCS, **overrides}
    r = await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN, json=body)
    assert r.status_code == 201, r.text
    return r.json()


# ---------- 后台 ----------
async def test_admin_adds_a_vendor_and_staff_see_it(client, device_headers):
    out = await create(client)
    assert out["examples"] == ["帮我新建一个在线表格", "总结我最近编辑的文档"]  # 空行去掉

    r = await client.get("/api/v1/client/mcp/vendors", headers=device_headers)
    assert r.status_code == 200
    [vendor] = r.json()
    assert vendor["id"] == "tencent-docs"
    assert vendor["headers"] == {"Authorization": "Bearer ${API_KEY}"}
    # 没有预填，员工得自己填 API Key
    assert vendor["needs_input"] == ["API_KEY"]


async def test_preset_values_are_encrypted_masked_for_admins_and_plain_for_devices(client, device_headers):
    await create(client, preset={"API_KEY": "tdoc-company-secret-9876"})

    admin_view = (await client.get("/api/v1/admin/mcp/vendors", headers=ADMIN)).json()[0]
    assert admin_view["preset_masked"] == {"API_KEY": "tdo****9876"}
    assert "tdoc-company-secret-9876" not in json.dumps(admin_view)

    device_view = (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json()[0]
    assert device_view["preset"] == {"API_KEY": "tdoc-company-secret-9876"}
    assert device_view["needs_input"] == []  # 管理员填好了，员工点一下就能连


async def test_updating_without_preset_keeps_it_and_empty_value_clears_it(client, device_headers):
    await create(client, preset={"API_KEY": "tdoc-company-secret-9876"})
    body = {**TENCENT_DOCS, "description": "改了介绍"}
    r = await client.put("/api/v1/admin/mcp/vendors/tencent-docs", headers=ADMIN, json=body)
    assert r.status_code == 200, r.text
    assert r.json()["preset_masked"] == {"API_KEY": "tdo****9876"}

    r = await client.put("/api/v1/admin/mcp/vendors/tencent-docs", headers=ADMIN, json={**body, "preset": {"API_KEY": ""}})
    assert r.json()["preset_masked"] == {}
    device_view = (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json()[0]
    assert device_view["needs_input"] == ["API_KEY"]


async def test_disabled_vendors_are_hidden_from_devices(client, device_headers):
    await create(client)
    await create(client, id="wecom", name="企业微信", url="https://wecom.example.com/mcp", headers={}, fields=[], auth="none")
    r = await client.patch("/api/v1/admin/mcp/vendors/wecom", headers=ADMIN, json={"enabled": False})
    assert r.status_code == 200
    ids = [v["id"] for v in (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json()]
    assert ids == ["tencent-docs"]


async def test_changes_wake_up_waiting_clients(client):
    from app.services import config_events

    before = config_events.current()
    await create(client)
    assert config_events.current() > before


@pytest.mark.parametrize(
    ("overrides", "message"),
    [
        ({"id": "Tencent Docs"}, "标识只能用"),
        ({"url": "docs.qq.com/mcp"}, "http:// 或 https://"),
        ({"transport": "stdio", "url": "", "command": ""}, "启动命令"),
        ({"headers": {"Authorization": "Bearer ${TOKEN}"}}, "${TOKEN}"),
        ({"icon": "javascript:alert(1)"}, "图标"),
        ({"transport": "stdio", "command": "npx", "auth": "oauth"}, "不支持浏览器授权"),
        ({"fields": [{"key": "API_KEY"}, {"key": "API_KEY"}]}, "重复"),
    ],
)
async def test_mistakes_are_caught_before_they_reach_staff(client, overrides, message):
    r = await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN, json={**TENCENT_DOCS, **overrides})
    assert r.status_code == 400
    assert message in r.json()["detail"]


async def test_placeholder_with_default_needs_no_field(client):
    await create(client, url="${BASE:-https://docs.qq.com}/mcp", headers={}, fields=[], auth="none")


async def test_ids_are_unique_and_cannot_be_renamed(client):
    await create(client)
    r = await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN, json=TENCENT_DOCS)
    assert r.status_code == 409
    r = await client.put("/api/v1/admin/mcp/vendors/tencent-docs", headers=ADMIN, json={**TENCENT_DOCS, "id": "docs"})
    assert r.status_code == 400


async def test_devices_cannot_use_admin_endpoints_and_strangers_see_nothing(client, device_headers):
    r = await client.get("/api/v1/admin/mcp/vendors", headers=device_headers)
    assert r.status_code in (401, 403)
    r = await client.get("/api/v1/client/mcp/vendors")
    assert r.status_code == 401


async def test_delete(client, device_headers):
    await create(client)
    r = await client.delete("/api/v1/admin/mcp/vendors/tencent-docs", headers=ADMIN)
    assert r.status_code == 204
    assert (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json() == []


# ---------- 导入 ----------
def test_imports_claude_style_config_and_lifts_hardcoded_secrets():
    text = json.dumps({
        "mcpServers": {
            "Tencent Docs": {
                "type": "http",
                "url": "https://docs.qq.com/openapi/mcp",
                "headers": {"Authorization": "Bearer tdoc-abc123"},
            },
            "filesystem": {
                "command": "npx",
                "args": ["-y", "@modelcontextprotocol/server-filesystem", "${ROOT}"],
                "env": {"GITHUB_TOKEN": "ghp_xyz", "LOG_LEVEL": "info"},
            },
        }
    })
    docs, fs = mcp_catalog.parse_import(text)

    assert docs.id == "tencent-docs"
    assert docs.transport == "http"
    assert docs.headers == {"Authorization": "Bearer ${API_KEY}"}
    assert docs.preset == {"API_KEY": "tdoc-abc123"}
    assert docs.auth == "fields"

    assert fs.transport == "stdio"
    assert fs.command == "npx"
    assert fs.env == {"GITHUB_TOKEN": "${GITHUB_TOKEN}", "LOG_LEVEL": "info"}
    assert fs.preset == {"GITHUB_TOKEN": "ghp_xyz"}
    keys = {f["key"]: f["secret"] for f in fs.fields}
    assert keys == {"ROOT": False, "GITHUB_TOKEN": True}


def test_imports_claude_mcp_add_command():
    [d] = mcp_catalog.parse_import(
        'claude mcp add --transport http notion https://mcp.notion.com/mcp --header "X-Api-Key: secret-1"'
    )
    assert (d.id, d.transport, d.url) == ("notion", "http", "https://mcp.notion.com/mcp")
    assert d.headers == {"X-Api-Key": "${API_KEY}"}
    assert d.preset == {"API_KEY": "secret-1"}

    [s] = mcp_catalog.parse_import("claude mcp add db -e DB_URL=postgres://x -- npx -y @bytebase/dbhub")
    assert (s.transport, s.command, s.args) == ("stdio", "npx", ["-y", "@bytebase/dbhub"])


def test_import_rejects_garbage():
    for text in ("", "not json", "[]", '{"foo": 1}', '{"mcpServers": {"x": {"type": "ws", "url": "wss://a"}}}'):
        with pytest.raises(mcp_catalog.McpConfigError):
            mcp_catalog.parse_import(text)


async def test_parse_endpoint_marks_existing_ids(client):
    await create(client)
    r = await client.post("/api/v1/admin/mcp/parse", headers=ADMIN, json={
        "text": json.dumps({"mcpServers": {"tencent-docs": {"url": "https://docs.qq.com/openapi/mcp"}}})
    })
    assert r.status_code == 200
    assert r.json()[0]["exists"] is True


# ---------- 试连 ----------
def initialize_reply(request):
    body = json.loads(request.content)
    if body["method"] == "initialize":
        assert request.headers["authorization"] == "Bearer tdoc-company-secret-9876"
        return httpx.Response(200, json={
            "jsonrpc": "2.0", "id": body["id"],
            "result": {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}},
                       "serverInfo": {"name": "tencent-docs", "version": "1.4.0"}},
        }, headers={"Mcp-Session-Id": "s-1"})
    if "id" not in body:
        return httpx.Response(202)
    assert request.headers["mcp-session-id"] == "s-1"
    assert request.headers["mcp-protocol-version"] == "2025-06-18"
    # 工具列表用 SSE 回，确认两种回法都认
    event = json.dumps({"jsonrpc": "2.0", "id": body["id"], "result": {"tools": [
        {"name": "create_sheet", "description": "新建在线表格"},
        {"name": "read_doc", "description": "读取文档内容"},
    ]}})
    return httpx.Response(200, text=f"event: message\ndata: {event}\n\n", headers={"Content-Type": "text/event-stream"})


@respx.mock
async def test_admin_can_test_a_connection(client):
    respx.post("https://docs.qq.com/openapi/mcp").mock(side_effect=initialize_reply)
    await create(client, preset={"API_KEY": "tdoc-company-secret-9876"})

    r = await client.post("/api/v1/admin/mcp/vendors/tencent-docs/test", headers=ADMIN, json={})
    assert r.status_code == 200, r.text
    out = r.json()
    assert out["ok"] is True, out
    assert out["server_name"] == "tencent-docs"
    assert [t["name"] for t in out["tools"]] == ["create_sheet", "read_doc"]


@respx.mock
async def test_connection_test_explains_a_wrong_key(client):
    respx.post("https://docs.qq.com/openapi/mcp").mock(return_value=httpx.Response(401))
    await create(client)
    r = await client.post("/api/v1/admin/mcp/vendors/tencent-docs/test", headers=ADMIN, json={"values": {"API_KEY": "wrong"}})
    out = r.json()
    assert out["ok"] is False
    assert "密钥不对" in out["error"]


async def test_connection_test_asks_for_missing_values_and_skips_local_processes(client):
    await create(client)
    out = (await client.post("/api/v1/admin/mcp/vendors/tencent-docs/test", headers=ADMIN, json={})).json()
    assert out["ok"] is False and "API_KEY" in out["error"]

    await create(client, id="local-fs", transport="stdio", url="", command="npx", headers={}, fields=[], auth="none")
    out = (await client.post("/api/v1/admin/mcp/vendors/local-fs/test", headers=ADMIN, json={})).json()
    assert out["ok"] is False and "员工电脑" in out["error"]


def test_sse_parsing_joins_multiline_data():
    assert mcp_catalog.sse_data("data: a\ndata: b\n\nevent: x\ndata:c\n\n") == ["a\nb", "c"]
