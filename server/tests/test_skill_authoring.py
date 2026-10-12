"""在后台编写技能：新建、编辑（自动加版本号、保留图片）、AI 起草。"""

import io
import json
import zipfile

import httpx
import pytest
import respx

from conftest import ADMIN, setup_models

pytestmark = pytest.mark.asyncio

SKILL_MD = "---\nname: weekly-report\ndescription: 把本周的生产数据整理成周报 Excel\n---\n\n# 周报\n1. 读取数据\n"


async def test_create_edit_and_download(client, device_headers):
    r = await client.post("/api/v1/admin/skills", headers=ADMIN, json={
        "name": "weekly-report", "skill_md": SKILL_MD, "required": True,
        "files": [{"path": "scripts/build.py", "content": "print('ok')"}],
    })
    assert r.status_code == 201, r.text
    s = r.json()
    assert s["version"] == "1.0.0" and s["file_count"] == 2 and s["origin"] == "后台编写" and s["required"] is True
    assert (await client.post("/api/v1/admin/skills", headers=ADMIN, json={"name": "weekly-report", "skill_md": SKILL_MD})).status_code == 409

    src = (await client.get("/api/v1/admin/skills/weekly-report/source", headers=ADMIN)).json()
    assert "version: 1.0.0" in src["skill_md"]
    assert src["files"] == [{"path": "scripts/build.py", "content": "print('ok')", "binary": False, "size": 11}]

    # 员工电脑按版本号判断更新：没改版本号就自动加一
    r = await client.put("/api/v1/admin/skills/weekly-report/source", headers=ADMIN, json={
        "skill_md": src["skill_md"].replace("1. 读取数据", "1. 读取数据\n2. 生成图表"), "files": [],
    })
    assert r.status_code == 200, r.text
    assert r.json()["version"] == "1.0.1" and r.json()["file_count"] == 1

    [live] = (await client.get("/api/v1/client/skills", headers=device_headers)).json()
    assert live["version"] == "1.0.1"
    zipped = (await client.get("/api/v1/client/skills/weekly-report/download", headers=device_headers)).content
    with zipfile.ZipFile(io.BytesIO(zipped)) as zf:
        assert zf.namelist() == ["weekly-report/SKILL.md"]
        assert "生成图表" in zf.read("weekly-report/SKILL.md").decode()


async def test_binary_files_survive_edits(client):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("weekly-report/SKILL.md", SKILL_MD.replace("---\n\n", "version: 2.3\n---\n\n"))
        zf.writestr("weekly-report/assets/logo.png", b"\x89PNG\x00\x01binary")
        zf.writestr("weekly-report/reference/rules.md", "规则")
    r = await client.post("/api/v1/admin/skills/upload", headers=ADMIN, files={"file": ("w.zip", buf.getvalue(), "application/zip")})
    assert r.status_code == 200, r.text

    src = (await client.get("/api/v1/admin/skills/weekly-report/source", headers=ADMIN)).json()
    assert [(f["path"], f["binary"]) for f in src["files"]] == [("assets/logo.png", True), ("reference/rules.md", False)]

    # 只改文本：图片原样保留；管理员自己写的版本号不动
    r = await client.put("/api/v1/admin/skills/weekly-report/source", headers=ADMIN, json={
        "skill_md": src["skill_md"].replace("version: 2.3", "version: 3.0"),
        "files": [{"path": "reference/rules.md", "content": "新规则"}],
    })
    assert r.json()["version"] == "3.0" and r.json()["file_count"] == 3
    src = (await client.get("/api/v1/admin/skills/weekly-report/source", headers=ADMIN)).json()
    assert [f["path"] for f in src["files"]] == ["assets/logo.png", "reference/rules.md"]

    r = await client.put("/api/v1/admin/skills/weekly-report/source", headers=ADMIN, json={
        "skill_md": src["skill_md"], "files": [], "remove": ["assets/logo.png"],
    })
    assert r.json()["version"] == "3.1" and r.json()["file_count"] == 1


async def test_validation(client):
    bad = [
        ({"name": "other-name", "skill_md": SKILL_MD}, "一致"),
        ({"name": "weekly-report", "skill_md": "---\nname: weekly-report\n---\n正文"}, "description"),
        ({"name": "weekly-report", "skill_md": SKILL_MD, "files": [{"path": "../evil.py", "content": ""}]}, "路径"),
        ({"name": "weekly-report", "skill_md": SKILL_MD, "files": [{"path": "tool.exe", "content": ""}]}, "可执行"),
        ({"name": "weekly-report", "skill_md": SKILL_MD, "files": [{"path": "a.md", "content": ""}, {"path": "A.md", "content": ""}]}, "重名"),
    ]
    for body, word in bad:
        r = await client.post("/api/v1/admin/skills", headers=ADMIN, json=body)
        assert r.status_code == 400 and word in r.json()["detail"], (body, r.text)
    assert (await client.get("/api/v1/admin/skills/nope/source", headers=ADMIN)).status_code == 404
    assert (await client.post("/api/v1/admin/skills", json={"name": "x1", "skill_md": SKILL_MD})).status_code in (401, 403)


@respx.mock
async def test_ai_draft_and_revise(client):
    await setup_models(client)
    answer = {
        "name": "Weekly Report",
        "skill_md": "---\nname: something-else\ndescription: 生成生产周报\n---\n\n# 步骤\n先问用户数据在哪",
        "files": [{"path": "scripts/build.py", "content": "print(1)"}, {"path": "../bad.py", "content": "x"}, {"path": "SKILL.md", "content": "x"}],
        "notes": ["请补充周报模板"],
    }
    route = respx.post("http://primary.local/v1/chat/completions").mock(return_value=httpx.Response(200, json={
        "choices": [{"message": {"content": "```json\n" + json.dumps(answer, ensure_ascii=False) + "\n```"}}]}))
    r = await client.post("/api/v1/admin/skills/draft", headers=ADMIN, json={"text": "每周五把产量数据做成周报"})
    assert r.status_code == 200, r.text
    d = r.json()
    assert d["name"] == "weekly-report"
    assert "name: weekly-report" in d["skill_md"] and "version: 1.0.0" in d["skill_md"]
    assert [f["path"] for f in d["files"]] == ["scripts/build.py"]
    assert "请补充周报模板" in d["notes"] and any("跳过" in n for n in d["notes"])

    # 起草的结果能直接保存
    r = await client.post("/api/v1/admin/skills", headers=ADMIN, json={**d, "origin": "AI 生成"})
    assert r.status_code == 201, r.text

    # 在已有技能上修改：把现在的内容交给 AI，名字不变
    r = await client.post("/api/v1/admin/skills/draft", headers=ADMIN, json={"text": "加上图表", "name": "weekly-report"})
    assert r.json()["name"] == "weekly-report"
    system = json.loads(route.calls[-1].request.content)["messages"][0]["content"]
    assert "先问用户数据在哪" in system and "print(1)" in system


async def test_ai_draft_without_models(client):
    r = await client.post("/api/v1/admin/skills/draft", headers=ADMIN, json={"text": "周报"})
    assert r.status_code == 502 and "模型" in r.json()["detail"]
