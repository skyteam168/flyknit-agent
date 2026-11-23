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


# 连接池里闲着的连接最多用这么久就换新的（秒）。数据库在别的机器上时，中间的防火墙 / 网络设备会悄悄掐断长时间不说话的连接
POOL_RECYCLE_SECONDS = 1800


def make_engine(url: str, schema: str = "") -> AsyncEngine:
    """
    PostgreSQL 指定了 schema 时，每个连接的 search_path 都设成它：表建在里面，查询也只看它。
    网络数据库的连接池：取连接前先 ping 一下（断了就自动换一条），闲置的连接定期换新；
    否则网络把空闲连接掐断后，下一个请求会拿到死连接直接 500（Windows 上是「信号灯超时时间已到」）。
    """
    schema = check_schema(schema)
    if not url.startswith("postgresql"):
        return create_async_engine(url, future=True)
    kwargs: dict[str, Any] = {"pool_pre_ping": True, "pool_recycle": POOL_RECYCLE_SECONDS}
    if schema:
        kwargs["connect_args"] = {"server_settings": {"search_path": schema}}
    return create_async_engine(url, future=True, **kwargs)


def is_connection_lost(exc: BaseException) -> bool:
    """
    数据库连接中途断了（不是 SQL 写错、也不是磁盘 / 权限问题）：重试一次通常就好。
    条件：异常链里有数据库错误，并且是连接层面的（被标成连接失效，或者底下是连接断开 / 网络超时）。
    """
    from sqlalchemy.exc import DBAPIError

    chain: list[BaseException] = []
    cur: BaseException | None = exc
    while cur is not None and len(chain) < 20:
        chain.append(cur)
        cur = cur.__cause__ or cur.__context__
    db_errors = [e for e in chain if isinstance(e, DBAPIError)]
    if not db_errors:
        return False
    if any(e.connection_invalidated for e in db_errors):
        return True
    return any(
        isinstance(e, (ConnectionError, TimeoutError))
        or type(e).__name__ in ("ConnectionDoesNotExistError", "ConnectionFailureError")
        or (isinstance(e, OSError) and getattr(e, "winerror", None) in (64, 121, 10053, 10054, 10060))
        for e in chain
    )


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
