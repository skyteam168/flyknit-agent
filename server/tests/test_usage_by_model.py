"""按模型的用量统计：网关按实际用的模型记（走了备用模型就记在备用模型上），首页按模型汇总，历史从聊天记录补。"""

from datetime import datetime, timedelta, timezone

import httpx
import pytest
import respx
from sqlalchemy import select

from app import db
from app.models import ChatRecord, Device, Setting, UsageModelDaily
from app.services import usage_store
from conftest import ADMIN, setup_models

pytestmark = pytest.mark.asyncio

USAGE = {"prompt_tokens": 1000, "completion_tokens": 200, "total_tokens": 1200}


async def ask(client, headers, scene="chat"):
    r = await client.post("/api/v1/chat/completions", headers=headers,
                          json={"model": scene, "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200, r.text
    return r


@respx.mock
async def test_usage_is_recorded_against_the_model_that_answered(client, device_headers):
    await setup_models(client)
    primary = respx.post("http://primary.local/v1/chat/completions")
    primary.mock(return_value=httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}], "usage": USAGE}))
    await ask(client, device_headers)
    await ask(client, device_headers, scene="agent")

    # 主模型挂了，走备用：记在备用模型头上
    primary.mock(return_value=httpx.Response(503))
    respx.post("http://backup.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}], "usage": USAGE}))
    await ask(client, device_headers)

    d = (await client.get("/api/v1/admin/dashboard?days=7", headers=ADMIN)).json()
    models = {m["model"]: m for m in d["models"]}
    assert models["Qwen3.5-397B"]["tokens"] == 2400
    assert models["Qwen3.5-397B"]["requests"] == 2
    assert models["Qwen3.5-397B"]["today_tokens"] == 2400
    assert {s["scene"] for s in models["Qwen3.5-397B"]["scenes"]} == {"chat", "agent"}
    assert models["Qwen-Plus"]["tokens"] == 1200
    assert d["models"][0]["model"] == "Qwen3.5-397B"
    series = {s["model"]: s["data"] for s in d["model_series"]}
    assert series["Qwen3.5-397B"][-1] == 2400
    assert len(series["Qwen-Plus"]) == 7


async def test_many_models_are_grouped_into_other(client, device_headers):
    async with db.get_sessionmaker()() as s:
        device_id = (await s.scalars(select(Device.id))).first()
        for i in range(9):
            await usage_store.record(s, device_id, "chat", 100 * (i + 1), 0, model_id=i, model_name=f"模型{i}")
    d = (await client.get("/api/v1/admin/dashboard?days=7", headers=ADMIN)).json()
    assert len(d["models"]) == 9
    names = [s["model"] for s in d["model_series"]]
    assert names[-1] == "其他" and len(names) == 7
    assert d["model_series"][-1]["data"][-1] == 100 + 200 + 300


async def test_history_is_backfilled_once_from_chat_records(client):
    yesterday = datetime.now(timezone.utc) - timedelta(days=1)
    async with db.get_sessionmaker()() as s:
        s.add_all([
            ChatRecord(model="Qwen3.5-397B", scene="agent", prompt_tokens=5000, completion_tokens=100, created_at=yesterday),
            ChatRecord(model="Qwen3.5-397B", scene="agent", prompt_tokens=3000, completion_tokens=100, created_at=yesterday),
            ChatRecord(model="qwen-plus", scene="chat", prompt_tokens=800, completion_tokens=50, created_at=yesterday),
            ChatRecord(model="", scene="chat", prompt_tokens=10, completion_tokens=0, created_at=yesterday),
        ])
        # 启动时已经在空库上跑过一次（记下了「做过」）；模拟从老版本升级上来：去掉这个标记
        await s.delete(await s.get(Setting, usage_store.MODEL_BACKFILL_KEY))
        await s.commit()
        assert await usage_store.backfill_model_usage(s) == 3
        # 只做一次
        assert await usage_store.backfill_model_usage(s) == 0
        rows = (await s.scalars(select(UsageModelDaily).order_by(UsageModelDaily.model_name))).all()
    assert [(r.model_name, r.prompt_tokens, r.requests) for r in rows] == [("Qwen3.5-397B", 8000, 2), ("qwen-plus", 800, 1)]
    d = (await client.get("/api/v1/admin/dashboard?days=7", headers=ADMIN)).json()
    assert d["models"][0]["model"] == "Qwen3.5-397B"
    assert d["models"][0]["tokens"] == 8200
    assert d["models"][0]["today_tokens"] == 0
