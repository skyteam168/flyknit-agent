"""专家：草稿 / 发布 / 下线 / 历史回滚，员工端只看得到发布的版本；AI 起草只留认得的技能和连接器。"""

import json

import httpx
import pytest
import respx

from app import db
from app.models import SkillPackage
from app.services import config_events
from conftest import ADMIN, setup_models

pytestmark = pytest.mark.asyncio

MES = {
    "id": "mes_analyst",
    "name": "MES生产分析专家",
    "avatar": "🏭",
    "tagline": "生产数据查询与异常分析",
    "category": "生产",
    "description": "负责：MES 生产数据查询、产量异常分析\n适合：分析产量下降\n不负责：财务\n需要的信息：车间、日期",
    "instructions": "你是 MES 生产分析专家。结论先行，给出依据。",
    "knowledge": "A 车间 = 1 号厂房",
    "examples": ["昨天 A 车间产量为什么下降？", "  ", "昨天 A 车间产量为什么下降？"],
    "disallowed_tools": ["delete_path"],
    "read_only": True,
}


async def client_experts(client, device_headers):
    r = await client.get("/api/v1/client/experts", headers=device_headers)
    assert r.status_code == 200, r.text
    return r.json()


async def test_only_published_versions_reach_employees(client, device_headers):
    r = await client.post("/api/v1/admin/experts", headers=ADMIN, json=MES)
    assert r.status_code == 201, r.text
    e = r.json()
    assert e["status"] == "draft" and e["version"] == 0
    assert e["examples"] == ["昨天 A 车间产量为什么下降？"]
    # 草稿员工端看不到
    assert await client_experts(client, device_headers) == []

    rev = config_events.current()
    r = await client.post("/api/v1/admin/experts/mes_analyst/publish", headers=ADMIN, json={"note": "首版"})
    assert r.status_code == 200, r.text
    assert r.json()["version"] == 1 and r.json()["status"] == "published"
    assert config_events.current() > rev  # 员工端马上来拉
    [live] = await client_experts(client, device_headers)
    assert live["name"] == "MES生产分析专家" and live["version"] == 1 and live["read_only"] is True

    # 改草稿：员工端还是旧的，后台标出「有未发布的修改」
    r = await client.put("/api/v1/admin/experts/mes_analyst", headers=ADMIN,
                         json={**MES, "instructions": "第二版工作方式"})
    assert r.json()["dirty"] is True
    assert (await client_experts(client, device_headers))[0]["instructions"] == MES["instructions"]

    await client.post("/api/v1/admin/experts/mes_analyst/publish", headers=ADMIN, json={})
    assert (await client_experts(client, device_headers))[0]["instructions"] == "第二版工作方式"

    # 历史两版；把第 1 版放回草稿，发布后员工端回到第 1 版的内容（版本号继续往上）
    versions = (await client.get("/api/v1/admin/experts/mes_analyst/versions", headers=ADMIN)).json()
    assert [v["version"] for v in versions] == [2, 1]
    assert versions[1]["note"] == "首版"
    r = await client.post("/api/v1/admin/experts/mes_analyst/versions/1/restore", headers=ADMIN)
    assert r.json()["instructions"] == MES["instructions"] and r.json()["dirty"] is True
    await client.post("/api/v1/admin/experts/mes_analyst/publish", headers=ADMIN, json={})
    live = (await client_experts(client, device_headers))[0]
    assert live["version"] == 3 and live["instructions"] == MES["instructions"]

    # 下线：员工端不再显示；重新发布就回来
    await client.post("/api/v1/admin/experts/mes_analyst/offline", headers=ADMIN)
    assert await client_experts(client, device_headers) == []
    await client.post("/api/v1/admin/experts/mes_analyst/publish", headers=ADMIN, json={})
    assert len(await client_experts(client, device_headers)) == 1

    assert (await client.delete("/api/v1/admin/experts/mes_analyst", headers=ADMIN)).status_code == 204
    assert await client_experts(client, device_headers) == []
    assert (await client.get("/api/v1/admin/experts", headers=ADMIN)).json() == []


async def test_discard_unpublished_changes(client):
    await client.post("/api/v1/admin/experts", headers=ADMIN, json=MES)
    r = await client.post("/api/v1/admin/experts/mes_analyst/discard", headers=ADMIN)
    assert r.status_code == 400  # 没发布过，没东西可恢复
    await client.post("/api/v1/admin/experts/mes_analyst/publish", headers=ADMIN, json={})
    await client.put("/api/v1/admin/experts/mes_analyst", headers=ADMIN, json={**MES, "name": "改了名"})
    r = await client.post("/api/v1/admin/experts/mes_analyst/discard", headers=ADMIN)
    assert r.json()["name"] == "MES生产分析专家" and r.json()["dirty"] is False


async def test_validation(client):
    assert (await client.post("/api/v1/admin/experts", headers=ADMIN, json={**MES, "id": "MES 专家"})).status_code == 422
    await client.post("/api/v1/admin/experts", headers=ADMIN, json=MES)
    assert (await client.post("/api/v1/admin/experts", headers=ADMIN, json=MES)).status_code == 409
    await client.put("/api/v1/admin/experts/mes_analyst", headers=ADMIN, json={**MES, "instructions": "  "})
    r = await client.post("/api/v1/admin/experts/mes_analyst/publish", headers=ADMIN, json={})
    assert r.status_code == 400 and "工作方式" in r.json()["detail"]
    # 员工端接口要设备令牌，后台接口要管理员
    assert (await client.get("/api/v1/client/experts")).status_code == 401
    assert (await client.get("/api/v1/admin/experts")).status_code in (401, 403)


async def test_options_list_tools_skills_connectors_and_chat_models(client):
    await setup_models(client)
    async with db.get_sessionmaker()() as s:
        s.add(SkillPackage(name="excel-report", description="生成 Excel 报表"))
        await s.commit()
    await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN,
                      json={"id": "mes", "name": "MES 系统", "url": "http://mes.local/mcp"})
    o = (await client.get("/api/v1/admin/experts/options", headers=ADMIN)).json()
    assert "run_shell" in [t["value"] for t in o["tools"]]
    assert [s["value"] for s in o["skills"]] == ["excel-report"]
    assert [m["value"] for m in o["mcp"]] == ["mes"]
    assert [m["label"] for m in o["models"]] == ["Qwen3.5-397B", "Qwen-Plus"]


@respx.mock
async def test_ai_draft_keeps_only_real_skills_and_connectors(client):
    await setup_models(client)
    async with db.get_sessionmaker()() as s:
        s.add(SkillPackage(name="excel-report", description="生成 Excel 报表"))
        await s.commit()
    await client.post("/api/v1/admin/mcp/vendors", headers=ADMIN,
                      json={"id": "mes", "name": "MES 系统", "url": "http://mes.local/mcp"})
    answer = {
        "id": "mes_analyst", "name": "MES生产分析专家", "avatar": "🏭", "tagline": "生产分析",
        "description": "负责：生产分析", "instructions": "你是 MES 专家", "knowledge": "",
        "examples": ["A 车间产量为什么下降？"],
        "preferred_skills": ["excel-report", "mes-sql"],
        "mcp_servers": ["mes", "erp"],
        "disallowed_tools": ["delete_path", "format_disk"],
        "read_only": True,
        "notes": ["ERP 还没有接入"],
    }
    route = respx.post("http://primary.local/v1/chat/completions").mock(return_value=httpx.Response(200, json={
        "choices": [{"message": {"content": "好的，草稿如下：\n```json\n" + json.dumps(answer, ensure_ascii=False) + "\n```"}}]}))
    r = await client.post("/api/v1/admin/experts/draft", headers=ADMIN, json={"text": "我要一个 MES 生产分析专家"})
    assert r.status_code == 200, r.text
    d = r.json()
    assert d["id"] == "mes_analyst" and d["read_only"] is True
    assert d["preferred_skills"] == ["excel-report"]
    assert d["mcp_servers"] == ["mes"]
    assert d["disallowed_tools"] == ["delete_path"]
    assert "ERP 还没有接入" in d["notes"]
    assert any("mes-sql" in n for n in d["notes"])
    # 给 AI 看的清单里有真实的技能和连接器
    prompt = json.loads(route.calls[0].request.content)["messages"][0]["content"]
    assert "excel-report" in prompt and "MES 系统" in prompt

    # 标识已被占用：提示换一个
    await client.post("/api/v1/admin/experts", headers=ADMIN, json=MES)
    d = (await client.post("/api/v1/admin/experts/draft", headers=ADMIN, json={"text": "MES 专家"})).json()
    assert any("已经被占用" in n for n in d["notes"])


@respx.mock
async def test_ai_draft_reports_unusable_answers(client):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "抱歉，我不太明白"}}]}))
    r = await client.post("/api/v1/admin/experts/draft", headers=ADMIN, json={"text": "MES 专家"})
    assert r.status_code == 502 and "格式" in r.json()["detail"]


async def test_ai_draft_without_models(client):
    r = await client.post("/api/v1/admin/experts/draft", headers=ADMIN, json={"text": "MES 专家"})
    assert r.status_code == 502 and "模型" in r.json()["detail"]


async def test_usage_is_counted_per_message_and_device(client, device_headers):
    await client.post("/api/v1/admin/experts", headers=ADMIN, json={**MES, "tags": ["产量分析", " 设备停机", "产量分析"], "author": "IT 部"})
    # 草稿不记
    assert (await client.post("/api/v1/client/experts/mes_analyst/use", headers=device_headers)).status_code == 404
    await client.post("/api/v1/admin/experts/mes_analyst/publish", headers=ADMIN, json={})
    for _ in range(3):
        assert (await client.post("/api/v1/client/experts/mes_analyst/use", headers=device_headers)).status_code == 204
    other = await client.post("/api/v1/devices/register",
                              json={"enrollment_key": "test-enroll", "machine_name": "PC-002", "user_name": "tran"})
    await client.post("/api/v1/client/experts/mes_analyst/use",
                      headers={"Authorization": f"Bearer {other.json()['token']}"})
    [live] = await client_experts(client, device_headers)
    assert live["uses"] == 4 and live["users"] == 2
    assert live["tags"] == ["产量分析", "设备停机"] and live["author"] == "IT 部"
    [admin_view] = (await client.get("/api/v1/admin/experts", headers=ADMIN)).json()
    assert admin_view["uses"] == 4


async def test_rows_from_before_the_upgrade_still_load(client, device_headers):
    """老版本建的专家：升级时补上的 tags、author 列是 NULL，快照里也没有这两项。"""
    from sqlalchemy import text

    await client.post("/api/v1/admin/experts", headers=ADMIN, json=MES)
    await client.post("/api/v1/admin/experts/mes_analyst/publish", headers=ADMIN, json={})
    async with db.get_sessionmaker()() as s:
        # 和升级时一样：列是后加的、可以为空，老行里是 NULL
        for column, kind in (("tags", "JSON"), ("author", "VARCHAR(100)")):
            await s.execute(text(f"ALTER TABLE experts DROP COLUMN {column}"))
            await s.execute(text(f"ALTER TABLE experts ADD COLUMN {column} {kind} NULL"))
        row = (await s.execute(text("SELECT published FROM experts"))).scalar_one()
        snap = json.loads(row) if isinstance(row, str) else dict(row)
        snap.pop("tags", None)
        snap.pop("author", None)
        await s.execute(text("UPDATE experts SET published = :p"), {"p": json.dumps(snap, ensure_ascii=False)})
        await s.commit()
    r = await client.get("/api/v1/admin/experts", headers=ADMIN)
    assert r.status_code == 200, r.text
    [e] = r.json()
    assert e["tags"] == [] and e["author"] == "" and e["dirty"] is False
    [live] = await client_experts(client, device_headers)
    assert live["tags"] == [] and live["author"] == ""
