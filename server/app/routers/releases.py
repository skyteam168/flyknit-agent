"""员工端的版本发布与自动更新。

发布一个版本，所有电脑会自己发现、下载、校验、装上——不需要 IT 一台台跑。
这是这套系统里影响面最大的动作（它替换的是员工电脑上正在跑的程序本身），
所以上传和发布都限超级管理员，而且分两步：传完先不发布，确认无误再发。

客户端那一侧只认 sha256。文件对不上就不装——宁可停在旧版本，也不能把一堆
校验不过的文件铺到全厂电脑上。
"""

import hashlib
import json
import logging
import re
import shutil
import zipfile
from datetime import datetime, timezone
from pathlib import Path

from fastapi import APIRouter, Depends, File, Form, HTTPException, UploadFile, status
from fastapi.responses import FileResponse
from starlette.background import BackgroundTask
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from ..config import get_settings
from ..crypto import hash_token, new_token
from ..db import get_session
from ..deps import require_admin, require_agent, require_device, require_owner
from ..models import AdminUser, ClientRelease, Device, EnrollmentTicket, MachineAgent
from ..schemas import ClientUpdateOut, ReleaseOut, ReleasePatch, TicketIn, TicketOut
from ..services import versions

log = logging.getLogger("flyknit.releases")
router = APIRouter(prefix="/api/v1", tags=["releases"])

#: 客户端是 self-contained 发布，整个文件夹压完 150 MB 上下。留足余量
MAX_RELEASE_BYTES = 1024 * 1024 * 1024
_VERSION = re.compile(r"^v?\d+(\.\d+){0,3}([-+][\w.]+)?$")


def _release_dir() -> Path:
    path = Path(get_settings().data_dir) / "releases"
    path.mkdir(parents=True, exist_ok=True)
    return path


def _release_path(release: ClientRelease) -> Path:
    return _release_dir() / f"{release.sha256}.zip"


async def _latest(session: AsyncSession) -> ClientRelease | None:
    """已发布的版本里最新的那个。按版本号比，不按上传时间——补传一个旧版本不该变成「最新」。"""
    rows = await session.scalars(select(ClientRelease).where(ClientRelease.published.is_(True)))
    newest: ClientRelease | None = None
    for row in rows:
        if newest is None or versions.is_newer(row.version, newest.version):
            newest = row
    return newest


# ---------- 管理端 ----------

@router.get("/admin/releases", response_model=list[ReleaseOut], dependencies=[Depends(require_admin)])
async def list_releases(session: AsyncSession = Depends(get_session)):
    rows = await session.scalars(select(ClientRelease).order_by(ClientRelease.id.desc()))
    return list(rows)


@router.post("/admin/releases", response_model=ReleaseOut, status_code=201)
async def upload_release(
    file: UploadFile = File(...),
    version: str = Form(..., min_length=1, max_length=50),
    notes: str = Form("", max_length=20000),
    owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    """
    上传一个新版本（publish 出来的整个文件夹打成的 zip）。

    传完不会立刻下发：要再调一次 PATCH 把 published 置 True。发布是不可逆的动作——
    几百台电脑会在几分钟内跟着装上，传错了这一步是最后的拦截点。
    """
    version = version.strip()
    if not _VERSION.match(version):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "版本号要写成 0.2.0 这样的点分数字，可带 -beta.1 之类的后缀")
    if (file.filename or "").lower().rsplit(".", 1)[-1] != "zip":
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "只支持 zip：把 publish 出来的整个文件夹打成一个 zip")
    if await session.scalar(select(ClientRelease).where(ClientRelease.version == version)):
        raise HTTPException(status.HTTP_409_CONFLICT, f"版本 {version} 已经存在。改个版本号，或先删掉旧的那个")

    tmp = _release_dir() / f".upload-{new_token()[:16]}"
    digest = hashlib.sha256()
    size = 0
    try:
        with tmp.open("wb") as out:
            while chunk := await file.read(1024 * 1024):
                size += len(chunk)
                if size > MAX_RELEASE_BYTES:
                    raise HTTPException(status.HTTP_400_BAD_REQUEST, "超过 1 GB")
                digest.update(chunk)
                out.write(chunk)
        if size == 0:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "文件是空的")
        # 程序自己报的版本号是编译时写进 exe 的，不是这里填的。两个对不上的话，
        # 员工电脑装完还报旧版本号，就会一直觉得「有新版本」，反复下载重装
        built = _built_version(tmp)
        if built is not None and versions.compare(built, version) != 0:
            raise HTTPException(
                status.HTTP_400_BAD_REQUEST,
                f"zip 里的程序版本是 {built}，和填写的 {version} 对不上。"
                f"打包时要把版本号编进程序：dotnet publish 加上 -p:Version={version.lstrip('vV')}"
                f"（或用 client\\scripts\\publish-client.ps1 -Version {version.lstrip('vV')}），"
                f"或者把这里的版本号改成 {built}",
            )
        release = ClientRelease(
            version=version,
            notes=notes.strip(),
            filename=Path(file.filename or "release.zip").name[:255],
            size=size,
            sha256=digest.hexdigest(),
            published=False,
            uploaded_by=owner.username if owner else "admin_token",
        )
        target = _release_path(release)
        if target.exists():
            tmp.unlink()  # 同样的内容已经有了，不重复存一份
        else:
            tmp.replace(target)
    finally:
        tmp.unlink(missing_ok=True)

    session.add(release)
    await session.commit()
    log.info("上传客户端版本 %s（%d 字节，%s）", version, size, release.sha256[:12])
    return release


@router.patch("/admin/releases/{release_id}", response_model=ReleaseOut)
async def update_release(
    release_id: int,
    data: ReleasePatch,
    owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    release = await session.get(ClientRelease, release_id)
    if release is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "版本不存在")
    if data.notes is not None:
        release.notes = data.notes.strip()
    if data.published is not None:
        if data.published and not _release_path(release).exists():
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "安装包文件丢失，不能发布。请重新上传这个版本")
        release.published = data.published
        who = owner.username if owner else "admin_token"
        log.warning("%s 把客户端版本 %s 设为 %s", who, release.version, "已发布" if data.published else "未发布")
    await session.commit()
    return release


@router.delete("/admin/releases/{release_id}", status_code=204)
async def delete_release(
    release_id: int,
    _owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    release = await session.get(ClientRelease, release_id)
    if release is None:
        return
    path = _release_path(release)
    await session.delete(release)
    await session.commit()
    # 同一份内容可能被别的版本记录引用着，没人引用了才删文件
    if not await session.scalar(select(ClientRelease).where(ClientRelease.sha256 == release.sha256)):
        path.unlink(missing_ok=True)


# ---------- 员工端安装包（带服务器地址和安装凭证） ----------

#: 安装包里的开通文件，放在 FlyknitBuddy.exe 旁边。客户端首次启动读它，员工只需要点「登录」
PROVISION_FILE = "flyknit.provision.json"
_MAIN_EXE = "flyknitbuddy.exe"


#: 安装程序外壳，在发布出来的 FlyknitBuddy 文件夹里（publish-client.ps1 放进去）
SETUP_EXE = "FlyknitSetup.exe"
#: 安装程序结尾的标记。和 Flyknit.Setup/Payload.cs 里的保持一致
SETUP_MAGIC = b"FLYKNIT-SETUP-01"


def setup_trailer(offset: int, length: int) -> bytes:
    """32 字节：16 字节标记 + zip 起始位置 + zip 长度（各 8 字节小端）。"""
    return SETUP_MAGIC + offset.to_bytes(8, "little") + length.to_bytes(8, "little")


def _built_version(path: Path) -> str | None:
    """
    zip 里主程序编译时写进去的版本号（从 FlyknitBuddy.deps.json 里读：dotnet publish 会把
    项目自己记成「FlyknitBuddy/版本号」）。读不出来（老包、不是 zip、手工拼的）返回 None，不拦。
    """
    try:
        with zipfile.ZipFile(path) as zf:
            folder = _exe_folder(zf)
            if folder is None:
                return None
            name = next((n for n in zf.namelist()
                         if n.replace("\\", "/").lower() == (folder + "FlyknitBuddy.deps.json").lower()), None)
            if name is None:
                return None
            deps = json.loads(zf.read(name).decode("utf-8-sig"))
    except (zipfile.BadZipFile, OSError, ValueError, UnicodeDecodeError):
        return None
    for key in (deps.get("libraries") or {}) if isinstance(deps, dict) else ():
        lib, _, ver = key.partition("/")
        if lib.lower() == "flyknitbuddy" and ver:
            return ver
    return None


def _exe_folder(zf: zipfile.ZipFile) -> str | None:
    """zip 里 FlyknitBuddy.exe 所在的目录（可能在根目录，也可能套了一层文件夹）。没有就返回 None。"""
    for name in zf.namelist():
        if name.replace("\\", "/").rsplit("/", 1)[-1].lower() == _MAIN_EXE:
            return name.replace("\\", "/").rsplit("/", 1)[0] + "/" if "/" in name.replace("\\", "/") else ""
    return None


@router.post("/admin/client-package")
async def download_client_package(
    data: TicketIn,
    owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    """
    下载员工端安装包：最新发布的版本 + 一张新的安装凭证和服务器地址（flyknit.provision.json）。
    员工解压运行后直接点「登录」，不用填服务器地址和注册密钥。

    每下载一次生成一张凭证，在「安装凭证」里能看到用它注册了几台，包外泄就吊销那一张。
    限超级管理员：这个包能让任何人往系统里注册电脑。
    """
    server_url = data.server_url.strip().rstrip("/")
    if not re.match(r"^https?://[^\s/]+", server_url):
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "服务器地址要写成 http://10.0.0.5:8000 这样的完整地址")
    latest = await _latest(session)
    if latest is None or not _release_path(latest).exists():
        raise HTTPException(status.HTTP_404_NOT_FOUND, "还没有已发布的员工端版本：先在下面上传一个版本并发布")

    with zipfile.ZipFile(_release_path(latest)) as zf:
        folder = _exe_folder(zf)
    if folder is None:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, f"版本 {latest.version} 的 zip 里找不到 FlyknitBuddy.exe")

    # 先确认能做成安装程序，再发凭证：做不成就别留一张没人用的凭证
    stub: bytes | None = None
    if data.format == "exe":
        with zipfile.ZipFile(_release_path(latest)) as zf:
            name = next((n for n in zf.namelist() if n.replace("\\", "/").lower() == (folder + SETUP_EXE).lower()), None)
            if name is None:
                raise HTTPException(
                    status.HTTP_400_BAD_REQUEST,
                    f"版本 {latest.version} 的包里没有安装程序（{SETUP_EXE}）。用新版 publish-client.ps1 重新打包上传，或者先选 zip",
                )
            stub = zf.read(name)

    who = owner.username if owner else "admin_token"
    token = new_token()
    ticket = EnrollmentTicket(
        token_hash=hash_token(token),
        label=data.label.strip()[:200] or f"{latest.version} 安装包",
        server_url=server_url,
        created_by=who,
    )
    session.add(ticket)
    await session.commit()

    # 复制一份再追加一个文件：不用把 100 多 MB 重新压一遍
    out = _release_dir() / f".package-{new_token()[:16]}.zip"
    shutil.copyfile(_release_path(latest), out)
    provision = {
        "server_url": server_url,
        "ticket": token,
        "label": ticket.label,
        "version": latest.version,
        "issued_at": datetime.now(timezone.utc).isoformat(),
    }
    with zipfile.ZipFile(out, "a", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.writestr(folder + PROVISION_FILE, json.dumps(provision, ensure_ascii=False, indent=2))
    log.warning("%s 下载了员工端安装包 %s（%s，凭证 #%s，服务器 %s）", who, latest.version, data.format, ticket.id, server_url)
    if stub is None:
        return FileResponse(
            out,
            media_type="application/zip",
            filename=f"FlyknitBuddy-{latest.version}.zip",
            background=BackgroundTask(out.unlink, missing_ok=True),
        )
    # 安装程序 = 安装程序外壳 + 上面这个 zip + 结尾 32 字节的标记（记着 zip 从哪开始、多长），
    # 外壳运行时从自己身上读出 zip。在服务端拼，每次下载都带一张新凭证
    exe = out.with_suffix(".exe")
    try:
        with exe.open("wb") as dst:
            dst.write(stub)
            with out.open("rb") as src:
                shutil.copyfileobj(src, dst, 1024 * 1024)
            dst.write(setup_trailer(len(stub), out.stat().st_size))
    finally:
        out.unlink(missing_ok=True)
    return FileResponse(
        exe,
        media_type="application/vnd.microsoft.portable-executable",
        filename=f"FlyknitBuddy-Setup-{latest.version}.exe",
        background=BackgroundTask(exe.unlink, missing_ok=True),
    )


@router.get("/admin/tickets", response_model=list[TicketOut])
async def list_tickets(_owner: AdminUser | None = Depends(require_owner), session: AsyncSession = Depends(get_session)):
    return list(await session.scalars(select(EnrollmentTicket).order_by(EnrollmentTicket.id.desc())))


@router.post("/admin/tickets/{ticket_id}/revoke", response_model=TicketOut)
async def revoke_ticket(
    ticket_id: int,
    owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    """吊销一张安装凭证：用这个包新装的电脑注册不上，已经注册的不受影响。"""
    ticket = await session.get(EnrollmentTicket, ticket_id)
    if ticket is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "凭证不存在")
    ticket.revoked = True
    await session.commit()
    log.warning("%s 吊销了安装凭证 #%s（%s）", owner.username if owner else "admin_token", ticket.id, ticket.label)
    return ticket


# ---------- 客户端 ----------

async def _update_for(session: AsyncSession, version: str) -> ClientUpdateOut:
    latest = await _latest(session)
    if latest is None or not versions.is_newer(latest.version, version):
        return ClientUpdateOut(available=False)
    if not _release_path(latest).exists():
        # 文件丢了就当没有更新：让客户端去下一个下不到的包，只会反复失败
        log.error("版本 %s 的安装包文件丢失，暂不下发", latest.version)
        return ClientUpdateOut(available=False)
    return ClientUpdateOut(
        available=True,
        version=latest.version,
        notes=latest.notes,
        size=latest.size,
        sha256=latest.sha256,
    )


async def _download_latest(session: AsyncSession, version: str) -> FileResponse:
    latest = await _latest(session)
    if latest is None or (version and latest.version != version):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "没有这个版本，或它已经不是最新版")
    path = _release_path(latest)
    if not path.exists():
        raise HTTPException(status.HTTP_404_NOT_FOUND, "安装包文件丢失，请在管理后台重新上传")
    return FileResponse(path, media_type="application/zip", filename=f"FlyknitBuddy-{latest.version}.zip")


@router.get("/client/update", response_model=ClientUpdateOut)
async def check_update(
    version: str = "",
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    """这台电脑有没有新版本可装。没有就 available=false，客户端什么也不做。"""
    result = await _update_for(session, version)
    if result.available:
        device.last_seen = datetime.now(timezone.utc)
        await session.commit()
    return result


@router.get("/client/update/download")
async def download_update(
    version: str = "",
    _: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    """下载某个版本的安装包。只给已发布的。"""
    return await _download_latest(session, version)


# ---------- 运维代理替员工端升级 ----------
# 用 Setup.exe 装在 Program Files 里的员工端，员工账号没有权限替换程序目录，
# 由以 SYSTEM 运行的运维代理来下载、校验、替换。

@router.get("/agent/client-update", response_model=ClientUpdateOut)
async def agent_check_client_update(
    version: str = "",
    _: MachineAgent = Depends(require_agent),
    session: AsyncSession = Depends(get_session),
):
    return await _update_for(session, version)


@router.get("/agent/client-update/download")
async def agent_download_client_update(
    version: str = "",
    _: MachineAgent = Depends(require_agent),
    session: AsyncSession = Depends(get_session),
):
    return await _download_latest(session, version)
