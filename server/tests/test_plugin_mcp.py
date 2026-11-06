"""插件 MCP：四种写法、路径安全、官方插件真实结构、同名（新建 / 复用 / 冲突 / 改名）、安全检查、来源追溯、老连接器兼容。"""

import io
import json
import stat
import zipfile
from pathlib import Path

import pytest

from app.services import plugin_mcp_resolver as r
from conftest import ADMIN

FIXTURES = Path(__file__).parent / "fixtures" / "claude_plugins"
PW = {"playwright": {"command": "npx", "args": ["@playwright/mcp@latest"]}}


def resolve(files: dict[str, str], manifest: dict | None = None):
    manifest = manifest or {"name": "demo"}
    return r.resolve(files, lambda rel: files[rel], manifest, ".claude-plugin/plugin.json", "demo", "1.0.0")


def make_zip(files: dict[str, str], root: str = "plug") -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, text in files.items():
            z.writestr(f"{root}/{name}", text)
    return buf.getvalue()


def fixture_zip(name: str) -> bytes:
    base = FIXTURES / name
    return make_zip({str(p.relative_to(base)).replace("\\", "/"): p.read_text("utf-8")
                     for p in base.rglob("*") if p.is_file()}, root=name)


def plugin(mcp: dict | str, extra: dict | None = None, name: str = "demo", mcp_file: str = ".mcp.json") -> bytes:
    manifest = {"name": name, "version": "1.0.0"}
    files = {".claude-plugin/plugin.json": json.dumps(manifest)}
    files[mcp_file] = mcp if isinstance(mcp, str) else json.dumps(mcp)
    files.update(extra or {})
    return make_zip(files)


async def upload(client, data: bytes, **params):
    return await client.post("/api/v1/admin/plugins/upload", headers=ADMIN, params=params,
                             files={"file": ("p.zip", data, "application/zip")})


async def preview(client, data: bytes, **params):
    return await client.post("/api/v1/admin/plugins/preview", headers=ADMIN, params=params,
                             files={"file": ("p.zip", data, "application/zip")})


# ---------- 四种写法 ----------
def test_format_a_server_names_at_top_level():
    defs, _ = resolve({".mcp.json": json.dumps(PW)})
    assert [(d.name, d.transport_type, d.command, d.args) for d in defs] == [("playwright", "stdio", "npx", ["@playwright/mcp@latest"])]
    assert defs[0].source_mcp_config == ".mcp.json" and defs[0].source_server_name == "playwright"
    assert any("没有固定版本" in w for w in defs[0].warnings)  # @latest：不拒收，提示固定版本


def test_format_b_mcp_servers_object():
    defs, _ = resolve({".mcp.json": json.dumps({"mcpServers": {"tools": {"command": "uvx", "args": ["mcp-tools==1.2.0"]}}})})
    assert [(d.name, d.command) for d in defs] == [("tools", "uvx")] and defs[0].warnings == []


def test_format_c_manifest_points_to_a_file_inside_the_package():
    files = {"mcp.claude-code.json": json.dumps({"postman": {"type": "http", "url": "https://mcp.postman.com/mcp"}})}
    defs, _ = resolve(files, {"name": "demo", "mcpServers": "./mcp.claude-code.json"})
    assert [(d.name, d.transport_type, d.source_mcp_config) for d in defs] == [("postman", "http", "mcp.claude-code.json")]
    # 内联对象也认
    defs, _ = resolve({}, {"name": "demo", "mcpServers": {"x-api": {"type": "http", "url": "https://x.example.com/mcp"}}})
    assert defs[0].source_mcp_config == ".claude-plugin/plugin.json#mcpServers"


def test_format_d_http_at_top_level():
    defs, _ = resolve({".mcp.json": json.dumps({"figma": {"type": "http", "url": "https://mcp.figma.com/mcp"}})})
    assert [(d.name, d.transport_type, d.url) for d in defs] == [("figma", "http", "https://mcp.figma.com/mcp")]


def test_multiple_sources_merge_and_conflicts_are_not_silent():
    same = {"mcpServers": {"playwright": PW["playwright"]}}
    defs, notes = resolve({".mcp.json": json.dumps(PW)}, {"name": "demo", "mcpServers": same["mcpServers"]})
    assert len(defs) == 1 and any("重复声明" in n for n in notes)
    other = {"playwright": {"command": "npx", "args": ["@playwright/mcp@0.0.30"]}}
    with pytest.raises(r.PluginMcpError, match="配置不一样"):
        resolve({".mcp.json": json.dumps(other)}, {"name": "demo", "mcpServers": PW})


# ---------- 路径安全 ----------
@pytest.mark.parametrize("path", ["../x.json", "./a/../../x.json", "C:\\x.json", "C:/x.json", "/etc/x.json",
                                  "\\\\server\\share\\x.json", "//server/share/x.json", ""])
def test_manifest_path_must_stay_inside_the_package(path):
    with pytest.raises(r.PluginMcpError):
        resolve({"x.json": "{}"}, {"name": "demo", "mcpServers": path})


def test_missing_referenced_file_is_an_error_not_silent():
    with pytest.raises(r.PluginMcpError, match="没有这个文件"):
        resolve({}, {"name": "demo", "mcpServers": "./mcp.claude-code.json"})


async def test_symlink_entry_is_read_as_text_never_followed(client):
    """zip 里的符号链接只按名字读到一段文字，不会去读包外的文件：解析失败、整包拒收。"""
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("plug/.claude-plugin/plugin.json", json.dumps({"name": "linky"}))
        info = zipfile.ZipInfo("plug/.mcp.json")
        info.external_attr = (stat.S_IFLNK | 0o777) << 16
        z.writestr(info, "../../../../etc/passwd")
    resp = await upload(client, buf.getvalue())
    assert resp.status_code == 400 and "JSON" in resp.text


# ---------- 安全检查 ----------
@pytest.mark.parametrize("command", ["cmd", "cmd.exe", "powershell", "pwsh", "bash", "sh", "curl", "wget", "docker",
                                     "C:\\Windows\\System32\\cmd.exe"])
def test_only_known_launchers_are_allowed(command):
    with pytest.raises(r.PluginMcpError, match="不在允许范围"):
        resolve({".mcp.json": json.dumps({"x": {"command": command, "args": ["-c", "echo"]}})})


@pytest.mark.parametrize("key", ["PATH", "Path", "PATHEXT", "COMSPEC", "SYSTEMROOT", "NODE_OPTIONS", "npm_config_registry",
                                 "PYTHONPATH", "PYTHONSTARTUP", "DOTNET_STARTUP_HOOKS", "HTTPS_PROXY", "http_proxy"])
def test_plugin_env_cannot_touch_protected_variables(key):
    with pytest.raises(r.PluginMcpError, match="env 不能设置"):
        resolve({".mcp.json": json.dumps({"x": {"command": "npx", "args": ["a@1.0.0"], "env": {key: "evil"}}})})


def test_plugin_root_programs_are_refused_and_hardcoded_secrets_are_dropped():
    with pytest.raises(r.PluginMcpError, match="CLAUDE_PLUGIN_ROOT"):
        resolve({".mcp.json": json.dumps({"x": {"command": "node", "args": ["${CLAUDE_PLUGIN_ROOT}/s.js"]}})})
    defs, _ = resolve({".mcp.json": json.dumps({"api": {"type": "http", "url": "https://a.example.com/mcp",
                                                         "headers": {"Authorization": "Bearer sk-live-123"}}})})
    assert "sk-live-123" not in json.dumps(defs[0].to_dict())  # 写死的密钥不随包走
    assert any("密钥没有导入" in w for w in defs[0].warnings)


# ---------- 官方插件的真实结构 ----------
async def test_official_playwright_figma_postman_import(client, device_headers):
    for name in ("playwright", "figma", "postman"):
        resp = await upload(client, fixture_zip(name))
        assert resp.status_code == 200, (name, resp.text)
        conn = resp.json()["manifest"]["connectors"]
        assert [(c["server"], c["result"]) for c in conn] == [(name, "NEW")], name

    vendors = {v["id"]: v for v in (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json()}
    pw, fg, pm = vendors["playwright"], vendors["figma"], vendors["postman"]
    assert (pw["transport"], pw["command"], pw["args"]) == ("stdio", "npx", ["@playwright/mcp@latest"])
    assert (pw["source_type"], pw["plugin"], pw["source_mcp_config"], pw["source_server_name"]) == \
        ("plugin", "playwright", ".mcp.json", "playwright")
    assert pw["source_plugin_version"] == "unknown"  # 官方 playwright 插件没写 version
    assert (fg["transport"], fg["url"], fg["source_plugin_version"]) == ("http", "https://mcp.figma.com/mcp", "2.2.157")
    assert fg["headers"]["X-Figma-Plugin-Bundle"] == "figma_prod@2_2_157"
    assert (pm["url"], pm["source_mcp_config"], pm["source_manifest"]) == \
        ("https://mcp.postman.com/mcp", "mcp.claude-code.json", ".claude-plugin/plugin.json")

    plugins = {p["id"]: p for p in (await client.get("/api/v1/admin/plugins", headers=ADMIN)).json()["items"]}
    assert any("没有固定版本" in w for w in plugins["playwright"]["manifest"]["warnings"])


# ---------- 同名 ----------
async def _global_vendor(client, **conf):
    body = {"id": "playwright", "name": "Playwright（IT）", "transport": "stdio", "command": "npx",
            "args": ["@playwright/mcp@latest"], **conf}
    resp = await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN, json=body)
    assert resp.status_code in (200, 201), resp.text


async def test_same_config_reuses_the_global_connector(client, device_headers):
    await _global_vendor(client)
    resp = await upload(client, fixture_zip("playwright"))
    assert resp.status_code == 200, resp.text
    [c] = resp.json()["manifest"]["connectors"]
    assert (c["result"], c["vendor_id"]) == ("REUSE_EXISTING", "playwright")
    vendors = [v for v in (await client.get("/api/v1/admin/mcp/vendors", headers=ADMIN)).json() if "playwright" in v["id"]]
    assert [(v["id"], v["source_type"], v["plugin"]) for v in vendors] == [("playwright", "global", "")]
    # 卸载插件：复用的全局连接器不删
    assert (await client.delete("/api/v1/admin/plugins/playwright", headers=ADMIN)).status_code == 204
    assert any(v["id"] == "playwright" for v in (await client.get("/api/v1/admin/mcp/vendors", headers=ADMIN)).json())


async def test_different_config_conflicts_then_rename(client, device_headers):
    await _global_vendor(client, args=["@playwright/mcp@0.0.30", "--headless"])
    pre = (await preview(client, fixture_zip("playwright"))).json()
    [c] = pre["manifest"]["connectors"]
    assert c["result"] == "CONFLICT" and c["suggested_rename"] == "playwright-playwright"
    assert any("连接器 playwright" in x for x in pre["conflicts"])

    bad = await upload(client, fixture_zip("playwright"))
    assert bad.status_code == 409 and "already exists with different configuration" in bad.text

    ok = await upload(client, fixture_zip("playwright"), rename="playwright=pw-playwright")
    assert ok.status_code == 200, ok.text
    [c] = ok.json()["manifest"]["connectors"]
    assert (c["result"], c["vendor_id"]) == ("RENAMED", "pw-playwright")
    vendors = {v["id"]: v for v in (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json()}
    assert vendors["playwright"]["args"] == ["@playwright/mcp@0.0.30", "--headless"]  # 全局的没被动
    assert vendors["playwright"]["source_type"] == "global"
    assert (vendors["pw-playwright"]["source_type"], vendors["pw-playwright"]["source_server_name"]) == ("plugin", "playwright")

    # 改成一个也被占用的名字：仍然冲突
    taken = await upload(client, fixture_zip("playwright"), rename="playwright=playwright")
    assert taken.status_code == 409


async def test_upgrade_updates_own_connector_and_another_plugins_name_conflicts(client):
    assert (await upload(client, plugin(PW, name="pw-one"))).status_code == 200
    again = await upload(client, plugin({"playwright": {"command": "npx", "args": ["@playwright/mcp@0.0.31"]}}, name="pw-one"))
    assert again.status_code == 200 and again.json()["manifest"]["connectors"][0]["result"] == "UPDATE"
    # 别的插件同名、配置一样也不复用（复用只对管理员手工建的：插件卸载时会删自己的连接器）
    other = await upload(client, plugin({"playwright": {"command": "npx", "args": ["@playwright/mcp@0.0.31"]}}, name="pw-two"))
    assert other.status_code == 409


# ---------- 来源追溯 ----------
async def test_audit_records_where_each_mcp_tool_came_from(client, device_headers):
    await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN,
                      json={"id": "internal-db", "name": "内部库", "transport": "http", "url": "https://db.example.com/mcp"})
    assert (await upload(client, fixture_zip("figma"))).status_code == 200
    items = [
        {"tool_name": "mcp__figma__get_screenshot", "risk": "auto", "decision": "auto"},
        {"tool_name": "mcp__internal-db__query", "risk": "auto", "decision": "auto"},
        {"tool_name": "mcp__gone__x", "risk": "auto", "decision": "auto"},
        {"tool_name": "read_file", "risk": "auto", "decision": "auto"},
    ]
    assert (await client.post("/api/v1/audit", headers=device_headers, json={"items": items})).status_code == 204
    rows = {a["tool_name"]: a["mcp_source"] for a in (await client.get("/api/v1/admin/audit", headers=ADMIN)).json()}
    assert rows == {"mcp__figma__get_screenshot": "plugin:figma@2.2.157", "mcp__internal-db__query": "global",
                    "mcp__gone__x": "unknown", "read_file": ""}


# ---------- 兼容 ----------
async def test_global_connectors_keep_working_and_admin_paste_accepts_top_level_names(client, device_headers):
    await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN,
                      json={"id": "internal-db", "name": "内部库", "transport": "http", "url": "https://db.example.com/mcp"})
    [v] = [x for x in (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json() if x["id"] == "internal-db"]
    assert (v["source_type"], v["plugin"], v["source_plugin_version"]) == ("global", "", "")
    # 后台「连接器」粘贴配置：官方 playwright 那种顶层直接写服务名的也认
    resp = await client.post("/api/v1/admin/mcp/parse", headers=ADMIN, json={"text": json.dumps(PW)})
    assert resp.status_code == 200, resp.text
    assert [(d["id"], d["transport"]) for d in resp.json()] == [("playwright", "stdio")]
