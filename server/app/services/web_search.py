"""手机对话的联网检索：服务端出网，把结果塞进模型上下文。

提供商三选一（管理后台配置）：博查 Bocha（国内）、Serper / Tavily（国际）。
密钥用 crypto 加密后进 settings 表，不落明文。
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from urllib.parse import urlparse

import httpx
from sqlalchemy.ext.asyncio import AsyncSession

from ..crypto import decrypt, encrypt, mask
from . import settings_store

log = logging.getLogger("flyknit.web_search")

KEY = "web_search"
PROVIDERS = ("bocha", "serper", "tavily")
DEFAULT = {"enabled": False, "provider": "bocha", "api_key_enc": "", "count": 5}
TIMEOUT = 8.0
MAX_QUERY = 500
MAX_SNIPPET = 400


@dataclass(frozen=True)
class Hit:
    title: str
    url: str
    snippet: str


class SearchError(Exception):
    """检索失败：配置不对或上游不可达。"""


async def get_config(session: AsyncSession) -> dict:
    cfg = await settings_store.get_value(session, KEY, DEFAULT)
    for k, v in DEFAULT.items():
        cfg.setdefault(k, v)
    if cfg.get("provider") not in PROVIDERS:
        cfg["provider"] = "bocha"
    try:
        cfg["count"] = max(1, min(10, int(cfg.get("count") or 5)))
    except (TypeError, ValueError):
        cfg["count"] = 5
    cfg["enabled"] = bool(cfg.get("enabled"))
    return cfg


def api_key_of(cfg: dict) -> str:
    try:
        return decrypt(cfg.get("api_key_enc") or "")
    except ValueError:
        return ""


def is_ready(cfg: dict) -> bool:
    return bool(cfg.get("enabled")) and bool(api_key_of(cfg))


def public_out(cfg: dict) -> dict:
    key = api_key_of(cfg)
    return {
        "enabled": bool(cfg.get("enabled")),
        "provider": cfg.get("provider") or "bocha",
        "count": int(cfg.get("count") or 5),
        "api_key_masked": mask(key),
        "api_key_set": bool(key),
        "ready": is_ready(cfg),
    }


async def save_config(
    session: AsyncSession,
    *,
    enabled: bool,
    provider: str,
    count: int,
    api_key: str | None,
) -> dict:
    cfg = await get_config(session)
    if provider not in PROVIDERS:
        raise SearchError(f"不支持的提供商：{provider}")
    cfg["enabled"] = bool(enabled)
    cfg["provider"] = provider
    cfg["count"] = max(1, min(10, int(count)))
    if api_key is not None:
        cfg["api_key_enc"] = encrypt(api_key) if api_key else ""
    await settings_store.set_value(session, KEY, cfg)
    return await get_config(session)


def last_user_query(messages: list[dict]) -> str:
    """从拼好的 messages 里抽出最后一条用户纯文字（忽略图）。"""
    for m in reversed(messages):
        if m.get("role") != "user":
            continue
        content = m.get("content")
        if isinstance(content, str):
            return content.strip()[:MAX_QUERY]
        if isinstance(content, list):
            parts: list[str] = []
            for part in content:
                if isinstance(part, dict) and part.get("type") == "text":
                    t = str(part.get("text") or "").strip()
                    if t:
                        parts.append(t)
            text = "\n".join(parts).strip()
            if text:
                return text[:MAX_QUERY]
    return ""


def format_for_prompt(hits: list[Hit]) -> str:
    if not hits:
        return ""
    lines = [
        "以下是刚刚联网检索到的网页摘要。请优先依据这些结果回答；",
        "若与你已知信息冲突，以检索结果为准。在关键结论处用（来源1）这类标注引用。",
        "",
    ]
    for i, h in enumerate(hits, 1):
        lines.append(f"来源{i}：{h.title}")
        lines.append(f"链接：{h.url}")
        if h.snippet:
            lines.append(f"摘要：{h.snippet}")
        lines.append("")
    return "\n".join(lines).strip()


def hits_out(hits: list[Hit]) -> list[dict]:
    return [{"title": h.title, "url": h.url, "snippet": h.snippet} for h in hits]


async def search(http: httpx.AsyncClient, cfg: dict, query: str) -> list[Hit]:
    q = (query or "").strip()[:MAX_QUERY]
    if not q:
        raise SearchError("没有可检索的文字")
    if not is_ready(cfg):
        raise SearchError("管理员未开启联网搜索或未配置密钥")
    key = api_key_of(cfg)
    provider = cfg.get("provider") or "bocha"
    count = int(cfg.get("count") or 5)
    try:
        if provider == "bocha":
            return await _bocha(http, key, q, count)
        if provider == "serper":
            return await _serper(http, key, q, count)
        if provider == "tavily":
            return await _tavily(http, key, q, count)
    except httpx.HTTPError as exc:
        log.warning("联网搜索上游失败 provider=%s: %s", provider, exc)
        raise SearchError(f"搜索服务暂时不可用：{exc}") from exc
    raise SearchError(f"不支持的提供商：{provider}")


def _clip(text: object, n: int = MAX_SNIPPET) -> str:
    s = str(text or "").strip()
    return s if len(s) <= n else s[: n - 1] + "…"


def _clean_hits(raw: list[Hit], count: int) -> list[Hit]:
    out: list[Hit] = []
    seen: set[str] = set()
    for h in raw:
        url = (h.url or "").strip()
        if not url.startswith(("http://", "https://")) or url in seen:
            continue
        seen.add(url)
        title = _clip(h.title, 120) or urlparse(url).netloc or url
        out.append(Hit(title=title, url=url, snippet=_clip(h.snippet)))
        if len(out) >= count:
            break
    return out


async def _bocha(http: httpx.AsyncClient, key: str, query: str, count: int) -> list[Hit]:
    r = await http.post(
        "https://api.bochaai.com/v1/web-search",
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
        json={"query": query, "count": count, "summary": True, "freshness": "noLimit"},
        timeout=TIMEOUT,
    )
    if r.status_code >= 400:
        raise SearchError(f"博查返回错误（HTTP {r.status_code}）")
    data = r.json() if r.content else {}
    pages = (((data.get("data") or {}) if isinstance(data, dict) else {}).get("webPages") or {})
    values = pages.get("value") if isinstance(pages, dict) else None
    if not isinstance(values, list):
        # 兼容少数网关把列表直接放在 data
        values = (data.get("data") or {}).get("value") if isinstance(data, dict) else None
    hits: list[Hit] = []
    if isinstance(values, list):
        for item in values:
            if not isinstance(item, dict):
                continue
            hits.append(Hit(
                title=str(item.get("name") or item.get("title") or ""),
                url=str(item.get("url") or item.get("displayUrl") or ""),
                snippet=str(item.get("summary") or item.get("snippet") or item.get("description") or ""),
            ))
    return _clean_hits(hits, count)


async def _serper(http: httpx.AsyncClient, key: str, query: str, count: int) -> list[Hit]:
    r = await http.post(
        "https://google.serper.dev/search",
        headers={"X-API-KEY": key, "Content-Type": "application/json"},
        json={"q": query, "num": count},
        timeout=TIMEOUT,
    )
    if r.status_code >= 400:
        raise SearchError(f"Serper 返回错误（HTTP {r.status_code}）")
    data = r.json() if r.content else {}
    organic = data.get("organic") if isinstance(data, dict) else None
    hits: list[Hit] = []
    if isinstance(organic, list):
        for item in organic:
            if not isinstance(item, dict):
                continue
            hits.append(Hit(
                title=str(item.get("title") or ""),
                url=str(item.get("link") or ""),
                snippet=str(item.get("snippet") or ""),
            ))
    return _clean_hits(hits, count)


async def _tavily(http: httpx.AsyncClient, key: str, query: str, count: int) -> list[Hit]:
    r = await http.post(
        "https://api.tavily.com/search",
        headers={"Content-Type": "application/json"},
        json={"api_key": key, "query": query, "max_results": count, "include_answer": False},
        timeout=TIMEOUT,
    )
    if r.status_code >= 400:
        raise SearchError(f"Tavily 返回错误（HTTP {r.status_code}）")
    data = r.json() if r.content else {}
    results = data.get("results") if isinstance(data, dict) else None
    hits: list[Hit] = []
    if isinstance(results, list):
        for item in results:
            if not isinstance(item, dict):
                continue
            hits.append(Hit(
                title=str(item.get("title") or ""),
                url=str(item.get("url") or ""),
                snippet=str(item.get("content") or item.get("snippet") or ""),
            ))
    return _clean_hits(hits, count)


def inject_into_messages(messages: list[dict], hits: list[Hit]) -> list[dict]:
    """在首条 system 后插入检索上下文；无结果则原样返回。"""
    block = format_for_prompt(hits)
    if not block:
        return messages
    out = list(messages)
    insert_at = 1 if out and out[0].get("role") == "system" else 0
    out.insert(insert_at, {"role": "system", "content": block})
    return out
