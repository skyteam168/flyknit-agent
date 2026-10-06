"""语音转文字。

百炼的 ASR 有两种用法，这里都实现了，按顺序尝试，不把任何一条写死：

1. inline —— 走 OpenAI 兼容的 /chat/completions，音频用 base64 直接放进请求体。
   录音输入都是几十秒的短音频，这条路一次往返就出结果，**不需要任何 URL**。
2. filetrans —— qwen3-asr-flash-filetrans 这类只收 file_url 的接口。
   内网没有公网可访问的地址，所以先用百炼自己的临时文件上传换一个 oss:// 地址
   （GET /api/v1/uploads?action=getPolicy），再带 X-DashScope-OssResourceResolve: enable
   提交任务并轮询。文件存在百炼侧，48 小时自动失效，不用自建 OSS。

选哪条由模型配置里的 extra_body.asr_transport 决定（inline / filetrans / auto）。
auto 的含义是：先 inline，遇到“不支持”类错误再退到 filetrans，并把结果记下来，
同一个模型下次直接走对的那条，不用每次都试错。
"""

from __future__ import annotations

import asyncio
import base64
import json
import logging
import re
from dataclasses import dataclass, field
from typing import Any
from urllib.parse import urlparse

import httpx

log = logging.getLogger("flyknit.asr")

# 整段音频的上限。录音输入不该出现超长的东西，超了说明是误操作
MAX_AUDIO_BYTES = 25 * 1024 * 1024
# filetrans 轮询：总共最多等这么久
POLL_TIMEOUT_SECONDS = 120.0
POLL_INTERVAL_SECONDS = 0.6

# 音频格式 → MIME，供 base64 的 data URI 使用
_MIME = {
    "wav": "audio/wav",
    "mp3": "audio/mpeg",
    "m4a": "audio/mp4",
    "aac": "audio/aac",
    "ogg": "audio/ogg",
    "opus": "audio/opus",
    "flac": "audio/flac",
    "amr": "audio/amr",
    "webm": "audio/webm",
}

# 上游说“我不吃内联音频”的几种表达。命中就换 filetrans，而不是把错误抛给用户
_UNSUPPORTED = re.compile(
    r"input_audio|not support|unsupported|invalid.*content|unknown field|"
    r"audio.*required|file_url|InvalidParameter",
    re.IGNORECASE,
)


class AsrError(Exception):
    """转写失败，message 是能直接给用户看的中文。"""

    def __init__(self, message: str, *, retryable: bool = False):
        super().__init__(message)
        self.retryable = retryable


@dataclass
class AsrTarget:
    """一个可用的 ASR 上游。字段都来自数据库里的模型配置，没有硬编码。"""

    model: str
    base_url: str
    api_key: str
    transport: str = "auto"  # inline / filetrans / auto
    language: str | None = None
    hotwords: list[str] = field(default_factory=list)
    display_name: str = ""

    @property
    def root(self) -> str:
        """把 .../api/v1 或 .../compatible-mode/v1 收敛成服务根地址。"""
        u = self.base_url.rstrip("/")
        for suffix in ("/compatible-mode/v1", "/api/v1", "/v1"):
            if u.endswith(suffix):
                return u[: -len(suffix)]
        return u


@dataclass
class AsrResult:
    text: str
    language: str = ""
    duration_seconds: int = 0
    transport: str = ""


def _headers(target: AsrTarget, extra: dict[str, str] | None = None) -> dict[str, str]:
    h = {"Authorization": f"Bearer {target.api_key}", "Content-Type": "application/json"}
    if extra:
        h.update(extra)
    return h


def _prompt(target: AsrTarget) -> str | None:
    """热词：机台号、工序名这些，喂给模型能显著减少同音字错误。"""
    if not target.hotwords:
        return None
    return "以下是本次音频可能出现的词汇：" + "、".join(target.hotwords[:200])


# --------------------------------------------------------------------------
# 路线 1：内联 base64
# --------------------------------------------------------------------------

async def _inline(client: httpx.AsyncClient, target: AsrTarget, audio: bytes, fmt: str) -> AsrResult:
    data = base64.b64encode(audio).decode("ascii")
    mime = _MIME.get(fmt, "audio/wav")
    content: list[dict[str, Any]] = [
        {"type": "input_audio", "input_audio": {"data": f"data:{mime};base64,{data}", "format": fmt}}
    ]
    messages: list[dict[str, Any]] = []
    if (p := _prompt(target)) is not None:
        messages.append({"role": "system", "content": [{"type": "text", "text": p}]})
    messages.append({"role": "user", "content": content})

    body: dict[str, Any] = {"model": target.model, "messages": messages}
    if target.language:
        body["asr_options"] = {"language": target.language, "enable_itn": True}

    url = target.root + "/compatible-mode/v1/chat/completions"
    resp = await client.post(url, headers=_headers(target), json=body)
    if resp.status_code >= 400:
        raise _from_status(resp, "inline")

    payload = resp.json()
    choices = payload.get("choices") or []
    if not choices:
        raise AsrError("语音识别没有返回结果")
    message = choices[0].get("message") or {}
    text = message.get("content")
    if isinstance(text, list):  # 有的实现把内容拆成分片
        text = "".join(p.get("text", "") for p in text if isinstance(p, dict))
    usage = payload.get("usage") or {}
    return AsrResult(
        text=(text or "").strip(),
        language=target.language or "",
        duration_seconds=int(usage.get("seconds") or usage.get("audio_seconds") or 0),
        transport="inline",
    )


# --------------------------------------------------------------------------
# 路线 2：临时文件上传 + 异步任务轮询
# --------------------------------------------------------------------------

async def _upload(client: httpx.AsyncClient, target: AsrTarget, audio: bytes, fmt: str) -> str:
    """把音频传到百炼的临时空间，换回一个 oss:// 地址。48 小时后自动失效。"""
    policy_url = f"{target.root}/api/v1/uploads"
    resp = await client.get(
        policy_url,
        params={"action": "getPolicy", "model": target.model},
        headers={"Authorization": f"Bearer {target.api_key}"},
    )
    if resp.status_code >= 400:
        raise _from_status(resp, "getPolicy")
    data = (resp.json() or {}).get("data") or {}
    required = ("upload_host", "upload_dir", "policy", "signature", "oss_access_key_id")
    missing = [k for k in required if not data.get(k)]
    if missing:
        raise AsrError(f"上游没有返回上传凭证（缺 {', '.join(missing)}），无法上传录音")

    key = f"{data['upload_dir']}/flyknit-{abs(hash(audio)) & 0xFFFFFFFF:08x}.{fmt}"
    form = {
        "key": key,
        "policy": data["policy"],
        "OSSAccessKeyId": data["oss_access_key_id"],
        "signature": data["signature"],
        "success_action_status": "200",
    }
    if acl := data.get("x_oss_object_acl"):
        form["x-oss-object-acl"] = acl
    if forbid := data.get("x_oss_forbid_overwrite"):
        form["x-oss-forbid-overwrite"] = forbid

    up = await client.post(
        data["upload_host"],
        data=form,
        files={"file": (key.rsplit("/", 1)[-1], audio, _MIME.get(fmt, "application/octet-stream"))},
    )
    if up.status_code >= 400:
        raise AsrError(f"录音上传失败（HTTP {up.status_code}）", retryable=True)
    return f"oss://{key}"


async def _filetrans(client: httpx.AsyncClient, target: AsrTarget, audio: bytes, fmt: str) -> AsrResult:
    file_url = await _upload(client, target, audio, fmt)
    headers = _headers(
        target,
        {
            # 没有这个头，上游不会去解析 oss:// 地址
            "X-DashScope-OssResourceResolve": "enable",
            "X-DashScope-Async": "enable",
        },
    )
    body: dict[str, Any] = {
        "model": target.model,
        "input": {"file_url": file_url},
        "parameters": {"channel_id": [0], "enable_itn": True},
    }
    if target.language:
        body["parameters"]["language_hints"] = [target.language]

    submit = await client.post(
        f"{target.root}/api/v1/services/audio/asr/transcription", headers=headers, json=body
    )
    if submit.status_code >= 400:
        raise _from_status(submit, "filetrans.submit")
    task_id = ((submit.json() or {}).get("output") or {}).get("task_id")
    if not task_id:
        raise AsrError("上游没有返回任务号")

    payload = await _poll(client, target, task_id)
    out = payload.get("output") or {}
    result = (out.get("result") or {}).get("transcription_url")
    if not result:
        raise AsrError("转写完成但没有拿到结果地址")
    return _parse_transcription(await _fetch_transcription(client, result), payload)


async def _poll(client: httpx.AsyncClient, target: AsrTarget, task_id: str) -> dict[str, Any]:
    """返回整个响应体：usage 是 output 的兄弟字段，不在 output 里面。"""
    url = f"{target.root}/api/v1/tasks/{task_id}"
    headers = {"Authorization": f"Bearer {target.api_key}"}
    deadline = asyncio.get_running_loop().time() + POLL_TIMEOUT_SECONDS
    while True:
        resp = await client.get(url, headers=headers)
        if resp.status_code >= 400:
            raise _from_status(resp, "filetrans.poll")
        payload = resp.json() or {}
        out = payload.get("output") or {}
        status = (out.get("task_status") or "").upper()
        if status == "SUCCEEDED":
            return payload
        if status in ("FAILED", "CANCELED", "UNKNOWN"):
            reason = out.get("message") or out.get("code") or status
            raise AsrError(f"转写失败：{reason}")
        if asyncio.get_running_loop().time() >= deadline:
            raise AsrError("转写超时，请再说一次", retryable=True)
        await asyncio.sleep(POLL_INTERVAL_SECONDS)


async def _fetch_transcription(client: httpx.AsyncClient, url: str) -> dict[str, Any]:
    # 结果地址是带签名的公网地址，和上游同一张网，走同一个 client（代理设置一致）
    if urlparse(url).scheme not in ("http", "https"):
        raise AsrError("结果地址不合法")
    resp = await client.get(url)
    if resp.status_code >= 400:
        raise AsrError(f"下载转写结果失败（HTTP {resp.status_code}）", retryable=True)
    try:
        return resp.json()
    except (ValueError, json.JSONDecodeError) as exc:  # pragma: no cover - 上游格式异常
        raise AsrError("转写结果不是合法的 JSON") from exc


def _parse_transcription(doc: dict[str, Any], payload: dict[str, Any]) -> AsrResult:
    """把 transcripts[].text 拼起来。多声道时按声道顺序接在一起。"""
    parts: list[str] = []
    language = ""
    for tr in doc.get("transcripts") or []:
        if text := (tr.get("text") or "").strip():
            parts.append(text)
        for s in tr.get("sentences") or []:
            if not language and (lang := s.get("language")):
                language = lang
    usage = payload.get("usage") if isinstance(payload.get("usage"), dict) else {}
    return AsrResult(
        text="\n".join(parts).strip(),
        language=language,
        duration_seconds=int((usage or {}).get("seconds") or 0),
        transport="filetrans",
    )


# --------------------------------------------------------------------------

def _from_status(resp: httpx.Response, where: str) -> AsrError:
    try:
        payload = resp.json()
    except ValueError:
        payload = {}
    message = ""
    if isinstance(payload, dict):
        err = payload.get("error")
        if isinstance(err, dict):
            message = err.get("message") or ""
        message = message or payload.get("message") or payload.get("code") or ""
    message = str(message or resp.text or "")[:300]
    log.warning("ASR %s 失败 status=%s %s", where, resp.status_code, message)
    if resp.status_code in (401, 403):
        return AsrError("语音服务没有授权，请联系 IT 检查 ASR 模型的密钥")
    if resp.status_code == 429:
        return AsrError("语音服务繁忙，请稍后再说一次", retryable=True)
    return AsrError(f"语音识别失败：{message or f'HTTP {resp.status_code}'}", retryable=resp.status_code >= 500)


def _should_fallback(exc: AsrError) -> bool:
    """inline 被拒，是“这个上游不吃内联音频”还是真的出错了？"""
    return not exc.retryable and bool(_UNSUPPORTED.search(str(exc)))


# 记住每个上游实际走通的是哪条路，避免每次都先试错一次
_learned: dict[str, str] = {}


def learned_transport(target: AsrTarget) -> str | None:
    return _learned.get(f"{target.root}|{target.model}")


def forget_learned() -> None:
    _learned.clear()


async def transcribe(client: httpx.AsyncClient, target: AsrTarget, audio: bytes, fmt: str) -> AsrResult:
    """把一段音频转成文字。音频格式用扩展名表示（wav / mp3 / ...）。"""
    if not audio:
        raise AsrError("录音是空的")
    if len(audio) > MAX_AUDIO_BYTES:
        raise AsrError(f"录音太大（{len(audio) / 1024 / 1024:.1f} MB），请分段说")
    if not target.api_key:
        raise AsrError("语音服务没有配置密钥，请联系 IT")

    key = f"{target.root}|{target.model}"
    transport = target.transport if target.transport in ("inline", "filetrans") else (_learned.get(key) or "auto")

    if transport == "filetrans":
        return await _filetrans(client, target, audio, fmt)
    if transport == "inline":
        return await _inline(client, target, audio, fmt)

    try:
        result = await _inline(client, target, audio, fmt)
    except AsrError as exc:
        if not _should_fallback(exc):
            raise
        log.info("内联音频不被支持，改走文件上传：%s", exc)
        result = await _filetrans(client, target, audio, fmt)
    _learned[key] = result.transport
    return result
