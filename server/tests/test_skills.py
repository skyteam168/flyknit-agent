"""公司技能库：导入、分发、必装标记。"""

import io
import zipfile

import httpx
import pytest
import respx

from app.services import skill_library

SKILL_MD = """---
name: excel-report
description: >
  按公司模板生成 Excel 周报和月报。
  需要汇总质检或生产数据时使用。
version: 1.2.0
author: IT
keywords:
  - excel
  - 报表
---

# 生成周报

1. 读取数据
2. 按车间汇总
"""


def make_zip(files: dict[str, str]) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as zf:
        for name, content in files.items():
            zf.writestr(name, content)
    return buffer.getvalue()


def test_parses_frontmatter_with_folded_description_and_list_keywords():
    meta, body = skill_library.split_frontmatter(SKILL_MD)
    assert meta["name"] == "excel-report"
    assert meta["description"].startswith("按公司模板生成 Excel 周报和月报。")
    assert meta["version"] == "1.2.0"
    assert meta["keywords"] == "excel 报表"
    assert body.strip().startswith("# 生成周报")


def test_parses_package_with_wrapper_directory():
    data = make_zip({"repo-main/excel-report/SKILL.md": SKILL_MD, "repo-main/excel-report/scripts/build.py": "print(1)"})
    skill = skill_library.parse_packages(data)[0]
    assert skill.name == "excel-report"
    assert skill.version == "1.2.0"
    # 重新打包后以技能名为根目录
    with zipfile.ZipFile(io.BytesIO(skill.data)) as zf:
        assert sorted(zf.namelist()) == ["excel-report/SKILL.md", "excel-report/scripts/build.py"]


def test_parses_multiple_skills_in_one_repository():
    data = make_zip({
        "repo-main/skills/a/SKILL.md": SKILL_MD.replace("excel-report", "skill-a"),
        "repo-main/skills/b/SKILL.md": SKILL_MD.replace("excel-report", "skill-b"),
        "repo-main/README.md": "# repo",
    })
    names = sorted(s.name for s in skill_library.parse_packages(data))
    assert names == ["skill-a", "skill-b"]


@pytest.mark.parametrize(
    "files, message",
    [
        ({"a/readme.md": "x"}, "没有找到 SKILL.md"),
        ({"a/SKILL.md": "---\nname: xy\n---\nbody"}, "缺少 description"),
        ({"../evil.md": "x", "a/SKILL.md": SKILL_MD}, "路径不安全"),
        ({"a/SKILL.md": SKILL_MD, "a/setup.exe": "MZ"}, "可执行程序"),
    ],
)
def test_rejects_bad_packages(files, message):
    with pytest.raises(skill_library.SkillError) as exc:
        skill_library.parse_packages(make_zip(files))
    assert message in str(exc.value)


@pytest.mark.parametrize(
    "url, expected",
    [
        ("https://github.com/acme/skills", "https://codeload.github.com/acme/skills/zip/HEAD"),
        ("https://github.com/acme/skills/tree/main/excel-report", "https://codeload.github.com/acme/skills/zip/main"),
        ("https://example.com/pack.zip", "https://example.com/pack.zip"),
    ],
)
def test_github_url_conversion(url, expected):
    assert skill_library.github_zip_url(url) == expected


def test_github_subdir_filters_to_one_skill():
    assert skill_library.github_subdir("https://github.com/acme/skills/tree/main/skills/excel-report") == "skills/excel-report"
    skills = skill_library.parse_packages(make_zip({
        "r-main/skills/excel-report/SKILL.md": SKILL_MD,
        "r-main/skills/other/SKILL.md": SKILL_MD.replace("excel-report", "other"),
    }))
    picked = skill_library.filter_subdir(skills, "skills/excel-report")
    assert [s.name for s in picked] == ["excel-report"]


@respx.mock
async def test_import_from_github_then_client_downloads(client, admin_headers, device_headers):
    data = make_zip({"skills-main/excel-report/SKILL.md": SKILL_MD})
    respx.get("https://codeload.github.com/acme/skills/zip/HEAD").mock(return_value=httpx.Response(200, content=data))

    r = await client.post(
        "/api/v1/admin/skills/import",
        headers=admin_headers,
        json={"url": "https://github.com/acme/skills", "required": True},
    )
    assert r.status_code == 200
    assert r.json()["imported"] == ["excel-report"]

    listed = await client.get("/api/v1/admin/skills", headers=admin_headers)
    assert listed.json()[0]["version"] == "1.2.0"
    assert listed.json()[0]["required"] is True

    # 客户端看到并能下载
    mine = await client.get("/api/v1/client/skills", headers=device_headers)
    assert [s["name"] for s in mine.json()] == ["excel-report"]
    blob = await client.get("/api/v1/client/skills/excel-report/download", headers=device_headers)
    assert blob.status_code == 200
    with zipfile.ZipFile(io.BytesIO(blob.content)) as zf:
        assert "excel-report/SKILL.md" in zf.namelist()


@respx.mock
async def test_import_rejects_bad_package(client, admin_headers):
    respx.get("https://example.com/x.zip").mock(return_value=httpx.Response(200, content=make_zip({"a/readme.md": "x"})))
    r = await client.post("/api/v1/admin/skills/import", headers=admin_headers, json={"url": "https://example.com/x.zip"})
    assert r.status_code == 400
    assert "SKILL.md" in r.json()["detail"]


async def test_upload_patch_and_delete(client, admin_headers, device_headers):
    data = make_zip({"excel-report/SKILL.md": SKILL_MD})
    r = await client.post(
        "/api/v1/admin/skills/upload",
        headers=admin_headers,
        files={"file": ("pack.zip", data, "application/zip")},
    )
    assert r.json()["imported"] == ["excel-report"]

    # 停用后客户端看不到
    await client.patch("/api/v1/admin/skills/excel-report", headers=admin_headers, json={"enabled": False})
    assert (await client.get("/api/v1/client/skills", headers=device_headers)).json() == []
    assert (await client.get("/api/v1/client/skills/excel-report/download", headers=device_headers)).status_code == 404

    await client.delete("/api/v1/admin/skills/excel-report", headers=admin_headers)
    assert (await client.get("/api/v1/admin/skills", headers=admin_headers)).json() == []


async def test_client_skills_require_device_token(client):
    assert (await client.get("/api/v1/client/skills")).status_code == 401
