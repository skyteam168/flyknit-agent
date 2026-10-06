import httpx
import pytest
import respx

from app.services import asr
from conftest import ADMIN

pytestmark = pytest.mark.asyncio

AUDIO = b"RIFF\x24\x00\x00\x00WAVEfmt " + b"\x00" * 40


@pytest.fixture(autouse=True)
def _forget():
    # 服务里会记住每个上游走通的是哪条路，用例之间不能互相影响
    asr.forget_learned()
    yield
    asr.forget_learned()


async def setup_asr(client, base_url="http://asr.local/api/v1", extra_body=None):
    p = (await client.post("/api/v1/admin/providers", headers=ADMIN,
                           json={"name": "百炼", "base_url": base_url, "api_key": "sk-asr-9999"})).json()
    m = (await client.post("/api/v1/admin/models", headers=ADMIN,
                           json={"provider_id": p["id"], "name": "Qwen3-ASR",
                                 "model": "qwen3-asr-flash", "extra_body": extra_body or {}})).json()
    r = await client.put("/api/v1/admin/routes/asr", headers=ADMIN, json={"model_id": m["id"]})
    assert r.status_code == 200, r.text
    return m


def post_audio(client, headers, audio=AUDIO, fmt="wav", **data):
    return client.post(
        "/api/v1/speech/transcribe",
        headers=headers,
        files={"audio": ("clip.wav", audio, "audio/wav")},
        data={"audio_format": fmt, **data},
    )


async def test_requires_device_token(client):
    r = await post_audio(client, {})
    assert r.status_code == 401


async def test_without_asr_model_configured(client, device_headers):
    r = await post_audio(client, device_headers)
    assert r.status_code == 503
    assert "语音识别模型" in r.json()["error"]["message"]


async def test_rejects_unknown_format(client, device_headers):
    r = await post_audio(client, device_headers, fmt="xyz")
    assert r.status_code == 400


async def test_rejects_empty_recording(client, device_headers):
    await setup_asr(client)
    r = await post_audio(client, device_headers, audio=b"")
    assert r.status_code == 400


@respx.mock
async def test_inline_base64_needs_no_url(client, device_headers):
    """录音直接以 base64 送上去，全程不出现任何 file_url。"""
    await setup_asr(client)
    route = respx.post("http://asr.local/compatible-mode/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={
            "choices": [{"message": {"role": "assistant", "content": "把九月的日报整理一下"}}],
            "usage": {"seconds": 6},
        })
    )
    r = await post_audio(client, device_headers)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["text"] == "把九月的日报整理一下"
    assert body["transport"] == "inline"
    assert body["duration_seconds"] == 6

    sent = route.calls.last.request
    assert sent.headers["authorization"] == "Bearer sk-asr-9999"
    payload = sent.read().decode()
    assert "data:audio/wav;base64," in payload
    assert "file_url" not in payload


@respx.mock
async def test_language_and_hotwords_are_passed(client, device_headers):
    await setup_asr(client, extra_body={"hotwords": "七号机台, 飞织鞋面"})
    route = respx.post("http://asr.local/compatible-mode/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "ok"}}]})
    )
    r = await post_audio(client, device_headers, language="vi")
    assert r.status_code == 200
    payload = route.calls.last.request.read().decode("utf-8")
    assert "vi" in payload
    assert "\\u4e03" in payload or "七号机台" in payload  # 热词进了 system 提示


@respx.mock
async def test_falls_back_to_file_upload_when_inline_unsupported(client, device_headers):
    """上游不吃内联音频时，自动改走临时文件上传，用户无感。"""
    await setup_asr(client)
    respx.post("http://asr.local/compatible-mode/v1/chat/completions").mock(
        return_value=httpx.Response(400, json={"error": {"message": "unknown field input_audio"}})
    )
    policy = respx.get("http://asr.local/api/v1/uploads").mock(
        return_value=httpx.Response(200, json={"data": {
            "upload_host": "http://oss.local/bucket",
            "upload_dir": "prod/abc",
            "policy": "cG9saWN5",
            "signature": "sig",
            "oss_access_key_id": "LTAI-test",
            "x_oss_object_acl": "private",
        }})
    )
    upload = respx.post("http://oss.local/bucket").mock(return_value=httpx.Response(200))
    submit = respx.post("http://asr.local/api/v1/services/audio/asr/transcription").mock(
        return_value=httpx.Response(200, json={"output": {"task_id": "task-1", "task_status": "PENDING"}})
    )
    respx.get("http://asr.local/api/v1/tasks/task-1").mock(
        return_value=httpx.Response(200, json={
            "output": {
                "task_id": "task-1", "task_status": "SUCCEEDED",
                "result": {"transcription_url": "http://result.local/t.json"},
            },
            "usage": {"seconds": 35},
        })
    )
    respx.get("http://result.local/t.json").mock(
        return_value=httpx.Response(200, json={"transcripts": [
            {"channel_id": 0, "text": "今天这节课",
             "sentences": [{"sentence_id": 0, "language": "zh", "text": "今天这节课"}]}
        ]})
    )

    r = await post_audio(client, device_headers)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["text"] == "今天这节课"
    assert body["transport"] == "filetrans"
    assert body["language"] == "zh"
    assert body["duration_seconds"] == 35

    assert policy.calls.last.request.url.params["action"] == "getPolicy"
    assert upload.called
    # 提交任务时必须带这个头，否则上游不会去解析 oss:// 地址
    assert submit.calls.last.request.headers["x-dashscope-ossresourceresolve"] == "enable"
    assert "oss://prod/abc/" in submit.calls.last.request.read().decode()


@respx.mock
async def test_remembers_the_working_transport(client, device_headers):
    """第一次试错之后，后面不该再白跑一次 inline。"""
    await setup_asr(client)
    inline = respx.post("http://asr.local/compatible-mode/v1/chat/completions").mock(
        return_value=httpx.Response(400, json={"error": {"message": "unsupported input_audio"}})
    )
    respx.get("http://asr.local/api/v1/uploads").mock(
        return_value=httpx.Response(200, json={"data": {
            "upload_host": "http://oss.local/bucket", "upload_dir": "d", "policy": "p",
            "signature": "s", "oss_access_key_id": "k",
        }})
    )
    respx.post("http://oss.local/bucket").mock(return_value=httpx.Response(200))
    respx.post("http://asr.local/api/v1/services/audio/asr/transcription").mock(
        return_value=httpx.Response(200, json={"output": {"task_id": "t", "task_status": "PENDING"}})
    )
    respx.get("http://asr.local/api/v1/tasks/t").mock(
        return_value=httpx.Response(200, json={"output": {
            "task_status": "SUCCEEDED", "result": {"transcription_url": "http://result.local/x.json"}}})
    )
    respx.get("http://result.local/x.json").mock(
        return_value=httpx.Response(200, json={"transcripts": [{"text": "好"}]})
    )

    assert (await post_audio(client, device_headers)).status_code == 200
    assert inline.call_count == 1
    assert (await post_audio(client, device_headers)).status_code == 200
    assert inline.call_count == 1  # 第二次没有再试 inline


@respx.mock
async def test_explicit_transport_skips_probing(client, device_headers):
    await setup_asr(client, extra_body={"asr_transport": "filetrans"})
    inline = respx.post("http://asr.local/compatible-mode/v1/chat/completions")
    respx.get("http://asr.local/api/v1/uploads").mock(
        return_value=httpx.Response(200, json={"data": {
            "upload_host": "http://oss.local/b", "upload_dir": "d", "policy": "p",
            "signature": "s", "oss_access_key_id": "k"}})
    )
    respx.post("http://oss.local/b").mock(return_value=httpx.Response(200))
    respx.post("http://asr.local/api/v1/services/audio/asr/transcription").mock(
        return_value=httpx.Response(200, json={"output": {"task_id": "t", "task_status": "PENDING"}})
    )
    respx.get("http://asr.local/api/v1/tasks/t").mock(
        return_value=httpx.Response(200, json={"output": {
            "task_status": "SUCCEEDED", "result": {"transcription_url": "http://r.local/x.json"}}})
    )
    respx.get("http://r.local/x.json").mock(
        return_value=httpx.Response(200, json={"transcripts": [{"text": "直接走文件"}]})
    )
    r = await post_audio(client, device_headers)
    assert r.status_code == 200
    assert r.json()["text"] == "直接走文件"
    assert not inline.called


@respx.mock
async def test_upstream_failure_is_explained_in_chinese(client, device_headers):
    await setup_asr(client)
    respx.post("http://asr.local/compatible-mode/v1/chat/completions").mock(
        return_value=httpx.Response(401, json={"error": {"message": "invalid api key"}})
    )
    r = await post_audio(client, device_headers)
    assert r.status_code == 502
    assert "没有授权" in r.json()["error"]["message"]


@respx.mock
async def test_task_failure_surfaces_reason(client, device_headers):
    await setup_asr(client, extra_body={"asr_transport": "filetrans"})
    respx.get("http://asr.local/api/v1/uploads").mock(
        return_value=httpx.Response(200, json={"data": {
            "upload_host": "http://oss.local/b", "upload_dir": "d", "policy": "p",
            "signature": "s", "oss_access_key_id": "k"}})
    )
    respx.post("http://oss.local/b").mock(return_value=httpx.Response(200))
    respx.post("http://asr.local/api/v1/services/audio/asr/transcription").mock(
        return_value=httpx.Response(200, json={"output": {"task_id": "t", "task_status": "PENDING"}})
    )
    respx.get("http://asr.local/api/v1/tasks/t").mock(
        return_value=httpx.Response(200, json={"output": {"task_status": "FAILED", "message": "audio too short"}})
    )
    r = await post_audio(client, device_headers)
    assert r.status_code == 502
    assert "audio too short" in r.json()["error"]["message"]


@respx.mock
async def test_missing_upload_policy_is_reported(client, device_headers):
    await setup_asr(client, extra_body={"asr_transport": "filetrans"})
    respx.get("http://asr.local/api/v1/uploads").mock(return_value=httpx.Response(200, json={"data": {}}))
    r = await post_audio(client, device_headers)
    assert r.status_code == 502
    assert "上传凭证" in r.json()["error"]["message"]


@respx.mock
async def test_counts_a_request_without_tokens(client, device_headers):
    await setup_asr(client)
    respx.post("http://asr.local/compatible-mode/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "嗯"}}]})
    )
    assert (await post_audio(client, device_headers)).status_code == 200
    usage = (await client.get("/api/v1/client/usage", headers=device_headers)).json()
    assert usage["today_tokens"] == 0  # 语音不按 token 计，不能污染配额
    asr_rows = [s for s in usage["by_scene"] if s["scene"] == "asr"]
    assert asr_rows and asr_rows[0]["requests"] == 1


@respx.mock
async def test_base_url_spellings_both_work(client, device_headers):
    """管理员把地址填成 /compatible-mode/v1 或 /api/v1 都要能用。"""
    await setup_asr(client, base_url="http://asr.local/compatible-mode/v1")
    route = respx.post("http://asr.local/compatible-mode/v1/chat/completions").mock(
        return_value=httpx.Response(200, json={"choices": [{"message": {"content": "可以"}}]})
    )
    r = await post_audio(client, device_headers)
    assert r.status_code == 200
    assert route.called
