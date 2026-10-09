"""用户协议与隐私政策。

默认文本在 app/legal/*.md；管理员在后台改过就存进 settings 表，以改过的为准，也能恢复默认。
登录界面（还没登录、没有设备令牌）要能看到，所以读取接口不需要认证。

version 用最后修改时间：员工登录时同意的是哪一版，记在设备上，以后改了协议能查到谁同意的是旧版。
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path

from sqlalchemy.ext.asyncio import AsyncSession

from . import settings_store

KINDS = {
    "terms": "用户协议",
    "privacy": "隐私政策",
}

_DEFAULT_DIR = Path(__file__).resolve().parent.parent / "legal"
#: 默认文本的版本号。改了默认文本（app/legal/*.md）就改这个日期
DEFAULT_VERSION = "default-2026-10-09"
MAX_CHARS = 100_000


def _key(kind: str) -> str:
    return f"legal_{kind}"


def default_content(kind: str) -> str:
    return (_DEFAULT_DIR / f"{kind}.md").read_text(encoding="utf-8")


async def get(session: AsyncSession, kind: str) -> dict:
    stored = await settings_store.get_value(session, _key(kind), {})
    content = stored.get("content") if isinstance(stored.get("content"), str) else None
    return {
        "kind": kind,
        "title": KINDS[kind],
        "content": content if content else default_content(kind),
        "version": stored.get("version") or DEFAULT_VERSION,
        "updated_at": stored.get("updated_at"),
        "updated_by": stored.get("updated_by", ""),
        "customized": bool(content),
    }


async def save(session: AsyncSession, kind: str, content: str, who: str) -> dict:
    now = datetime.now(timezone.utc)
    await settings_store.set_value(session, _key(kind), {
        "content": content.strip()[:MAX_CHARS],
        "version": now.strftime("%Y%m%d%H%M%S"),
        "updated_at": now.isoformat(),
        "updated_by": who,
    })
    return await get(session, kind)


async def reset(session: AsyncSession, kind: str) -> dict:
    await settings_store.set_value(session, _key(kind), {})
    return await get(session, kind)


async def versions(session: AsyncSession) -> str:
    """当前两份文件的版本，写成 terms@v;privacy@v，登录时客户端原样带回来表示同意了这一版。"""
    parts = []
    for kind in KINDS:
        doc = await get(session, kind)
        parts.append(f"{kind}@{doc['version']}")
    return ";".join(parts)
