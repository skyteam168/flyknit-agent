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


@pytest_asyncio.fixture
async def client(tmp_path):
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
