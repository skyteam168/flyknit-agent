"""定期清理与备份。

两件事都做成后台任务而不是写进文档让人记得跑——留存期靠人记得清理，等于没有
留存期；备份靠人记得做，等于没有备份。

清理：聊天记录和审计日志过了保留期就删。存得越久越是负担，不是资产。
备份：每天一份快照。聊天内容进库之后，丢掉这个库的代价比以前高得多。
"""

from __future__ import annotations

import asyncio
import logging
import shutil
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path

from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..models import AdminAccess, AdminSession, AuditLog, ChatRecord

log = logging.getLogger("flyknit.housekeeping")

#: 各类记录保留多久
DEFAULT_RETENTION = {
    "chat_days": 90,
    "audit_days": 180,
    "access_days": 365,  # 谁看了谁的对话，留得比内容本身久
}

#: 保留几份备份
BACKUP_KEEP = 14

#: 两次例行维护之间隔多久
INTERVAL_HOURS = 24


def cutoff(days: int) -> datetime:
    return datetime.now(timezone.utc) - timedelta(days=max(1, days))


async def purge(session: AsyncSession, retention: dict | None = None) -> dict[str, int]:
    """删掉过期记录，返回每类删了多少。"""
    conf = {**DEFAULT_RETENTION, **(retention or {})}
    removed: dict[str, int] = {}

    for name, model, days in (
        ("chat", ChatRecord, conf["chat_days"]),
        ("audit", AuditLog, conf["audit_days"]),
        ("access", AdminAccess, conf["access_days"]),
    ):
        result = await session.execute(delete(model).where(model.created_at < cutoff(days)))
        removed[name] = result.rowcount or 0

    # 过期的登录会话没有保留价值，直接清掉
    expired = await session.execute(delete(AdminSession).where(AdminSession.expires_at < datetime.now(timezone.utc)))
    removed["sessions"] = expired.rowcount or 0

    await session.commit()
    if any(removed.values()):
        log.info("已清理过期记录：%s", removed)
    return removed


def database_file(url: str) -> Path | None:
    """
    从连接串里取出 SQLite 文件路径。不是 SQLite（比如 Postgres）就返回 None。

    两种写法都要认：相对路径 sqlite+aiosqlite:///./flyknit.db，
    绝对路径 sqlite+aiosqlite:////var/lib/flyknit.db（四条斜杠）。
    """
    if not url.startswith("sqlite") or "///" not in url:
        return None
    raw = url.split("///", 1)[1].split("?", 1)[0]
    if not raw or raw == ":memory:":
        return None
    return Path(raw).resolve()


def backup(url: str, directory: Path, keep: int = BACKUP_KEEP) -> Path | None:
    """
    给 SQLite 库做一份一致的快照。

    不能直接拷文件——WAL 模式下拷到的可能是半截事务。用 sqlite3 自带的 backup，
    它会在拷贝期间处理好并发写。Postgres 不走这里，交给 pg_dump 之类的外部工具。
    """
    source = database_file(url)
    if source is None:
        log.debug("非 SQLite，跳过内置备份")
        return None
    if not source.exists():
        log.warning("数据库文件不存在，跳过备份：%s", source)
        return None

    directory.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    target = directory / f"flyknit-{stamp}.db"

    src = dst = None
    try:
        src = sqlite3.connect(f"file:{source}?mode=ro", uri=True)
        dst = sqlite3.connect(target)
        src.backup(dst)
    except sqlite3.Error as exc:
        log.error("备份失败：%s", exc)
        target.unlink(missing_ok=True)
        return None
    finally:
        for conn in (dst, src):
            if conn is not None:
                conn.close()

    prune(directory, keep)
    log.info("已备份到 %s（%.1f MB）", target, target.stat().st_size / 1024 / 1024)
    return target


def prune(directory: Path, keep: int) -> int:
    """只留最近的几份，否则磁盘迟早被备份塞满。"""
    files = sorted(directory.glob("flyknit-*.db"), key=lambda p: p.name, reverse=True)
    removed = 0
    for old in files[max(1, keep):]:
        try:
            old.unlink()
            removed += 1
        except OSError as exc:
            log.warning("删除旧备份失败 %s：%s", old, exc)
    return removed


async def run_forever(sessionmaker, url: str, backup_dir: Path, retention: dict | None = None) -> None:
    """每天跑一次清理和备份。单次失败不能让任务死掉，否则后面就再也不跑了。"""
    while True:
        try:
            async with sessionmaker() as session:
                await purge(session, retention)
            await asyncio.to_thread(backup, url, backup_dir)
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001
            log.warning("例行维护出错，下一轮再试", exc_info=True)
        await asyncio.sleep(INTERVAL_HOURS * 3600)
