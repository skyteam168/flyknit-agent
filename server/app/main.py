import asyncio
import logging
from contextlib import asynccontextmanager, suppress
from pathlib import Path

import httpx
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from . import __version__, db
from .config import get_settings
from .routers import admin, agents, chats, client, console, gateway, instructions, speech
from .services import housekeeping

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

    # 清理过期记录和备份数据库。做成后台任务而不是写进文档让人记得跑——
    # 留存期靠人记得清理等于没有留存期，备份靠人记得做等于没有备份。
    chore = None
    if settings.housekeeping:
        chore = asyncio.create_task(
            housekeeping.run_forever(
                db.get_sessionmaker(),
                settings.database_url,
                Path(settings.data_dir) / "backups",
            )
        )

    yield

    if chore is not None:
        chore.cancel()
        with suppress(asyncio.CancelledError):
            await chore
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
app.include_router(chats.router)
app.include_router(console.router)
app.include_router(agents.router)
app.include_router(instructions.router)


@app.get("/healthz", tags=["system"])
async def healthz():
    return {"status": "ok", "version": __version__}


# 管理后台网页（admin/ 目录 npm run build 的产物）。没构建过就不挂，接口照常可用
_admin_dist = Path(__file__).resolve().parent / "static" / "admin"
if _admin_dist.is_dir():
    app.mount("/admin", StaticFiles(directory=_admin_dist, html=True), name="admin")
