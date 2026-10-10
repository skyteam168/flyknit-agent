import os

os.environ["FLYKNIT_ADMIN_TOKEN"] = "test-admin"
os.environ["FLYKNIT_SECRET_KEY"] = "test-secret"
os.environ["FLYKNIT_ENROLLMENT_KEY"] = "test-enroll"
os.environ["FLYKNIT_DATABASE_URL"] = "sqlite+aiosqlite:///:memory:"
os.environ["FLYKNIT_HOUSEKEEPING"] = "false"

import httpx  # noqa: E402
import pytest  # noqa: E402
import pytest_asyncio  # noqa: E402

from app.config import get_settings  # noqa: E402

get_settings.cache_clear()

from app.main import app  # noqa: E402

ADMIN = {"Authorization": "Bearer test-admin"}


#: 设了这个就在 PostgreSQL 上跑整套测试（每个用例前清空 public schema），例如
#: FLYKNIT_TEST_PG_URL=postgresql+asyncpg://postgres@127.0.0.1:55432/flyknit_test
PG_URL = os.environ.get("FLYKNIT_TEST_PG_URL", "")


async def _reset_pg(url: str) -> None:
    from sqlalchemy import text
    from sqlalchemy.ext.asyncio import create_async_engine

    engine = create_async_engine(url)
    async with engine.begin() as conn:
        await conn.execute(text("DROP SCHEMA public CASCADE"))
        await conn.execute(text("CREATE SCHEMA public"))
    await engine.dispose()


@pytest_asyncio.fixture
async def client(tmp_path):
    if PG_URL:
        await _reset_pg(PG_URL)
        get_settings().database_url = PG_URL
    else:
        get_settings().database_url = f"sqlite+aiosqlite:///{tmp_path / 'test.db'}"
    get_settings().data_dir = str(tmp_path / "data")
    async with app.router.lifespan_context(app):
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url="http://test") as c:
            yield c


@pytest.fixture
def admin_headers():
    return dict(ADMIN)


@pytest_asyncio.fixture
async def chat_reader_headers(client):
    """一个能看聊天内容的具名账号。看内容必须有名字——共享令牌答不出「是谁看的」。"""
    await client.post("/api/v1/admin/users", headers=ADMIN,
                      json={"username": "it.reader", "password": "init-pass-123",
                            "display_name": "读者", "can_read_chats": True})
    first = await client.post("/api/v1/admin/login",
                              json={"username": "it.reader", "password": "init-pass-123"})
    headers = {"Authorization": f"Bearer {first.json()['token']}"}
    # 初始密码改掉之前，这个账号除了改密码什么也做不了
    await client.post("/api/v1/admin/password", headers=headers,
                      json={"old_password": "init-pass-123", "new_password": "changed-pass-456"})
    r = await client.post("/api/v1/admin/login",
                          json={"username": "it.reader", "password": "changed-pass-456"})
    return {"Authorization": f"Bearer {r.json()['token']}"}


@pytest_asyncio.fixture
async def device_headers(client):
    r = await client.post(
        "/api/v1/devices/register",
        json={"enrollment_key": "test-enroll", "machine_name": "PC-001", "user_name": "nguyen"},
    )
    assert r.status_code == 200, r.text
    return {"Authorization": f"Bearer {r.json()['token']}"}


async def setup_models(client, primary_url="http://primary.local/v1", fallback_url="http://backup.local/v1"):
    p1 = (await client.post("/api/v1/admin/providers", headers=ADMIN,
                            json={"name": "内部", "base_url": primary_url, "api_key": "sk-internal-123456"})).json()
    p2 = (await client.post("/api/v1/admin/providers", headers=ADMIN,
                            json={"name": "百炼", "base_url": fallback_url, "api_key": "sk-cloud-abcdef"})).json()
    m1 = (await client.post("/api/v1/admin/models", headers=ADMIN,
                            json={"provider_id": p1["id"], "name": "Qwen3.5-397B", "model": "qwen3.5-397b"})).json()
    m2 = (await client.post("/api/v1/admin/models", headers=ADMIN,
                            json={"provider_id": p2["id"], "name": "Qwen-Plus", "model": "qwen-plus"})).json()
    for scene in ("chat", "agent"):
        r = await client.put(f"/api/v1/admin/routes/{scene}", headers=ADMIN,
                             json={"model_id": m1["id"], "fallback_model_id": m2["id"]})
        assert r.status_code == 200, r.text
    return m1, m2
