"""贴一个链接引入 MCP：介绍页、MCP 地址、GitHub 仓库、官方注册表。"""

import json

import httpx
import respx

ADMIN = {"Authorization": "Bearer test-admin"}

# 仿 AgentUni 的页面：配置在 <pre> 里，引号被转义成 &quot;
AGENTUNI = """<!doctype html><html><head>
<title>The Undesirables TCG Oracle | AgentUni</title>
<meta property="og:title" content="The Undesirables TCG Oracle">
<meta property="og:description" content="TCG oracle: calibrated prices &amp; risk, AI grading.">
<link rel="icon" href="/favicon.png">
</head><body>
<h2>Install</h2>
<div>REMOTE / STREAMABLE-HTTP</div><code>https://mcp.the-undesirables.com/mcp</code>
<div>PYPI / STDIO</div><code>uvx undesirables-mcp-server</code>
<pre>{
  &quot;mcpServers&quot;: {
    &quot;io-github-sailorpepe-undesirables-mcp-server&quot;: {
      &quot;url&quot;: &quot;https://mcp.the-undesirables.com/mcp&quot;
    }
  }
}</pre>
</body></html>"""


async def parse(client, text):
    return await client.post("/api/v1/admin/mcp/parse", headers=ADMIN, json={"text": text})


@respx.mock
async def test_a_listing_page_becomes_a_draft_and_oauth_is_detected(client):
    respx.get("https://agentuni.dev/servers/undesirables").mock(
        return_value=httpx.Response(200, text=AGENTUNI, headers={"Content-Type": "text/html; charset=utf-8"}))
    respx.post("https://mcp.the-undesirables.com/mcp").mock(
        return_value=httpx.Response(401, headers={"WWW-Authenticate": 'Bearer resource_metadata="https://mcp.the-undesirables.com/.well-known/oauth-protected-resource"'}))

    r = await parse(client, "https://agentuni.dev/servers/undesirables")
    assert r.status_code == 200, r.text
    [d] = r.json()
    assert d["name"] == "The Undesirables TCG Oracle"          # 用页面标题，不用那串注册表键名
    assert d["id"] == "the-undesirables-tcg-oracle"
    assert d["transport"] == "http"
    assert d["url"] == "https://mcp.the-undesirables.com/mcp"
    assert d["description"].startswith("TCG oracle: calibrated prices & risk")
    assert d["icon"] == "https://agentuni.dev/favicon.png"
    assert d["homepage"] == "https://agentuni.dev/servers/undesirables"
    assert d["auth"] == "oauth"                                  # 对方回 401，自动设成浏览器授权登录
    assert any("浏览器授权登录" in n for n in d["notes"])


@respx.mock
async def test_a_bare_endpoint_is_used_as_is(client):
    respx.post("https://mcp.example.com/mcp").mock(return_value=httpx.Response(200, json={"jsonrpc": "2.0", "id": 1, "result": {}}))
    [d] = (await parse(client, "https://mcp.example.com/mcp")).json()
    assert (d["transport"], d["url"], d["auth"], d["source"]) == ("http", "https://mcp.example.com/mcp", "none", "endpoint")

    [s] = (await parse(client, "https://old.example.com/sse")).json()
    assert s["transport"] == "sse"


REGISTRY = {
    "servers": [{
        "server": {
            "name": "io.github.acme/factory-mcp",
            "title": "Acme Factory",
            "description": "MES data for AI agents",
            "version": "1.2.0",
            "websiteUrl": "https://acme.example.com",
            "icons": [{"src": "https://acme.example.com/icon.png"}],
            "remotes": [
                {"type": "sse", "url": "https://mcp.acme.example.com/sse"},
                {"type": "streamable-http", "url": "https://{tenant}.acme.example.com/mcp",
                 "variables": {"tenant": {"description": "公司租户名", "isRequired": True}},
                 "headers": [{"name": "X-API-Key", "description": "后台生成的密钥", "isRequired": True, "isSecret": True}]},
            ],
            "packages": [{
                "registryType": "npm", "identifier": "@acme/factory-mcp", "version": "1.2.0",
                "transport": {"type": "stdio"},
                "environmentVariables": [
                    {"name": "ACME_TOKEN", "isRequired": True, "isSecret": True, "description": "访问令牌"},
                    {"name": "ACME_REGION", "default": "cn", "isSecret": False},
                ],
            }],
        },
        "_meta": {},
    }],
}


@respx.mock
async def test_a_registry_name_builds_remote_and_local_drafts(client):
    respx.get("https://registry.modelcontextprotocol.io/v0/servers").mock(return_value=httpx.Response(200, json=REGISTRY))
    r = await parse(client, "io.github.acme/factory-mcp")
    assert r.status_code == 200, r.text
    http_d, sse_d, local = r.json()

    assert http_d["transport"] == "http"                        # streamable-http 排在前面
    assert http_d["url"] == "https://${TENANT}.acme.example.com/mcp"
    assert http_d["headers"] == {"X-API-Key": "${X_API_KEY}"}
    fields = {f["key"]: f for f in http_d["fields"]}
    assert fields["TENANT"]["secret"] is False and fields["TENANT"]["help"] == "公司租户名"
    assert fields["X_API_KEY"]["secret"] is True
    assert http_d["auth"] == "fields"
    assert http_d["name"] == "Acme Factory" and http_d["description"] == "MES data for AI agents"
    assert http_d["icon"] == "https://acme.example.com/icon.png"

    assert sse_d["transport"] == "sse"
    assert len({http_d["id"], sse_d["id"], local["id"]}) == 3    # 三个草稿标识各不相同

    assert local["transport"] == "stdio"
    assert local["command"] == "npx" and local["args"] == ["-y", "@acme/factory-mcp@1.2.0"]
    assert local["env"] == {"ACME_TOKEN": "${ACME_TOKEN}", "ACME_REGION": "${ACME_REGION:-cn}"}


@respx.mock
async def test_a_github_repository_is_read_from_its_readme(client):
    readme = "# Notion MCP\n\nInstall:\n\n```\nclaude mcp add --transport http notion https://mcp.notion.com/mcp\n```\n"
    respx.get("https://raw.githubusercontent.com/makenotion/notion-mcp-server/HEAD/README.md").mock(
        return_value=httpx.Response(200, text=readme))
    respx.post("https://mcp.notion.com/mcp").mock(return_value=httpx.Response(401))
    [d] = (await parse(client, "https://github.com/makenotion/notion-mcp-server")).json()
    assert d["url"] == "https://mcp.notion.com/mcp"
    assert d["auth"] == "oauth"


@respx.mock
async def test_a_page_without_any_config_says_so(client):
    respx.get("https://example.com/blog").mock(return_value=httpx.Response(200, text="<html><title>Blog</title><p>hello</p></html>"))
    r = await parse(client, "https://example.com/blog")
    assert r.status_code == 400
    assert "没找到 MCP 配置" in r.json()["detail"]


async def test_pasted_config_still_works(client):
    r = await parse(client, json.dumps({"mcpServers": {"wecom": {"command": "npx", "args": ["-y", "wecom-mcp"]}}}))
    [d] = r.json()
    assert (d["id"], d["transport"], d["source"]) == ("wecom", "stdio", "config")
