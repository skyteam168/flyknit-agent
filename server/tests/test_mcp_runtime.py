"""MCP 运行环境：服务端替员工电脑下载便携版 Node.js / uv / Python，并当 npm、PyPI 的缓存代理。"""

import hashlib
import json

import httpx
import pytest
import respx

from conftest import ADMIN

pytestmark = pytest.mark.asyncio

NODE_URL = "https://nodejs.org/dist/v22.12.0/node-v22.12.0-win-x64.zip"


async def test_manifest_and_runtime_download(client, device_headers):
    with respx.mock(assert_all_called=False) as mock:
        node = mock.get(NODE_URL).mock(return_value=httpx.Response(200, content=b"PK-node-zip"))
        m = (await client.get("/api/v1/client/runtimes", headers=device_headers)).json()
        assert m["enabled"] is True
        assert m["npm_registry"].endswith("/api/v1/mirror/npm/") and m["pypi_index"].endswith("/api/v1/mirror/pypi/simple/")
        [n] = [r for r in m["runtimes"] if r["name"] == "node"]
        assert n == {"name": "node", "version": "22.12.0", "archive": "zip", "root": "node-v22.12.0-win-x64",
                     "bin": [""], "sha256": "", "size": 0}
        py = next(r for r in m["runtimes"] if r["name"] == "python")
        assert py["archive"] == "tar.gz" and py["bin"] == ["", "Scripts"]

        r = await client.get("/api/v1/client/runtimes/node/download", headers=device_headers)
        assert r.status_code == 200 and r.content == b"PK-node-zip"
        assert r.headers["x-runtime-sha256"] == hashlib.sha256(b"PK-node-zip").hexdigest()
        # 第二次直接用缓存
        await client.get("/api/v1/client/runtimes/node/download", headers=device_headers)
        assert node.call_count == 1
        m = (await client.get("/api/v1/client/runtimes", headers=device_headers)).json()
        assert next(r for r in m["runtimes"] if r["name"] == "node")["size"] == len(b"PK-node-zip")

    # 要设备令牌
    assert (await client.get("/api/v1/client/runtimes")).status_code == 401


async def test_offline_upload_and_failure_message(client, device_headers):
    with respx.mock(assert_all_called=False) as mock:
        mock.get(url__regex=r"https://github\.com/.*").mock(side_effect=httpx.ConnectError("no internet"))
        r = await client.get("/api/v1/client/runtimes/uv/download", headers=device_headers)
        assert r.status_code == 502 and "上传离线包" in r.json()["detail"]

    # 管理员上传离线包，之后员工端直接拿
    bad = await client.post("/api/v1/admin/mcp-runtime/uv/upload", headers=ADMIN, files={"file": ("uv.tar.gz", b"x")})
    assert bad.status_code == 400
    r = await client.post("/api/v1/admin/mcp-runtime/uv/upload", headers=ADMIN,
                          files={"file": ("uv-x86_64-pc-windows-msvc.zip", b"PK-uv")})
    assert r.status_code == 200
    assert next(x for x in r.json()["runtimes"] if x["name"] == "uv")["cached"] is True
    r = await client.get("/api/v1/client/runtimes/uv/download", headers=device_headers)
    assert r.content == b"PK-uv"


async def test_settings_switch_mirrors_and_versions(client, device_headers):
    r = await client.put("/api/v1/admin/mcp-runtime", headers=ADMIN, json={
        "enabled": True, "npm": "npmmirror", "pypi": "tuna", "node_dist": "npmmirror",
        "overrides": {"node": {"version": "20.18.1"}, "evil": {"url": "x"}}})
    assert r.status_code == 200, r.text
    node = next(x for x in r.json()["runtimes"] if x["name"] == "node")
    assert node["url"] == "https://npmmirror.com/mirrors/node/v20.18.1/node-v20.18.1-win-x64.zip"
    bad = await client.put("/api/v1/admin/mcp-runtime", headers=ADMIN, json={"overrides": {"uv": {"url": "file:///etc/passwd"}}})
    assert bad.status_code == 400

    # 关掉：员工端回到原来的做法（用电脑上自己装的）
    await client.put("/api/v1/admin/mcp-runtime", headers=ADMIN, json={"enabled": False})
    assert (await client.get("/api/v1/client/runtimes", headers=device_headers)).json()["enabled"] is False
    assert (await client.get("/api/v1/mirror/npm/express")).status_code == 404
    assert (await client.get("/api/v1/admin/mcp-runtime")).status_code in (401, 403)


async def test_npm_proxy_rewrites_tarballs_and_caches(client):
    packument = {"name": "@playwright/mcp", "versions": {"0.0.40": {"dist": {
        "tarball": "https://registry.npmjs.org/@playwright/mcp/-/mcp-0.0.40.tgz", "integrity": "sha512-x"}}}}
    with respx.mock(assert_all_called=False) as mock:
        meta = mock.get("https://registry.npmjs.org/@playwright/mcp").mock(return_value=httpx.Response(200, json=packument))
        tgz = mock.get("https://registry.npmjs.org/@playwright/mcp/-/mcp-0.0.40.tgz").mock(
            return_value=httpx.Response(200, content=b"tgz-bytes"))

        r = await client.get("/api/v1/mirror/npm/@playwright%2fmcp", headers={"Accept": "application/vnd.npm.install-v1+json"})
        assert r.status_code == 200, r.text
        tarball = r.json()["versions"]["0.0.40"]["dist"]["tarball"]
        assert tarball == "http://test/api/v1/mirror/npm/@playwright/mcp/-/mcp-0.0.40.tgz"

        for _ in range(2):
            r = await client.get("/api/v1/mirror/npm/@playwright/mcp/-/mcp-0.0.40.tgz")
            assert r.content == b"tgz-bytes"
        assert tgz.call_count == 1

        # 元数据新鲜期内不再问上游
        await client.get("/api/v1/mirror/npm/@playwright%2fmcp", headers={"Accept": "application/vnd.npm.install-v1+json"})
        assert meta.call_count == 1

    # 不能拿来跳出缓存目录
    assert (await client.get("/api/v1/mirror/npm/../../etc/passwd.tgz")).status_code == 404


async def test_npm_proxy_serves_stale_metadata_when_upstream_is_down(client, monkeypatch):
    from app.routers import mcp_runtime
    with respx.mock(assert_all_called=False) as mock:
        mock.get("https://registry.npmjs.org/express").mock(return_value=httpx.Response(200, json={"name": "express"}))
        assert (await client.get("/api/v1/mirror/npm/express")).json()["name"] == "express"
    monkeypatch.setattr(mcp_runtime, "META_TTL", 0)
    with respx.mock(assert_all_called=False) as mock:
        mock.get("https://registry.npmjs.org/express").mock(side_effect=httpx.ConnectError("down"))
        r = await client.get("/api/v1/mirror/npm/express")
        assert r.status_code == 200 and r.json()["name"] == "express"
        mock.get("https://registry.npmjs.org/left-pad").mock(side_effect=httpx.ConnectError("down"))
        assert (await client.get("/api/v1/mirror/npm/left-pad")).status_code == 502


async def test_pypi_proxy_json_and_html(client):
    page = {"meta": {"api-version": "1.1"}, "name": "mcp-server-time", "files": [
        {"filename": "mcp_server_time-0.6.2-py3-none-any.whl",
         "url": "https://files.pythonhosted.org/packages/ab/cd/mcp_server_time-0.6.2-py3-none-any.whl",
         "hashes": {"sha256": "aa"}},
        {"filename": "evil.whl", "url": "https://evil.example.com/evil.whl", "hashes": {}}]}
    html = '<a href="https://files.pythonhosted.org/packages/ab/cd/mcp_server_time-0.6.2-py3-none-any.whl#sha256=aa">x</a>'
    with respx.mock(assert_all_called=False) as mock:
        route = mock.get("https://pypi.org/simple/mcp-server-time/")
        route.side_effect = lambda req: (
            httpx.Response(200, json=page, headers={"content-type": "application/vnd.pypi.simple.v1+json"})
            if "json" in req.headers.get("accept", "") else httpx.Response(200, text=html, headers={"content-type": "text/html"}))
        whl = mock.get("https://files.pythonhosted.org/packages/ab/cd/mcp_server_time-0.6.2-py3-none-any.whl").mock(
            return_value=httpx.Response(200, content=b"wheel"))

        r = await client.get("/api/v1/mirror/pypi/simple/MCP_Server.Time/", headers={"Accept": "application/vnd.pypi.simple.v1+json"})
        assert r.status_code == 200 and r.headers["content-type"].startswith("application/vnd.pypi.simple.v1+json")
        files = r.json()["files"]
        assert files[0]["url"] == "http://test/api/v1/mirror/pypi/files/files.pythonhosted.org/packages/ab/cd/mcp_server_time-0.6.2-py3-none-any.whl"
        assert files[1]["url"] == "https://evil.example.com/evil.whl"  # 别的主机不代理

        r = await client.get("/api/v1/mirror/pypi/simple/mcp-server-time/")
        assert 'href="http://test/api/v1/mirror/pypi/files/files.pythonhosted.org/packages/ab/cd/mcp_server_time-0.6.2-py3-none-any.whl#sha256=aa"' in r.text

        for _ in range(2):
            r = await client.get("/api/v1/mirror/pypi/files/files.pythonhosted.org/packages/ab/cd/mcp_server_time-0.6.2-py3-none-any.whl")
            assert r.content == b"wheel"
        assert whl.call_count == 1

    # 只代理配置好的文件主机
    assert (await client.get("/api/v1/mirror/pypi/files/evil.example.com/x.whl")).status_code == 404
