"""插件包：Claude Code 目录结构解析、按部门下发（服务端过滤）、升级、冲突、拒收脚本类 hook、卸载。"""

import io
import json
import zipfile

from conftest import ADMIN


def make_zip(files: dict[str, str], root: str = "qc-plugin") -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, text in files.items():
            z.writestr(f"{root}/{name}" if root else name, text)
    return buf.getvalue()


def qc_files(version: str = "1.0.0", **extra: str) -> dict[str, str]:
    files = {
        ".claude-plugin/plugin.json": json.dumps({
            "name": "qc", "displayName": "质检部插件", "version": version,
            "description": "质检日报、来料检验", "author": {"name": "IT 部"},
        }),
        "commands/daily.md": "---\ndescription: 生成质检日报\nargument-hint: 日期\n---\n汇总 $1 的质检数据，写成日报。补充：$ARGUMENTS",
        "skills/qc-report/SKILL.md": "---\nname: qc-report\ndescription: 质检日报格式\n---\n按公司模板写。",
        "agents/inspector.md": "---\nname: inspector\ndescription: 来料检验分析\ntools: Read, Grep, Glob\n---\n你是来料检验专家。",
        ".mcp.json": json.dumps({"mcpServers": {"qms": {"type": "http", "url": "https://qms.example.com/mcp",
                                                        "headers": {"Authorization": "Bearer ${QMS_TOKEN}"}}}}),
        "rules.json": json.dumps({"rules": [{"name": "QMS 只读", "tools": "mcp__qms__*", "match": "delete",
                                             "action": "block", "message": "QMS 只能查"}]}),
    }
    files.update(extra)
    return files


async def _upload(client, data: bytes, **params):
    return await client.post("/api/v1/admin/plugins/upload", headers=ADMIN, params=params,
                             files={"file": ("qc.zip", data, "application/zip")})


async def _set_department(client, dept: str):
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    r = await client.patch(f"/api/v1/admin/devices/{devices[0]['id']}", headers=ADMIN, json={"department": dept})
    assert r.status_code == 200, r.text


async def test_install_materializes_everything(client, device_headers):
    preview = await client.post("/api/v1/admin/plugins/preview", headers=ADMIN,
                                files={"file": ("qc.zip", make_zip(qc_files()), "application/zip")})
    assert preview.status_code == 200, preview.text
    p = preview.json()
    assert p["id"] == "qc" and p["conflicts"] == [] and p["upgrade_from"] is None
    assert p["counts"] == {"skills": 1, "commands": 1, "experts": 1, "connectors": 1, "rules": 1}
    # Claude Code 的 $1 / $ARGUMENTS 换成我们的 {参数}
    assert p["manifest"]["commands"][0]["template"] == "汇总 {arg1} 的质检数据，写成日报。补充：{arguments}"

    r = await _upload(client, make_zip(qc_files()), required="true")
    assert r.status_code == 200, r.text

    skills = (await client.get("/api/v1/client/skills", headers=device_headers)).json()
    qc = next(s for s in skills if s["name"] == "qc-report")
    assert qc["plugin"] == "qc" and qc["required"] is True
    assert (await client.get("/api/v1/client/skills/qc-report/download", headers=device_headers)).status_code == 200

    experts = (await client.get("/api/v1/client/experts", headers=device_headers)).json()
    ins = next(e for e in experts if e["id"] == "qc-inspector")
    assert ins["read_only"] is True  # tools 白名单里没有写入类工具 → 只读（专家只做减法）
    assert ins["instructions"] == "你是来料检验专家。"

    vendors = (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json()
    qms = next(v for v in vendors if v["id"] == "qms")
    assert qms["plugin"] == "qc" and [f["key"] for f in qms["fields"]] == ["QMS_TOKEN"]

    cmds = (await client.get("/api/v1/client/commands", headers=device_headers)).json()["items"]
    assert any(c["name"] == "daily" and c["source"] == "质检部插件" for c in cmds)

    cfg = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert any(r["action"] == "block" and r["tools"] == "mcp__qms__*" for r in cfg["tool_rules"])

    mine = (await client.get("/api/v1/client/plugins", headers=device_headers)).json()["items"]
    assert [x["id"] for x in mine] == ["qc"]


async def test_department_scope_is_enforced_on_the_server(client, device_headers):
    r = await _upload(client, make_zip(qc_files()), departments=["质检部"])
    assert r.status_code == 200, r.text

    # 设备还没有部门：插件带来的任何东西都看不到，技能包也下载不了
    assert (await client.get("/api/v1/client/plugins", headers=device_headers)).json()["items"] == []
    assert all(s["name"] != "qc-report" for s in (await client.get("/api/v1/client/skills", headers=device_headers)).json())
    assert (await client.get("/api/v1/client/skills/qc-report/download", headers=device_headers)).status_code == 404
    assert all(e["id"] != "qc-inspector" for e in (await client.get("/api/v1/client/experts", headers=device_headers)).json())
    assert all(v["id"] != "qms" for v in (await client.get("/api/v1/client/mcp/vendors", headers=device_headers)).json())
    assert all(c["name"] != "daily" for c in (await client.get("/api/v1/client/commands", headers=device_headers)).json()["items"])
    cfg = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    assert all(r["tools"] != "mcp__qms__*" for r in cfg["tool_rules"])

    await _set_department(client, "质检部")
    assert [x["id"] for x in (await client.get("/api/v1/client/plugins", headers=device_headers)).json()["items"]] == ["qc"]

    # 停用插件：同样全部收起来
    assert (await client.patch("/api/v1/admin/plugins/qc", headers=ADMIN, json={"enabled": False})).status_code == 200
    assert (await client.get("/api/v1/client/plugins", headers=device_headers)).json()["items"] == []
    assert all(e["id"] != "qc-inspector" for e in (await client.get("/api/v1/client/experts", headers=device_headers)).json())


async def test_upgrade_drops_removed_parts_and_uninstall_cleans_up(client, device_headers):
    assert (await _upload(client, make_zip(qc_files()))).status_code == 200
    v2 = qc_files("2.0.0")
    del v2["agents/inspector.md"]
    r = await _upload(client, make_zip(v2))
    assert r.status_code == 200 and r.json()["version"] == "2.0.0"
    assert all(e["id"] != "qc-inspector" for e in (await client.get("/api/v1/admin/experts", headers=ADMIN)).json())

    assert (await client.delete("/api/v1/admin/plugins/qc", headers=ADMIN)).status_code == 204
    assert all(s["name"] != "qc-report" for s in (await client.get("/api/v1/admin/skills", headers=ADMIN)).json())
    assert all(v["id"] != "qms" for v in (await client.get("/api/v1/admin/mcp/vendors", headers=ADMIN)).json())
    assert (await client.get("/api/v1/admin/plugins", headers=ADMIN)).json()["items"] == []


async def test_conflicts_and_unsafe_content_are_refused(client):
    # 管理员手工建的连接器同名：拒绝，不覆盖
    r = await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN,
                          json={"id": "qms", "name": "QMS", "transport": "http", "url": "https://q.example.com/mcp"})
    assert r.status_code in (200, 201), r.text
    r = await _upload(client, make_zip(qc_files()))
    assert r.status_code == 409 and "连接器 qms" in r.text

    # 要在员工电脑上跑脚本的 hook：拒收
    hooks = {"hooks": {"PreToolUse": [{"matcher": "Bash", "hooks": [{"type": "command", "command": "./check.sh"}]}]}}
    files = qc_files(**{"hooks/hooks.json": json.dumps(hooks)})
    del files[".mcp.json"]
    r = await _upload(client, make_zip(files))
    assert r.status_code == 400 and "执行脚本" in r.text

    # 规则不合法（没有「放行」动作）
    files = qc_files(**{"rules.json": json.dumps([{"name": "x", "action": "allow"}])})
    del files[".mcp.json"]
    assert (await _upload(client, make_zip(files))).status_code == 400

    # 本地进程要跑插件自带的程序
    files = qc_files(**{".mcp.json": json.dumps({"mcpServers": {"local": {"command": "node",
                                                                          "args": ["${CLAUDE_PLUGIN_ROOT}/s.js"]}}})})
    r = await _upload(client, make_zip(files))
    assert r.status_code == 400 and "CLAUDE_PLUGIN_ROOT" in r.text

    # 路径穿越、可执行文件和技能库同一套检查
    assert (await _upload(client, make_zip({**qc_files(), "../evil.md": "x"}))).status_code == 400
    assert (await _upload(client, make_zip({**qc_files(), "tool.exe": "MZ"}))).status_code == 400
    # 没有清单
    assert (await _upload(client, make_zip({"commands/a.md": "x"}))).status_code == 400


async def test_marketplace_zip_needs_a_subdir(client):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name in ("a-plugin", "b-plugin"):
            z.writestr(f"repo/plugins/{name}/.claude-plugin/plugin.json", json.dumps({"name": name}))
            z.writestr(f"repo/plugins/{name}/commands/hi.md", "你好")
    r = await _upload(client, buf.getvalue())
    assert r.status_code == 400 and "2 个插件" in r.text


async def test_only_admins_manage_plugins(client, device_headers):
    r = await client.post("/api/v1/admin/plugins/upload", headers=device_headers,
                          files={"file": ("qc.zip", make_zip(qc_files()), "application/zip")})
    assert r.status_code in (401, 403)
