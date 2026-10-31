"""
手机 App 的版本发布与升级。

  后台：上传 APK（填版本名和 Android 版本号）→ 发布；限超级管理员
  手机：打开 App 时问有没有新版本，有就下载、调系统安装
  电脑：「设置 → 手机」里给还没装 App 的手机一个下载二维码

下载链接带每个版本自己的随机串，不需要登录（没装 App 的手机还没有令牌）。
"""

from __future__ import annotations

import hashlib
import logging
import re
import secrets
import zipfile
from pathlib import Path

from fastapi import APIRouter, Depends, File, Form, HTTPException, Query, UploadFile, status
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..config import get_settings
from ..crypto import new_token
from ..db import get_session
from ..deps import require_admin, require_owner
from ..models import AdminUser, Device, MobileDevice, MobileRelease
from ..services.mobile_hub import get_config
from .mobile import require_mobile

log = logging.getLogger("flyknit.mobile")
router = APIRouter(prefix="/api/v1", tags=["mobile"])

#: APK 一般几十 MB，留足余量
MAX_APK_BYTES = 300 * 1024 * 1024
_VERSION_NAME = re.compile(r"^\d+(\.\d+){0,3}([-+][\w.]+)?$")
DOWNLOAD_PREFIX = "/api/v1/mobile/app/"


def _dir() -> Path:
    path = Path(get_settings().data_dir) / "mobile_releases"
    path.mkdir(parents=True, exist_ok=True)
    return path


def _path(r: MobileRelease) -> Path:
    return _dir() / f"{r.sha256}.apk"


def download_path(r: MobileRelease) -> str:
    return f"{DOWNLOAD_PREFIX}{r.download_token}/FlyknitBuddy-{r.version_name}.apk"


async def latest(session: AsyncSession) -> MobileRelease | None:
    """已发布、安装包还在的版本里 version_code 最大的那个。"""
    rows = (await session.scalars(
        select(MobileRelease).where(MobileRelease.published.is_(True)).order_by(MobileRelease.version_code.desc())
    )).all()
    for r in rows:
        if _path(r).exists():
            return r
        log.error("手机 App %s 的安装包文件丢失，暂不下发", r.version_name)
    return None


class MobileReleaseOut(BaseModel):
    id: int
    version_name: str
    version_code: int
    notes: str
    filename: str
    size: int
    sha256: str
    published: bool
    uploaded_by: str
    created_at: str
    download_path: str
    file_missing: bool


def _out(r: MobileRelease) -> MobileReleaseOut:
    return MobileReleaseOut(
        id=r.id, version_name=r.version_name, version_code=r.version_code, notes=r.notes, filename=r.filename,
        size=r.size, sha256=r.sha256, published=r.published, uploaded_by=r.uploaded_by,
        created_at=r.created_at.isoformat() if r.created_at else "", download_path=download_path(r),
        file_missing=not _path(r).exists(),
    )


# ---------- 后台 ----------

@router.get("/admin/mobile/releases", response_model=list[MobileReleaseOut], dependencies=[Depends(require_admin)])
async def list_releases(session: AsyncSession = Depends(get_session)):
    rows = (await session.scalars(select(MobileRelease).order_by(MobileRelease.version_code.desc()))).all()
    return [_out(r) for r in rows]


@router.post("/admin/mobile/releases", response_model=MobileReleaseOut, status_code=201)
async def upload_release(
    file: UploadFile = File(...),
    version_name: str = Form(..., min_length=1, max_length=50),
    version_code: int = Form(..., ge=1, le=2_100_000_000),
    notes: str = Form("", max_length=20000),
    owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    """上传一个 APK。和 manifest.json 里的「版本名称 / 版本号」填一样的；传完要再发布才下发。"""
    version_name = version_name.strip()
    if not _VERSION_NAME.match(version_name):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "版本名称要写成 0.1.0 这样的点分数字")
    if (file.filename or "").lower().rsplit(".", 1)[-1] != "apk":
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "只支持 .apk 安装包")
    if await session.scalar(select(MobileRelease).where(MobileRelease.version_code == version_code)):
        raise HTTPException(status.HTTP_409_CONFLICT, f"版本号 {version_code} 已经存在。每次打包都要把 manifest.json 里的版本号加 1")
    newest = await session.scalar(select(MobileRelease).order_by(MobileRelease.version_code.desc()))
    if newest is not None and version_code < newest.version_code:
        log.warning("上传的手机 App 版本号 %s 比已有的 %s 小：只有更早的手机会收到它", version_code, newest.version_code)

    tmp = _dir() / f".upload-{new_token()[:16]}"
    digest = hashlib.sha256()
    size = 0
    try:
        with tmp.open("wb") as out:
            while chunk := await file.read(1024 * 1024):
                size += len(chunk)
                if size > MAX_APK_BYTES:
                    raise HTTPException(status.HTTP_400_BAD_REQUEST, "超过 300 MB")
                digest.update(chunk)
                out.write(chunk)
        if size == 0:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "文件是空的")
        # APK 就是 zip，里面一定有 AndroidManifest.xml
        try:
            with zipfile.ZipFile(tmp) as zf:
                if "AndroidManifest.xml" not in zf.namelist():
                    raise HTTPException(status.HTTP_400_BAD_REQUEST, "这不是 Android 安装包（缺少 AndroidManifest.xml）")
        except zipfile.BadZipFile:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "这不是 Android 安装包") from None
        release = MobileRelease(
            version_name=version_name, version_code=version_code, notes=notes.strip(),
            filename=Path(file.filename or "app.apk").name[:255], size=size, sha256=digest.hexdigest(),
            download_token=secrets.token_urlsafe(18), published=False,
            uploaded_by=owner.username if owner else "admin_token",
        )
        target = _path(release)
        if target.exists():
            tmp.unlink()
        else:
            tmp.replace(target)
    finally:
        tmp.unlink(missing_ok=True)
    session.add(release)
    await session.commit()
    log.info("上传手机 App %s（%s，%d 字节）", version_name, version_code, size)
    return _out(release)


class MobileReleasePatch(BaseModel):
    published: bool | None = None
    notes: str | None = Field(default=None, max_length=20000)


@router.patch("/admin/mobile/releases/{release_id}", response_model=MobileReleaseOut)
async def update_release(release_id: int, data: MobileReleasePatch, owner: AdminUser | None = Depends(require_owner),
                         session: AsyncSession = Depends(get_session)):
    r = await session.get(MobileRelease, release_id)
    if r is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "版本不存在")
    if data.notes is not None:
        r.notes = data.notes.strip()
    if data.published is not None:
        if data.published and not _path(r).exists():
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "安装包文件丢失，不能发布。请重新上传这个版本")
        r.published = data.published
        log.warning("%s 把手机 App %s 设为 %s", owner.username if owner else "admin_token", r.version_name,
                    "已发布" if data.published else "未发布")
    await session.commit()
    return _out(r)


@router.delete("/admin/mobile/releases/{release_id}", status_code=204)
async def delete_release(release_id: int, _owner: AdminUser | None = Depends(require_owner),
                         session: AsyncSession = Depends(get_session)):
    r = await session.get(MobileRelease, release_id)
    if r is None:
        return
    path = _path(r)
    await session.delete(r)
    await session.commit()
    if not await session.scalar(select(MobileRelease).where(MobileRelease.sha256 == r.sha256)):
        path.unlink(missing_ok=True)


# ---------- 手机 ----------

class MobileUpdateOut(BaseModel):
    available: bool
    version_name: str = ""
    version_code: int = 0
    notes: str = ""
    size: int = 0
    sha256: str = ""
    #: 相对服务器地址的下载路径
    download_path: str = ""


@router.get("/mobile/update", response_model=MobileUpdateOut)
async def check_update(version_code: int = Query(default=0, ge=0),
                       ctx: tuple[MobileDevice, Device] = Depends(require_mobile),
                       session: AsyncSession = Depends(get_session)):
    mobile, _ = ctx
    r = await latest(session)
    if r is None or r.version_code <= version_code:
        return MobileUpdateOut(available=False)
    log.info("手机 #%s 当前 %s，下发 App %s（%s）", mobile.id, version_code, r.version_name, r.version_code)
    return MobileUpdateOut(available=True, version_name=r.version_name, version_code=r.version_code, notes=r.notes,
                           size=r.size, sha256=r.sha256, download_path=download_path(r))


@router.get("/mobile/app/{token}/{filename}")
async def download(token: str, filename: str, session: AsyncSession = Depends(get_session)):
    """下载 APK。不要求登录（没装 App 的手机还没有令牌），靠链接里的随机串；只给已发布的版本，后台关了手机端也不给。"""
    r = await session.scalar(select(MobileRelease).where(MobileRelease.download_token == token))
    if r is None or not r.published or not (await get_config(session))["enabled"] or not _path(r).exists():
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个安装包")
    return FileResponse(_path(r), media_type="application/vnd.android.package-archive",
                        filename=f"FlyknitBuddy-{r.version_name}.apk")
