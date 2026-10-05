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
