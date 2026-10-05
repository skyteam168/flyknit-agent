from collections.abc import AsyncIterator

from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.orm import DeclarativeBase


class Base(DeclarativeBase):
    pass


_engine: AsyncEngine | None = None
_sessionmaker: async_sessionmaker[AsyncSession] | None = None


def init_engine(url: str) -> AsyncEngine:
    global _engine, _sessionmaker
    _engine = create_async_engine(url, future=True)
    _sessionmaker = async_sessionmaker(_engine, expire_on_commit=False)
    return _engine


async def create_all() -> None:
    assert _engine is not None
    from . import models  # noqa: F401  确保模型已注册

    async with _engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)


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
