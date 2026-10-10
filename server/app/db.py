import re
from collections.abc import AsyncIterator
from typing import Any

from sqlalchemy import event, inspect, text
from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.orm import DeclarativeBase, Session


class Base(DeclarativeBase):
    pass


def strip_nul(value: Any) -> Any:
    """去掉字符串里的 \x00（含 JSON 里嵌套的）。没有的话原样返回同一个对象。"""
    if isinstance(value, str):
        return value.replace("\x00", "") if "\x00" in value else value
    if isinstance(value, dict):
        return {k: strip_nul(v) for k, v in value.items()}
    if isinstance(value, list):
        return [strip_nul(v) for v in value]
    return value


@event.listens_for(Session, "before_flush")
def _no_nul_characters(session: Session, _context, _instances) -> None:
    """
    PostgreSQL 的文本字段存不了 \x00（SQLite 可以）。员工端上报的命令输出、读到的二进制文件片段里偶尔会带，
    一条带了整批审计就写不进去、员工端还会一直重试。写库前统一去掉，两种库行为一致。
    """
    for obj in (*session.new, *session.dirty):
        state = inspect(obj)
        for attr in state.mapper.column_attrs:
            value = getattr(obj, attr.key, None)
            if isinstance(value, (str, dict, list)):
                cleaned = strip_nul(value)
                if cleaned is not value and cleaned != value:
                    setattr(obj, attr.key, cleaned)


_engine: AsyncEngine | None = None
_sessionmaker: async_sessionmaker[AsyncSession] | None = None


_schema = ""

_SCHEMA_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{0,62}$")


def check_schema(schema: str) -> str:
    schema = (schema or "").strip()
    if schema and not _SCHEMA_NAME.match(schema):
        raise ValueError(f"POSTGRES_SCHEMA 只能用字母、数字和下划线，且不能以数字开头：{schema!r}")
    return schema


def make_engine(url: str, schema: str = "") -> AsyncEngine:
    """PostgreSQL 指定了 schema 时，每个连接的 search_path 都设成它：表建在里面，查询也只看它。"""
    schema = check_schema(schema)
    if schema and url.startswith("postgresql"):
        return create_async_engine(url, future=True, connect_args={"server_settings": {"search_path": schema}})
    return create_async_engine(url, future=True)


async def ensure_pg_schema(engine: AsyncEngine, schema: str) -> None:
    if schema and engine.dialect.name == "postgresql":
        async with engine.begin() as conn:
            await conn.execute(text(f'CREATE SCHEMA IF NOT EXISTS "{check_schema(schema)}"'))


def init_engine(url: str, schema: str = "") -> AsyncEngine:
    global _engine, _sessionmaker, _schema
    _schema = check_schema(schema) if url.startswith("postgresql") else ""
    _engine = make_engine(url, _schema)
    _sessionmaker = async_sessionmaker(_engine, expire_on_commit=False)
    return _engine


async def create_all() -> None:
    """建表并补齐缺失的字段与索引（老版本升级上来时会用到）。"""
    assert _engine is not None
    from . import models  # noqa: F401  确保模型已注册
    from .migrate import ensure_schema

    await ensure_pg_schema(_engine, _schema)
    async with _engine.begin() as conn:
        changes = await conn.run_sync(ensure_schema)
    if changes:
        import logging

        logging.getLogger("flyknit").info("表结构已升级：%s", "、".join(changes))


async def dispose() -> None:
    if _engine is not None:
        await _engine.dispose()


def get_sessionmaker() -> async_sessionmaker[AsyncSession]:
    """后台任务里需要自己开会话（请求的会话在响应结束后就关了）。"""
    assert _sessionmaker is not None, "数据库未初始化"
    return _sessionmaker


async def get_session() -> AsyncIterator[AsyncSession]:
    assert _sessionmaker is not None, "数据库未初始化"
    async with _sessionmaker() as session:
        yield session
