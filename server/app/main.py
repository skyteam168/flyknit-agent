import logging
from contextlib import asynccontextmanager

import httpx
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from . import __version__, db
from .config import get_settings
from .routers import admin, client, gateway

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    if settings.admin_token.startswith("change-me") or settings.secret_key.startswith("change-me"):
        logging.getLogger("flyknit").warning("仍在使用默认的 ADMIN_TOKEN / SECRET_KEY，请在 .env 中修改")
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


@app.get("/healthz", tags=["system"])
async def healthz():
    return {"status": "ok", "version": __version__}
