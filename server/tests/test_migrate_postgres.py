"""SQLite → PostgreSQL 整库迁移：搬完后台照常登录、员工端令牌照常能用、用量和配置都在。

要一个真的 PostgreSQL 才跑（FLYKNIT_TEST_PG_URL），没有就跳过。
"""

import argparse
import sqlite3
from contextlib import asynccontextmanager

import httpx
import pytest
from sqlalchemy import text
from sqlalchemy.ext.asyncio import create_async_engine

from app import crypto, db
from app.config import get_settings
from app.main import app
from app.services import usage_store
from conftest import ADMIN, PG_URL, _reset_pg
from scripts import migrate_to_postgres

needs_pg = pytest.mark.skipif(not PG_URL, reason="没有配置 FLYKNIT_TEST_PG_URL")


@asynccontextmanager
async def running(url):
    get_settings().database_url = url
    async with app.router.lifespan_context(app):
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url="http://test") as c:
            yield c


def migrate(source, **flags):
    args = argparse.Namespace(source=str(source), target=PG_URL, dry_run=False, replace=False)
    for k, v in flags.items():
        setattr(args, k, v)
    return migrate_to_postgres.run(args)


async def seed(sqlite_file):
    """用真接口在 SQLite 里造一套数据：管理员、模型 Key、设备、用量、审计。"""
    async with running(f"sqlite+aiosqlite:///{sqlite_file}") as c:
        r = await c.post("/api/v1/admin/users", headers=ADMIN,
                         json={"username": "it.zhang", "password": "init-pass-123", "display_name": "张工"})
        assert r.status_code == 201, r.text
        token = (await c.post("/api/v1/admin/login", json={"username": "it.zhang", "password": "init-pass-123"})).json()["token"]
        await c.post("/api/v1/admin/password", headers={"Authorization": f"Bearer {token}"},
                     json={"old_password": "init-pass-123", "new_password": "changed-pass-456"})
        r = await c.post("/api/v1/admin/providers", headers=ADMIN,
                         json={"name": "百炼", "base_url": "http://10.0.0.1/v1", "api_key": "sk-1234567890"})
        assert r.status_code == 201
        r = await c.post("/api/v1/devices/register", json={
            "enrollment_key": "test-enroll", "machine_name": "PC-QC-017", "user_name": "SZ\\nguyen",
            "machine_guid": "5f1c0d2e-aaaa-bbbb-cccc-0123456789ab"})
        device = r.json()
        dev_headers = {"Authorization": f"Bearer {device['token']}"}
        await c.post("/api/v1/devices/heartbeat", headers=dev_headers, json={"user_name": "nguyen", "domain": "SZ"})
        r = await c.post("/api/v1/audit", headers=dev_headers, json={"items": [{
            "tool_name": "read_file", "arguments": "{}", "risk": "auto", "decision": "auto",
            "status": "ok", "summary": "读了报表", "occurred_at": "2026-10-10T08:00:00+08:00"}]})
        assert r.status_code == 204, r.text
        async with db.get_sessionmaker()() as s:
            await usage_store.record(s, device["device_id"], "agent", 500_000, 3_000)
    # SQLite 允许、PostgreSQL 不允许的残留：指向已删除设备的记录、超长字段
    con = sqlite3.connect(sqlite_file)
    con.execute("INSERT INTO usage_daily (device_id, day, scene, prompt_tokens, completion_tokens, requests, updated_at)"
                " VALUES (999, '2026-10-01', 'chat', 10, 0, 1, '2026-10-01 00:00:00')")
    con.execute("UPDATE audit_logs SET device_id = 999 WHERE id = (SELECT MIN(id) FROM audit_logs)")
    con.execute("UPDATE devices SET os_version = ? WHERE id = ?", ("x" * 300, device["device_id"]))
    # 命令输出里的空字符：SQLite 存得下，PostgreSQL 存不了
    con.execute("UPDATE audit_logs SET summary = ?", ("退出码：0\r\nMZ\x00\x00\x03",))
    con.commit()
    con.close()
    return device, dev_headers


@pytest.mark.asyncio
@needs_pg
async def test_whole_database_moves_to_postgres(tmp_path):
    sqlite_file = tmp_path / "flyknit.db"
    device, dev_headers = await seed(sqlite_file)
    await _reset_pg(PG_URL)

    assert await migrate(sqlite_file, dry_run=True) == 0
    assert await migrate(sqlite_file) == 0

    async with running(PG_URL) as c:
        # 管理员用改过的密码照常登录
        r = await c.post("/api/v1/admin/login", json={"username": "it.zhang", "password": "changed-pass-456"})
        assert r.status_code == 200, r.text
        # 员工端的旧令牌照常能用，不用重新登录
        assert (await c.post("/api/v1/devices/heartbeat", headers=dev_headers, json={})).status_code == 204
        devices = (await c.get("/api/v1/admin/devices", headers=ADMIN)).json()
        assert [d["id"] for d in devices] == [device["device_id"]]
        assert len(devices[0]["os_version"]) == 200
        usage = (await c.get("/api/v1/admin/usage?days=30", headers=ADMIN)).json()
        mine = next(u for u in usage if u["device_id"] == device["device_id"])
        assert mine["tokens"] == 503_000
        # 指向已删除设备的用量行没搬（原来在后台也看不到），审计记录保留但不再挂设备
        assert [u["device_id"] for u in usage] == [device["device_id"]]
        audit = (await c.get("/api/v1/admin/audit", headers=ADMIN)).json()
        assert len(audit) == 1
        async with db.get_sessionmaker()() as s:
            summary = (await s.execute(text("SELECT summary FROM audit_logs"))).scalar_one()
            assert summary == "退出码：0\r\nMZ\x03"
        # 模型 Key 还解得开（FLYKNIT_SECRET_KEY 没变）
        async with db.get_sessionmaker()() as s:
            provider = (await s.execute(text("SELECT api_key_enc FROM providers"))).scalar_one()
            assert crypto.decrypt(provider) == "sk-1234567890"
        # 新记录的编号接在旧编号后面，不会撞
        r = await c.post("/api/v1/devices/register", json={
            "enrollment_key": "test-enroll", "machine_name": "PC-NEW", "user_name": "tran"})
        assert r.status_code == 200, r.text
        assert r.json()["device_id"] > device["device_id"]

    # 新库有数据了：不加 --replace 不动它；加了就覆盖成旧库的样子
    assert await migrate(sqlite_file) == 3
    assert await migrate(sqlite_file, replace=True) == 0
    engine = create_async_engine(PG_URL)
    async with engine.connect() as conn:
        assert (await conn.execute(text("SELECT COUNT(*) FROM devices"))).scalar_one() == 1
    await engine.dispose()


@pytest.mark.asyncio
async def test_refuses_a_sqlite_target(tmp_path):
    sqlite_file = tmp_path / "flyknit.db"
    sqlite3.connect(sqlite_file).close()
    args = argparse.Namespace(source=str(sqlite_file), target="sqlite+aiosqlite:///x.db", dry_run=False, replace=False)
    assert await migrate_to_postgres.run(args) == 2


def test_postgres_settings_build_the_url(monkeypatch):
    from app.config import Settings

    monkeypatch.setenv("POSTGRES_HOST", "10.0.0.5")
    monkeypatch.setenv("POSTGRES_PASSWORD", "p@ss:w/rd")
    monkeypatch.setenv("POSTGRES_DB", "flyknit")
    s = Settings(_env_file=None)
    assert s.database_url == "postgresql+asyncpg://postgres:p%40ss%3Aw%2Frd@10.0.0.5:5432/flyknit"
    assert s.sqlite_url.startswith("sqlite")
