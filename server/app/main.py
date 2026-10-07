import asyncio
import io
import logging
from contextlib import asynccontextmanager, suppress
from pathlib import Path
from typing import NamedTuple

import httpx
from dotenv.parser import parse_stream
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from . import __version__, db
from .config import Settings, get_settings
from .routers import admin, agents, chats, client, console, gateway, instructions, releases, speech
from .services import housekeeping

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")


class BadEnvLine(NamedTuple):
    number: int
    hint: str


def unparsable_env_lines(env_file: Path) -> list[BadEnvLine]:
    """
    .env 里读不懂的行。

    python-dotenv 碰到这种行只会 warning 一句行号就跳过，那一项于是悄悄退回默认值——
    改过的密钥其实没生效，而日志里看不出来。这种「以为改了其实没改」的状态比没改更危险，
    所以这里按 ERROR 单独列出来，并带上那一行长什么样（不带值，值可能是密钥）。
    """
    if not env_file.exists():
        return []
    try:
        text = env_file.read_text(encoding="utf-8-sig")
    except OSError:
        return []

    bad = []
    lines = text.splitlines()
    for binding in parse_stream(io.StringIO(text)):
        number = binding.original.line
        raw = (lines[number - 1] if 0 < number <= len(lines) else "").strip()
        if binding.error:
            why = "这一行读不懂"
        elif binding.value is None and raw and not raw.startswith("#"):
            # 没有等号的行 dotenv 不报错，当成一个空设置吃掉——多半是忘了写 # 的注释
            why = "这一行没有等号，被当成空设置"
        else:
            continue
        # 只回显等号前面那段。等号后面可能就是密钥，不往日志里写
        head = raw.split("=", 1)[0].strip()
        bad.append(BadEnvLine(number, f"{why}：{head[:40] or raw[:40]}"))
    return bad


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    log = logging.getLogger("flyknit")
    env_file = Path(__file__).resolve().parents[1] / ".env"
    log.info(".env 路径：%s（%s）", env_file, "已找到" if env_file.exists() else "不存在，使用默认配置")
    log.info("管理员令牌：%s****（共 %d 个字符）", settings.admin_token[:3], len(settings.admin_token))
    for line in unparsable_env_lines(env_file):
        log.error(".env 第 %d 行没有生效，这一项用的是默认值 —— %s", line.number, line.hint)
    # 只点名真正还是默认值的那几项。原来不管哪个没改都念一遍「ADMIN_TOKEN / SECRET_KEY」，
    # 改了一个的人会以为自己没改成功，反过来也会以为改全了
    for name in ("admin_token", "secret_key", "enrollment_key"):
        if getattr(settings, name) == Settings.model_fields[name].default:
            log.warning("仍在使用默认的 FLYKNIT_%s，请在 .env 中修改%s", name.upper(),
                        "（拿到它就能冒充任意机器注册）" if name == "enrollment_key" else "")
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
app.include_router(releases.router)


@app.get("/healthz", tags=["system"])
async def healthz():
    return {"status": "ok", "version": __version__}


# 管理后台网页（admin/ 目录 npm run build 的产物）。没构建过就不挂，接口照常可用
_admin_dist = Path(__file__).resolve().parent / "static" / "admin"
if _admin_dist.is_dir():
    app.mount("/admin", StaticFiles(directory=_admin_dist, html=True), name="admin")
