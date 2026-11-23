"""数据库连接被网络掐断时：连接池先 ping 再用；万一还是撞上断掉的连接，回 503（员工端会自动重试）而不是 500。"""

import httpx
import pytest
from fastapi import APIRouter
from sqlalchemy.exc import DBAPIError, ProgrammingError

from app.db import POOL_RECYCLE_SECONDS, is_connection_lost, make_engine
from app.main import app


class ConnectionDoesNotExistError(Exception):
    """模拟 asyncpg 的同名异常（只看类名）。"""


def _lost_connection_error() -> DBAPIError:
    # 和日志里一样的链：OSError(WinError 121) → asyncpg 连接不存在 → SQLAlchemy DBAPIError
    try:
        try:
            try:
                raise OSError(121, "信号灯超时时间已到")
            except OSError as os_err:
                raise ConnectionDoesNotExistError("connection was closed in the middle of operation") from os_err
        except ConnectionDoesNotExistError as low:
            raise DBAPIError("SELECT 1", {}, low) from low
    except DBAPIError as e:
        return e


def test_postgres_pool_pings_and_recycles():
    engine = make_engine("postgresql+asyncpg://u:p@db.invalid/x", "flyknit")
    assert engine.pool._pre_ping is True
    assert engine.pool._recycle == POOL_RECYCLE_SECONDS
    sqlite = make_engine("sqlite+aiosqlite:///:memory:")
    assert sqlite.pool._pre_ping is False


def test_detects_lost_connection_only():
    assert is_connection_lost(_lost_connection_error())
    # SQL 写错：是数据库错误，但不是连接断了
    assert not is_connection_lost(ProgrammingError("SELECT nope", {}, Exception("syntax error")))
    # 不是数据库错误的 OSError（比如磁盘、权限）不算
    assert not is_connection_lost(OSError(121, "x"))
    assert not is_connection_lost(ValueError("x"))
    invalidated = DBAPIError("SELECT 1", {}, Exception("x"), connection_invalidated=True)
    assert is_connection_lost(invalidated)


_router = APIRouter()


@_router.get("/__test/db-lost")
async def _db_lost():
    raise _lost_connection_error()


@_router.get("/__test/db-bug")
async def _db_bug():
    raise ProgrammingError("SELECT nope", {}, Exception("syntax error"))


# 放在最前面：app 末尾挂了静态页面，追加在后面的路由会被它先接走
for _route in reversed(_router.routes):
    app.router.routes.insert(0, _route)


@pytest.mark.asyncio
async def test_lost_connection_returns_503_with_retry_after(client):
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as c:
        await _check(c)


async def _check(client):
    r = await client.get("/__test/db-lost")
    assert r.status_code == 503
    assert r.headers.get("retry-after") == "1"
    assert r.json()["error"]["type"] == "service_unavailable"
    # 真正的程序错误照旧 500
    r = await client.get("/__test/db-bug")
    assert r.status_code == 500
