"""
产品页（落地页）的下载：员工在浏览器里打开 http://服务器:8000/，点「下载」就拿到最新的员工端。

默认关闭，由超级管理员在后台「员工端版本」里打开。打开时生成一张专用的安装凭证写进下载的包里，
员工装完直接点「登录」。凭证和后台手动下载的一样能在「安装凭证」里看到注册了几台、随时吊销；
吊销或换服务器地址后自动换一张新的。

做好的安装包按（版本, 凭证）缓存在磁盘上：一百多 MB 的包不能每来一个人就重新拼一次。
"""

import logging
import os
import re
import zipfile
from pathlib import Path
from typing import Literal

from fastapi import APIRouter, Depends, HTTPException, Request, status
from pydantic import BaseModel, Field
from sqlalchemy.ext.asyncio import AsyncSession

from ..crypto import decrypt, encrypt, hash_token, new_token
from ..db import get_session
from ..deps import require_admin, require_owner
from ..models import AdminUser, ClientRelease, EnrollmentTicket
from ..services import settings_store
from . import releases

log = logging.getLogger("flyknit.landing")
router = APIRouter(prefix="/api/v1", tags=["landing"])

KEY = "landing_download"
LABEL = "产品页下载"
DEFAULT = {"enabled": False, "server_url": "", "ticket_id": None, "token": ""}


class LandingIn(BaseModel):
    enabled: bool
    #: 员工电脑访问服务端用的地址，写进下载的安装包
    server_url: str = Field(default="", max_length=500)


class LandingOut(BaseModel):
    enabled: bool
    server_url: str = ""
    ticket_id: int | None = None
    ticket_uses: int = 0
    version: str = ""


class PublicRelease(BaseModel):
    download_enabled: bool
    version: str = ""
    notes: str = ""
    size: int = 0
    published_at: str = ""
    formats: list[str] = []


def _cache_dir() -> Path:
    d = releases._release_dir() / "landing"
    d.mkdir(parents=True, exist_ok=True)
    return d


async def _valid_ticket(session: AsyncSession, conf: dict) -> EnrollmentTicket | None:
    if not conf.get("ticket_id"):
        return None
    ticket = await session.get(EnrollmentTicket, conf["ticket_id"])
    return None if ticket is None or ticket.revoked else ticket


async def _out(session: AsyncSession, conf: dict) -> LandingOut:
    ticket = await _valid_ticket(session, conf)
    latest = await releases._latest(session)
    return LandingOut(
        enabled=bool(conf.get("enabled")),
        server_url=conf.get("server_url", ""),
        ticket_id=ticket.id if ticket else None,
        ticket_uses=ticket.uses if ticket else 0,
        version=latest.version if latest else "",
    )


async def _new_ticket(session: AsyncSession, conf: dict, who: str) -> dict:
    """换一张新的专用凭证（旧的吊销），已经缓存的安装包跟着作废。"""
    old = await _valid_ticket(session, conf)
    if old is not None:
        old.revoked = True
    token = new_token()
    ticket = EnrollmentTicket(token_hash=hash_token(token), label=LABEL, server_url=conf["server_url"], created_by=who)
    session.add(ticket)
    await session.flush()
    for f in _cache_dir().iterdir():
        f.unlink(missing_ok=True)
    return {**conf, "ticket_id": ticket.id, "token": encrypt(token)}


# ---------- 管理端 ----------

@router.get("/admin/landing", response_model=LandingOut, dependencies=[Depends(require_admin)])
async def get_landing(session: AsyncSession = Depends(get_session)):
    return await _out(session, await settings_store.get_value(session, KEY, DEFAULT))


@router.put("/admin/landing", response_model=LandingOut)
async def set_landing(
    data: LandingIn,
    owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    """
    打开或关闭产品页下载。限超级管理员：打开后能打开产品页的人都能装上员工端并注册电脑。
    """
    conf = await settings_store.get_value(session, KEY, DEFAULT)
    who = owner.username if owner else "admin_token"
    server_url = data.server_url.strip().rstrip("/") or conf.get("server_url", "")
    if data.enabled and not re.match(r"^https?://[^\s/]+", server_url):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "服务器地址要写成 http://10.0.0.5:8000 这样的完整地址")
    changed_url = server_url != conf.get("server_url", "")
    conf = {**conf, "enabled": data.enabled, "server_url": server_url}
    if data.enabled and (changed_url or await _valid_ticket(session, conf) is None):
        conf = await _new_ticket(session, conf, who)
    await settings_store.set_value(session, KEY, conf)
    log.warning("%s %s了产品页下载（服务器 %s）", who, "打开" if data.enabled else "关闭", server_url or "-")
    return await _out(session, conf)


@router.post("/admin/landing/rotate", response_model=LandingOut)
async def rotate_landing_ticket(
    owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    """安装包外泄了：吊销旧凭证、换一张新的。已经注册的电脑不受影响。"""
    conf = await settings_store.get_value(session, KEY, DEFAULT)
    if not conf.get("server_url"):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "还没有打开过产品页下载")
    conf = await _new_ticket(session, conf, owner.username if owner else "admin_token")
    await settings_store.set_value(session, KEY, conf)
    return await _out(session, conf)


# ---------- 公开（产品页用，不需要登录） ----------

async def _ready(session: AsyncSession) -> tuple[dict, ClientRelease, EnrollmentTicket] | None:
    conf = await settings_store.get_value(session, KEY, DEFAULT)
    if not conf.get("enabled"):
        return None
    ticket = await _valid_ticket(session, conf)
    latest = await releases._latest(session)
    if ticket is None or latest is None or not releases._release_path(latest).exists():
        return None
    return conf, latest, ticket


_formats_cache: dict[str, list[str]] = {}


def _formats(latest: ClientRelease) -> list[str]:
    """这个版本能下哪几种：包里有安装程序外壳才有 exe。产品页每次打开都问，按版本缓存，不每次翻 zip。"""
    if latest.sha256 not in _formats_cache:
        try:
            with zipfile.ZipFile(releases._release_path(latest)) as zf:
                names = {n.replace("\\", "/").rsplit("/", 1)[-1].lower() for n in zf.namelist()}
        except (zipfile.BadZipFile, OSError):
            return []
        _formats_cache[latest.sha256] = (["exe"] if releases.SETUP_EXE.lower() in names else []) + ["zip"]
    return _formats_cache[latest.sha256]


@router.get("/public/release", response_model=PublicRelease)
async def public_release(session: AsyncSession = Depends(get_session)):
    """产品页显示的版本信息。没开放下载时只告诉页面「暂未开放」，不透露别的。"""
    ready = await _ready(session)
    if ready is None:
        return PublicRelease(download_enabled=False)
    _, latest, _ = ready
    return PublicRelease(
        download_enabled=True,
        version=latest.version,
        notes=latest.notes,
        size=latest.size,
        published_at=latest.created_at.isoformat() if latest.created_at else "",
        formats=_formats(latest),
    )


@router.get("/public/download")
async def public_download(
    request: Request,
    format: Literal["exe", "zip"] = "exe",
    session: AsyncSession = Depends(get_session),
):
    """下载最新的员工端（带产品页专用凭证）。exe 是安装程序，zip 是免安装版。"""
    ready = await _ready(session)
    if ready is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "下载暂未开放，请联系 IT 信息科技部")
    conf, latest, ticket = ready
    cached = _cache_dir() / f"{latest.sha256[:16]}-{ticket.id}.{format}"
    if not cached.exists():
        folder, stub = releases._package_stub(latest, format)
        token = decrypt(conf["token"])
        # 先拼到临时文件再改名：同时有几个人在下，谁也拿不到半截的包
        tmp = cached.with_name(f".{new_token()[:12]}-{cached.name}")
        try:
            await releases._build_package(latest, folder, stub, releases._provision(conf["server_url"], token, LABEL, latest.version), tmp)
            os.replace(tmp, cached)
        finally:
            tmp.unlink(missing_ok=True)
        # 旧版本、旧凭证的缓存清掉
        for f in _cache_dir().iterdir():
            if f.name != cached.name and not f.name.startswith("."):
                f.unlink(missing_ok=True)
    log.info("产品页下载 %s %s（%s）", latest.version, format, request.client.host if request.client else "-")
    return releases._package_response(cached, latest.version, format, delete_after=False)
