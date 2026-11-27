"""OpenTelemetry 导出（docs/observability.md）：总开关、只有超级管理员能改、请求头不回显、OTLP JSON 格式、不带内容、同一任务同一条 trace。"""

import json

import httpx
import pytest
import respx

from app.services import telemetry
from conftest import ADMIN, setup_models
from test_instructions import dispatcher

pytestmark = pytest.mark.asyncio

COLLECTOR = "http://otel.local:4318/v1/traces"


@pytest.fixture(autouse=True)
def fresh_state():
    telemetry.state.__init__()
    yield
    telemetry.state.__init__()


def _spans(route) -> list[dict]:
    out = []
    for call in route.calls:
        body = json.loads(call.request.content)
        for rs in body["resourceSpans"]:
            for ss in rs["scopeSpans"]:
                out.extend(ss["spans"])
    return out


def _attrs(span: dict) -> dict:
    out = {}
    for a in span["attributes"]:
        v = a["value"]
        out[a["key"]] = v.get("stringValue", v.get("intValue", v.get("boolValue", v.get("doubleValue"))))
    return out


async def _enable(client, **extra):
    r = await client.put("/api/v1/admin/observability", headers=ADMIN, json={
        "enabled": True, "endpoint": "http://otel.local:4318", "headers": [{"name": "Authorization", "value": "Bearer otel-secret-xyz"}],
        **extra})
    assert r.status_code == 200, r.text
    return r.json()


async def test_only_owners_change_it_and_header_values_never_come_back(client):
    await dispatcher(client, "it.boss")  # 第一个账号自动是超级管理员
    ops = await dispatcher(client)
    cfg = (await client.get("/api/v1/admin/observability", headers=ops)).json()
    assert cfg["enabled"] is False and cfg["endpoint"] == ""
    assert (await client.put("/api/v1/admin/observability", headers=ops, json={"enabled": False})).status_code == 403
    assert (await client.put("/api/v1/admin/observability", headers=ADMIN, json={"enabled": True})).status_code == 400
    assert (await client.put("/api/v1/admin/observability", headers=ADMIN, json={"endpoint": "otel.local"})).status_code == 422

    saved = await _enable(client)
    assert saved["headers"] == [{"name": "Authorization", "has_value": True}]
    assert saved["traces_url"] == COLLECTOR
    assert "otel-secret" not in json.dumps((await client.get("/api/v1/admin/observability", headers=ops)).json())
    # 不传值 = 保持原来的密钥
    again = (await client.put("/api/v1/admin/observability", headers=ADMIN, json={
        "enabled": True, "endpoint": "http://otel.local:4318", "headers": [{"name": "Authorization"}]})).json()
    assert again["headers"] == [{"name": "Authorization", "has_value": True}]


@respx.mock
async def test_model_and_tool_calls_become_spans_without_content(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(return_value=httpx.Response(200, json={
        "choices": [{"message": {"role": "assistant", "content": "机密回答 ABC"}}],
        "usage": {"prompt_tokens": 120, "completion_tokens": 30}}))
    collector = respx.post(COLLECTOR).mock(return_value=httpx.Response(200, json={}))

    # 关着：什么都不进队列
    await client.post("/api/v1/chat/completions", headers=device_headers,
                      json={"model": "agent", "messages": [{"role": "user", "content": "机密问题 XYZ"}], "conversation_id": "conv-1"})
    assert telemetry.stats()["queued"] == 0

    await _enable(client)
    r = await client.post("/api/v1/chat/completions", headers=device_headers,
                          json={"model": "agent", "messages": [{"role": "user", "content": "机密问题 XYZ"}], "conversation_id": "conv-1"})
    assert r.status_code == 200
    r = await client.post("/api/v1/audit", headers=device_headers, json={"items": [
        {"conversation_id": "conv-1", "tool_name": "run_shell", "arguments": '{"command": "del secret.txt"}', "risk": "confirm",
         "decision": "rejected", "status": "skipped", "summary": "机密结果", "task_id": "task-9"},
        {"conversation_id": "conv-1", "tool_name": "read_file", "arguments": '{"path": "D:/机密.xlsx"}', "risk": "auto",
         "decision": "auto", "status": "ok", "summary": "机密内容", "task_id": "task-9"},
    ]})
    assert r.status_code == 204
    assert await telemetry.flush() == 3

    raw = collector.calls.last.request
    assert raw.headers["Authorization"] == "Bearer otel-secret-xyz"
    text = raw.content.decode()
    for secret in ("机密问题", "机密回答", "机密结果", "机密内容", "secret.txt", "PC-001", "nguyen"):
        assert secret not in text  # 不带内容、参数、结果；默认也不带电脑名和使用者
    body = json.loads(text)
    assert {"key": "service.name", "value": {"stringValue": "flyknitbuddy"}} in body["resourceSpans"][0]["resource"]["attributes"]

    spans = _spans(collector)
    chat = next(s for s in spans if s["name"].startswith("chat "))
    a = _attrs(chat)
    assert chat["kind"] == 3 and chat["status"] == {"code": 1}
    assert a["gen_ai.request.model"] == "Qwen3.5-397B" and a["gen_ai.usage.input_tokens"] == "120" and a["gen_ai.usage.output_tokens"] == "30"
    assert a["flyknit.scene"] == "agent" and a["flyknit.conversation_id"] == "conv-1"
    assert int(chat["endTimeUnixNano"]) >= int(chat["startTimeUnixNano"])

    shell, read = (next(s for s in spans if s["name"] == f"execute_tool {n}") for n in ("run_shell", "read_file"))
    assert _attrs(shell)["flyknit.tool.decision"] == "rejected" and shell["status"]["code"] == 2
    assert read["status"] == {"code": 1} and _attrs(read)["gen_ai.tool.name"] == "read_file"
    assert shell["traceId"] == read["traceId"] and len(shell["traceId"]) == 32  # 同一个任务同一条 trace
    assert len({s["spanId"] for s in spans}) == 3


@respx.mock
async def test_identity_is_opt_in_and_failures_never_block(client, device_headers):
    collector = respx.post(COLLECTOR).mock(return_value=httpx.Response(503, text="busy"))
    await _enable(client, include_identity=True)
    await client.post("/api/v1/audit", headers=device_headers, json={"items": [
        {"conversation_id": "c", "tool_name": "list_dir", "arguments": "{}", "risk": "auto", "decision": "auto", "status": "ok"}]})
    assert await telemetry.flush() == 0  # 采集器挂了：这批丢掉、记下原因，不重试不堆积
    stats = telemetry.stats()
    assert stats["queued"] == 0 and stats["dropped"] == 1 and "503" in stats["last_error"]
    assert "PC-001" in collector.calls.last.request.content.decode()  # 打开了「带上电脑和使用者」

    collector.mock(return_value=httpx.Response(200))
    t = (await client.post("/api/v1/admin/observability/test", headers=ADMIN)).json()
    assert t["ok"] is True and t["error"] == ""


async def test_queue_is_bounded(client):
    telemetry.state.config = {**telemetry.DEFAULTS, "enabled": True, "endpoint": "http://otel.local:4318"}
    for i in range(telemetry.MAX_QUEUE + 10):
        telemetry.emit("x", i, i, {"i": i})
    s = telemetry.stats()
    assert s["queued"] == telemetry.MAX_QUEUE and s["dropped"] == 10
