import httpx
import pytest
import respx

from conftest import ADMIN, setup_models

pytestmark = pytest.mark.asyncio


async def test_requires_device_token(client):
    r = await client.post("/api/v1/chat/completions", json={"messages": []})
    assert r.status_code == 401


async def test_register_rejects_wrong_key(client):
    r = await client.post("/api/v1/devices/register", json={"enrollment_key": "bad", "machine_name": "x"})
    assert r.status_code == 403


async def test_no_route_configured(client, device_headers):
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "agent", "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 503
    assert "尚未配置" in r.json()["error"]["message"]


@respx.mock
async def test_forwards_with_upstream_model_and_key(client, device_headers):
    await setup_models(client)
    route = respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"role": "assistant", "content": "你好"}}]})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "agent", "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200
    assert r.json()["choices"][0]["message"]["content"] == "你好"
    assert r.headers["x-flyknit-model"] == "Qwen3.5-397B"
    assert int(r.headers["x-flyknit-context"]) > 0  # 客户端据此决定何时压缩上下文
    sent = route.calls.last.request
    assert sent.headers["authorization"] == "Bearer sk-internal-123456"
    import json
    assert json.loads(sent.content)["model"] == "qwen3.5-397b"


@respx.mock
async def test_falls_back_when_primary_down(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(return_value=httpx.Response(503))
    backup = respx.post("http://backup.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "from backup"}}]})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "chat", "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200
    assert backup.called
    assert r.headers["x-flyknit-model"] == "Qwen-Plus"


@respx.mock
async def test_falls_back_on_connect_error(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(side_effect=httpx.ConnectError("refused"))
    respx.post("http://backup.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": []})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "chat", "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200


@respx.mock
async def test_streaming_passthrough(client, device_headers):
    await setup_models(client)
    sse = (
        b'data: {"choices":[{"delta":{"content":"Xin "}}]}\n\n'
        b'data: {"choices":[{"delta":{"content":"chao"}}]}\n\n'
        b"data: [DONE]\n\n"
    )
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, content=sse, headers={"content-type": "text/event-stream"})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "chat", "stream": True, "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200
    assert r.headers["content-type"].startswith("text/event-stream")
    assert r.content == sse


@respx.mock
async def test_upstream_client_error_is_passed_through(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(400, json={"error": {"message": "bad tools"}})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "chat", "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 400
    assert r.json()["error"]["message"] == "bad tools"


async def test_disabled_device_is_rejected(client, device_headers):
    devices = (await client.get("/api/v1/admin/devices", headers=ADMIN)).json()
    await client.patch(f"/api/v1/admin/devices/{devices[0]['id']}", headers=ADMIN, json={"disabled": True})
    r = await client.get("/api/v1/client/config", headers=device_headers)
    assert r.status_code == 403


@respx.mock
async def test_chinese_model_name_in_header(client, device_headers):
    """模型显示名为中文时，响应头不能导致 500（流式与非流式）。"""
    from urllib.parse import unquote

    await setup_models(client)
    models = (await client.get("/api/v1/admin/models", headers=ADMIN)).json()
    await client.patch(f"/api/v1/admin/models/{models[0]['id']}", headers=ADMIN, json={"name": "通义千问"})
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, content=b"data: [DONE]\n\n", headers={"content-type": "text/event-stream"})
    )
    for stream in (True, False):
        r = await client.post("/api/v1/chat/completions", headers=device_headers,
                              json={"model": "chat", "stream": stream, "messages": [{"role": "user", "content": "hi"}]})
        assert r.status_code == 200
        assert unquote(r.headers["x-flyknit-model"]) == "通义千问"


@respx.mock
async def test_client_selected_model_is_used_first(client, device_headers):
    m1, m2 = await setup_models(client)
    backup = respx.post("http://backup.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "plus"}}]})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json={
        "model": "agent", "flyknit_model_id": m2["id"], "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200
    import json
    sent = json.loads(backup.calls.last.request.content)
    assert sent["model"] == "qwen-plus"
    assert "flyknit_model_id" not in sent


@respx.mock
async def test_selected_model_falls_back_to_scene_model(client, device_headers):
    m1, m2 = await setup_models(client)
    respx.post("http://backup.local/v1/chat/completions").mock(return_value=httpx.Response(503))
    primary = respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": []}))
    r = await client.post("/api/v1/chat/completions", headers=device_headers, json={
        "model": "chat", "flyknit_model_id": m2["id"], "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200 and primary.called


async def test_client_models_lists_enabled_models(client, device_headers):
    m1, m2 = await setup_models(client)
    await client.patch(f"/api/v1/admin/models/{m2['id']}", headers=ADMIN, json={"enabled": False})
    r = await client.get("/api/v1/client/models", headers=device_headers)
    assert [m["name"] for m in r.json()] == ["Qwen3.5-397B"]
    assert r.json()[0]["provider"] == "内部"


async def test_client_models_hide_non_chat_models(client, device_headers):
    p = (await client.post("/api/v1/admin/providers", headers=ADMIN,
                           json={"name": "百炼", "base_url": "http://bailian.local/v1", "api_key": "sk-x"})).json()
    for model in ("qwen-plus", "text-embedding-v4", "paraformer-v2"):
        await client.post("/api/v1/admin/models", headers=ADMIN,
                          json={"provider_id": p["id"], "name": model, "model": model})
    r = await client.get("/api/v1/client/models", headers=device_headers)
    assert [m["model"] for m in r.json()] == ["qwen-plus"]


@respx.mock
async def test_sync_models_from_provider(client):
    p = (await client.post("/api/v1/admin/providers", headers=ADMIN,
                           json={"name": "百炼", "base_url": "http://bailian.local/v1", "api_key": "sk-abcdefgh"})).json()
    route = respx.get("http://bailian.local/v1/models").mock(return_value=httpx.Response(200, json={"data": [
        {"id": "qwen3.8-max"}, {"id": "qwen-plus"}, {"id": "text-embedding-v4"}, {"id": "qwen-vl-max"}, {"id": "cosyvoice-v2"},
        {"id": "qwen-plus-2025-12-01"},
    ]}))
    r = await client.post(f"/api/v1/admin/providers/{p['id']}/sync-models", headers=ADMIN)
    assert r.status_code == 200, r.text
    assert sorted(r.json()["added"]) == ["cosyvoice-v2", "qwen-plus", "qwen-vl-max", "qwen3.8-max", "text-embedding-v4"]
    assert r.json()["skipped"] == 1
    assert route.calls.last.request.headers["authorization"] == "Bearer sk-abcdefgh"
    models = {m["model"]: m for m in (await client.get("/api/v1/admin/models", headers=ADMIN)).json()}
    assert models["qwen-vl-max"]["supports_vision"] is True
    assert models["qwen-plus"]["supports_tools"] is True
    assert models["text-embedding-v4"]["supports_tools"] is False
    assert models["cosyvoice-v2"]["supports_tools"] is False
    # 再次同步不重复添加
    r = await client.post(f"/api/v1/admin/providers/{p['id']}/sync-models", headers=ADMIN)
    assert r.json()["added"] == []
