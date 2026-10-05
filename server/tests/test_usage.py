"""Token 用量统计与每日配额。"""

import httpx
import respx
from conftest import ADMIN, setup_models

from app.services import usage_store

STREAM = (
    b'data: {"choices":[{"delta":{"content":"\\u597d"}}]}\n\n'
    b'data: {"choices":[],"usage":{"prompt_tokens":1200,"completion_tokens":300,"total_tokens":1500}}\n\n'
    b"data: [DONE]\n\n"
)


def test_scanner_reads_usage_from_a_split_stream():
    scanner = usage_store.StreamUsageScanner()
    # 分片可能切在一行中间
    for i in range(0, len(STREAM), 7):
        scanner.feed(STREAM[i : i + 7])
    scanner.finish()
    assert (scanner.prompt, scanner.completion) == (1200, 300)


def test_scanner_does_not_double_count_repeated_usage():
    scanner = usage_store.StreamUsageScanner()
    scanner.feed(STREAM)
    scanner.feed(STREAM)
    scanner.finish()
    assert (scanner.prompt, scanner.completion) == (1200, 300)


def test_extract_usage_from_non_streaming_body():
    assert usage_store.extract_usage(b'{"usage":{"prompt_tokens":10,"completion_tokens":5}}') == (10, 5)
    assert usage_store.extract_usage(b"not json") == (0, 0)
    assert usage_store.extract_usage(b'{"choices":[]}') == (0, 0)


@respx.mock
async def test_streaming_usage_is_recorded_per_scene(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, content=STREAM, headers={"content-type": "text/event-stream"})
    )

    for scene in ("agent", "translate"):
        r = await client.post(
            "/api/v1/chat/completions",
            headers=device_headers,
            json={"model": scene, "stream": True, "messages": [{"role": "user", "content": "hi"}]},
        )
        assert r.status_code == 200
        await r.aread()

    usage = (await client.get("/api/v1/client/usage", headers=device_headers)).json()
    assert usage["today_tokens"] == 3000
    assert {s["scene"]: s["total"] for s in usage["by_scene"]} == {"agent": 1500, "translate": 1500}
    assert {s["scene"]: s["requests"] for s in usage["by_scene"]} == {"agent": 1, "translate": 1}
    assert usage["daily_limit"] == 0  # 默认不限制
    assert usage["exceeded"] is False
    assert usage["contact_email"] == "jamesyang@shenzhougroup.com"

    # 管理后台能看到每台电脑的汇总
    rows = (await client.get("/api/v1/admin/usage", headers=ADMIN)).json()
    assert rows[0]["tokens"] == 3000
    assert rows[0]["machine_name"] == "PC-001"


@respx.mock
async def test_non_streaming_usage_is_recorded(client, device_headers):
    await setup_models(client)
    respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}], "usage": {"prompt_tokens": 40, "completion_tokens": 10}})
    )
    await client.post("/api/v1/chat/completions", headers=device_headers,
                      json={"model": "chat", "messages": [{"role": "user", "content": "hi"}]})

    usage = (await client.get("/api/v1/client/usage", headers=device_headers)).json()
    assert usage["today_tokens"] == 50


@respx.mock
async def test_requests_are_rejected_once_the_daily_quota_is_used_up(client, device_headers):
    await setup_models(client)
    route = respx.post("http://primary.local/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}], "usage": {"prompt_tokens": 900, "completion_tokens": 200}})
    )
    await client.put("/api/v1/admin/quota", headers=ADMIN, json={"daily_tokens": 1000, "contact_phone": "7815"})

    first = await client.post("/api/v1/chat/completions", headers=device_headers,
                              json={"model": "chat", "messages": [{"role": "user", "content": "hi"}]})
    assert first.status_code == 200  # 第一次还没超

    second = await client.post("/api/v1/chat/completions", headers=device_headers,
                               json={"model": "chat", "messages": [{"role": "user", "content": "hi"}]})
    assert second.status_code == 429
    error = second.json()["error"]
    assert error["type"] == "quota_exceeded"
    assert "jamesyang@shenzhougroup.com" in error["message"]
    assert "7815" in error["message"]
    assert route.call_count == 1  # 超额后不再请求上游

    usage = (await client.get("/api/v1/client/usage", headers=device_headers)).json()
    assert usage["exceeded"] is True
    assert usage["remaining"] == 0

    # 管理员提高额度后恢复
    await client.put("/api/v1/admin/quota", headers=ADMIN, json={"daily_tokens": 100000})
    third = await client.post("/api/v1/chat/completions", headers=device_headers,
                              json={"model": "chat", "messages": [{"role": "user", "content": "hi"}]})
    assert third.status_code == 200


async def test_quota_defaults_and_update(client):
    quota = (await client.get("/api/v1/admin/quota", headers=ADMIN)).json()
    assert quota == {
        "daily_tokens": 0,
        "contact_name": "IT 管理员",
        "contact_email": "jamesyang@shenzhougroup.com",
        "contact_phone": "7815",
    }
    updated = (await client.put("/api/v1/admin/quota", headers=ADMIN, json={"daily_tokens": 500000})).json()
    assert updated["daily_tokens"] == 500000
    assert updated["contact_phone"] == "7815"  # 没传的字段保持不变


async def test_audit_records_the_scene(client, device_headers):
    await client.post(
        "/api/v1/audit",
        headers=device_headers,
        json={"items": [{
            "conversation_id": "c1", "tool_name": "run_shell", "arguments": "{}", "scene": "agent",
            "risk": "blocked", "decision": "blocked", "status": "skipped", "summary": "rm -rf /",
        }]},
    )
    rows = (await client.get("/api/v1/admin/audit", headers=ADMIN)).json()
    assert rows[0]["scene"] == "agent"
    assert rows[0]["decision"] == "blocked"
