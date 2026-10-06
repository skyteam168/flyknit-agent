import logging
from contextlib import asynccontextmanager
from pathlib import Path

import httpx
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from . import __version__, db
from .config import get_settings
from .routers import admin, client, gateway, speech

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    log = logging.getLogger("flyknit")
    env_file = Path(__file__).resolve().parents[1] / ".env"
    log.info(".env 路径：%s（%s）", env_file, "已找到" if env_file.exists() else "不存在，使用默认配置")
    log.info("管理员令牌：%s****（共 %d 个字符）", settings.admin_token[:3], len(settings.admin_token))
    if settings.admin_token.startswith("change-me") or settings.secret_key.startswith("change-me"):
        log.warning("仍在使用默认的 ADMIN_TOKEN / SECRET_KEY，请在 .env 中修改")
    db.init_engine(settings.database_url)
    await db.create_all()
    app.state.http = httpx.AsyncClient(timeout=httpx.Timeout(settings.upstream_timeout, connect=10))
    yield
    await app.state.http.aclose()
    await db.dispose()


app = FastAPI(title="Flyknit Server", version=__version__, lifespan=lifespan)
app.add_middleware(
    CORSMiddleware,
    allow_origins=get_settings().cors_origins,
    allow_methods=["*"],
    allow_headers=["*"],
)
app.include_router(gateway.router)
app.include_router(client.router)
app.include_router(admin.router)
app.include_router(speech.router)


@app.get("/healthz", tags=["system"])
async def healthz():
    return {"status": "ok", "version": __version__}
