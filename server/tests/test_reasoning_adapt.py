"""思考开关和思考内容回传：网关按模型配置适配各家写法，不按厂商写死。"""

import json

import httpx
import pytest
import respx

from app.services import model_router
from app.services.model_router import Target, build_body
from conftest import ADMIN, setup_models

pytestmark = pytest.mark.asyncio

HISTORY = [
    {"role": "user", "content": "查一下"},
    {"role": "assistant", "content": "", "reasoning_content": "先列目录",
     "tool_calls": [{"id": "c1", "type": "function", "function": {"name": "list_dir", "arguments": "{}"}}]},
    {"role": "tool", "tool_call_id": "c1", "content": "a.txt"},
]


def _target(**kw) -> Target:
    base = dict(model_id=1, display_name="m", upstream_model="up", url="http://x/v1/chat/completions",
                api_key="", extra_body={}, supports_vision=False)
    base.update(kw)
    return Target(**base)


async def test_enable_thinking_passes_through_by_default():
    out = build_body({"messages": HISTORY, "enable_thinking": True}, _target())
    assert out["enable_thinking"] is True
    assert "thinking" not in out
    # 默认原样转发思考内容
    assert out["messages"][1]["reasoning_content"] == "先列目录"


@pytest.mark.parametrize("on,expected", [(True, "enabled"), (False, "disabled")])
async def test_thinking_object_style(on, expected):
    out = build_body({"messages": [], "enable_thinking": on}, _target(thinking_param="thinking"))
    assert "enable_thinking" not in out
    assert out["thinking"] == {"type": expected}


async def test_chat_template_kwargs_style_keeps_other_kwargs():
    out = build_body({"messages": [], "enable_thinking": False},
                     _target(thinking_param="chat_template_kwargs",
                             extra_body={"chat_template_kwargs": {"foo": 1}}))
    assert "enable_thinking" not in out
    # 客户端的开关覆盖 extra_body 里的默认；模型 extra_body 里其他 kwargs 不在这里合并（setdefault 不覆盖已有键）
    assert out["chat_template_kwargs"]["enable_thinking"] is False


async def test_none_style_drops_flag():
    out = build_body({"messages": [], "enable_thinking": True}, _target(thinking_param="none"))
    assert "enable_thinking" not in out and "thinking" not in out


async def test_client_silent_keeps_model_default():
    # 员工没动开关（办事模式默认不传）：按模型 extra_body 的默认，不额外加东西
    out = build_body({"messages": []}, _target(thinking_param="thinking", extra_body={"thinking": {"type": "enabled"}}))
    assert out["thinking"] == {"type": "enabled"}


async def test_strip_reasoning_without_touching_original():
    body = {"messages": HISTORY}
    out = build_body(body, _target(reasoning_echo="strip"))
    assert all("reasoning_content" not in m for m in out["messages"])
    assert out["messages"][1]["tool_calls"][0]["id"] == "c1"
    # 原始请求（归档还要用）没被改
    assert HISTORY[1]["reasoning_content"] == "先列目录"


@respx.mock
async def test_admin_config_reaches_upstream(client, device_headers):
    m1, _ = await setup_models(client)
    r = await client.patch(f"/api/v1/admin/models/{m1['id']}", headers=ADMIN,
                           json={"thinking_param": "thinking", "reasoning_echo": "strip"})
    assert r.status_code == 200, r.text
    assert r.json()["thinking_param"] == "thinking"
    assert r.json()["reasoning_echo"] == "strip"
    route = respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}]})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "agent", "messages": HISTORY, "enable_thinking": True})
    assert r.status_code == 200, r.text
    sent = json.loads(route.calls.last.request.content)
    assert sent["thinking"] == {"type": "enabled"}
    assert "enable_thinking" not in sent
    assert "reasoning_content" not in sent["messages"][1]


async def test_admin_rejects_unknown_style(client):
    m1, _ = await setup_models(client)
    r = await client.patch(f"/api/v1/admin/models/{m1['id']}", headers=ADMIN, json={"thinking_param": "magic"})
    assert r.status_code == 422


async def test_new_models_default_to_compatible_behaviour(client):
    m1, _ = await setup_models(client)
    assert m1["thinking_param"] == "enable_thinking"
    assert m1["reasoning_echo"] == "auto"


@respx.mock
async def test_code_scene_falls_back_to_agent(client, device_headers):
    await setup_models(client)  # 只配了 chat、agent
    route = respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}]})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "code", "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200, r.text
    assert route.called


@respx.mock
async def test_code_scene_can_be_routed(client, device_headers):
    _, m2 = await setup_models(client)
    r = await client.put("/api/v1/admin/routes/code", headers=ADMIN, json={"model_id": m2["id"]})
    assert r.status_code == 200, r.text
    backup = respx.post("http://backup.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}]})
    )
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "code", "messages": [{"role": "user", "content": "hi"}]})
    assert r.status_code == 200, r.text
    assert backup.called
    assert "code" in model_router.SCENES
