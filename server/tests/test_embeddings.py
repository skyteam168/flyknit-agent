"""embedding 场景：员工端按意思检索本机记忆用的向量接口。"""

import json

import httpx
import pytest
import respx

from conftest import ADMIN

pytestmark = pytest.mark.asyncio


async def setup_embedding(client, base_url="http://bailian.local/v1"):
    p = (await client.post("/api/v1/admin/providers", headers=ADMIN,
                           json={"name": "百炼", "base_url": base_url, "api_key": "sk-emb-1234"})).json()
    m = (await client.post("/api/v1/admin/models", headers=ADMIN,
                           json={"provider_id": p["id"], "name": "通义向量", "model": "text-embedding-v4",
                                 "extra_body": {"dimensions": 512}})).json()
    r = await client.put("/api/v1/admin/routes/embedding", headers=ADMIN, json={"model_id": m["id"]})
    assert r.status_code == 200, r.text
    return m


async def test_requires_device_token(client):
    r = await client.post("/api/v1/embeddings", json={"input": ["hi"]})
    assert r.status_code == 401


async def test_not_configured_returns_503(client, device_headers):
    # 员工端据此退回字面匹配，不报错
    r = await client.post("/api/v1/embeddings", headers=device_headers, json={"input": ["发邮件"]})
    assert r.status_code == 503
    assert "embedding" in r.json()["error"]["message"]


async def test_rejects_bad_input(client, device_headers):
    await setup_embedding(client)
    assert (await client.post("/api/v1/embeddings", headers=device_headers, json={"input": [1, 2]})).status_code == 400
    assert (await client.post("/api/v1/embeddings", headers=device_headers, json={"input": ["x"] * 33})).status_code == 400


@respx.mock
async def test_forwards_to_upstream_embeddings_and_records_usage(client, device_headers):
    await setup_embedding(client)
    route = respx.post("http://bailian.local/v1/embeddings").mock(return_value=httpx.Response(200, json={
        "object": "list",
        "model": "text-embedding-v4",
        "data": [{"object": "embedding", "index": 0, "embedding": [0.1, 0.2]},
                 {"object": "embedding", "index": 1, "embedding": [0.3, 0.4]}],
        "usage": {"prompt_tokens": 7, "total_tokens": 7},
    }))
    r = await client.post("/api/v1/embeddings", headers=device_headers,
                          json={"model": "embedding", "input": ["发邮件", "Outlook 账户是 wang@corp"]})
    assert r.status_code == 200, r.text
    assert len(r.json()["data"]) == 2
    assert r.headers["x-flyknit-scene"] == "embedding"
    sent = route.calls.last.request
    assert sent.headers["authorization"] == "Bearer sk-emb-1234"
    body = json.loads(sent.content)
    assert body == {"model": "text-embedding-v4", "input": ["发邮件", "Outlook 账户是 wang@corp"], "dimensions": 512}

    usage = (await client.get("/api/v1/client/usage", headers=device_headers)).json()
    rows = [s for s in usage["by_scene"] if s["scene"] == "embedding"]
    assert rows and rows[0]["prompt"] == 7 and rows[0]["requests"] == 1


@respx.mock
async def test_upstream_error_is_passed_through(client, device_headers):
    await setup_embedding(client)
    respx.post("http://bailian.local/v1/embeddings").mock(return_value=httpx.Response(400, json={"error": {"message": "bad"}}))
    r = await client.post("/api/v1/embeddings", headers=device_headers, json={"input": ["x"]})
    assert r.status_code == 400


async def test_embedding_models_stay_out_of_the_chat_model_list(client, device_headers):
    await setup_embedding(client)
    models = (await client.get("/api/v1/client/models", headers=device_headers)).json()
    assert all("embedding" not in json.dumps(m, ensure_ascii=False).lower() for m in models)
    config = (await client.get("/api/v1/client/config", headers=device_headers)).json()
    scene = next(s for s in config["scenes"] if s["scene"] == "embedding")
    assert scene["available"] is True
