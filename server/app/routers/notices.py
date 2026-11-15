"""
消息通知（docs/notices.md）。

- 管理端：写通知（富文本正文、图片视频）、发布 / 定时发布 / 撤回、看谁读了。
- 员工端：列出这台电脑能看到的通知、看详情、标已读。
- 媒体：/api/v1/notices/media/{id}。员工端（设备令牌，经宿主代理）、管理端（令牌或编辑器里的临时签名地址）能取；
  视频支持 Range，可以拖动进度条。

范围和正文清洗都在服务端执行（services/notice_service）：员工端拿不到范围外通知的任何内容。
"""

import logging
import secrets
from datetime import datetime, timezone
from pathlib import Path

from fastapi import APIRouter, Depends, File, Header, HTTPException, Query, UploadFile, status
from fastapi.responses import FileResponse
from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..config import get_settings
from ..crypto import hash_token
from ..db import get_session
from ..deps import admin_name, require_admin, require_device
from ..models import Device, Notice, NoticeMedia, NoticeRead
from ..schemas import NoticeAdminOut, NoticeIn, NoticeListOut, NoticeMediaOut, NoticeOut, NoticeReaderOut
from ..services import config_events, item_scope, notice_service

log = logging.getLogger("flyknit.notices")
admin_router = APIRouter(prefix="/api/v1/admin/notices", tags=["notices"], dependencies=[Depends(require_admin)])
client_router = APIRouter(prefix="/api/v1/client/notices", tags=["notices"])
media_router = APIRouter(prefix="/api/v1/notices/media", tags=["notices"])


def _media_dir() -> Path:
    path = Path(get_settings().data_dir) / "notice_media"
    path.mkdir(parents=True, exist_ok=True)
    return path


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _aware(dt: datetime | None) -> datetime | None:
    if dt is None:
        return None
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


async def _get(session: AsyncSession, notice_id: str) -> Notice:
    notice = await session.get(Notice, notice_id)
    if notice is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "通知不存在")
    return notice


def _apply(notice: Notice, body: NoticeIn) -> None:
    html, _ = notice_service.sanitize_html(body.body_html)
    notice.kind = body.kind
    notice.title = body.title.strip()
    notice.summary = body.summary.strip()
    notice.body_html = html
    notice.important = body.important
    notice.action_label = body.action_label.strip()
    notice.action_url = notice_service.safe_action_url(body.action_url)
    if notice.action_label and not notice.action_url:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "按钮地址只能是 http:// 或 https:// 开头的网址")
    if not notice.action_label:
        notice.action_url = ""
    notice.scope_departments = [d.strip() for d in body.scope_departments if d.strip()]
    notice.scope_devices = sorted(set(body.scope_devices))
    notice.publish_at = _aware(body.publish_at)
    notice.expire_at = _aware(body.expire_at)
    if notice.publish_at and notice.expire_at and notice.expire_at <= notice.publish_at:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "过期时间要晚于发布时间")


async def _admin_out(session: AsyncSession, notice: Notice, devices: list[Device] | None = None) -> NoticeAdminOut:
    if devices is None:
        devices = (await session.scalars(select(Device).where(Device.disabled.is_(False)))).all()
    audience = {d.id for d in devices if item_scope.in_scope(notice, d)}
    read_ids = set((await session.scalars(select(NoticeRead.device_id).where(NoticeRead.notice_id == notice.id))).all())
    now = _now()
    start = _aware(notice.publish_at)
    end = _aware(notice.expire_at)
    return NoticeAdminOut(
        id=notice.id, kind=notice.kind, title=notice.title, summary=notice.summary,
        body_html=notice_service.signed_body(notice.body_html, get_settings().secret_key),
        important=notice.important, action_label=notice.action_label, action_url=notice.action_url,
        scope_departments=list(notice.scope_departments or []), scope_devices=[int(x) for x in (notice.scope_devices or [])],
        status=notice.status, publish_at=notice.publish_at, expire_at=notice.expire_at, published_at=notice.published_at,
        author=notice.author, created_at=notice.created_at, updated_at=notice.updated_at,
        reads=len(read_ids & audience), audience=len(audience),
        scheduled=notice.status == "published" and bool(start and start > now),
        expired=bool(end and end <= now),
    )


# ---------- 管理端 ----------


@admin_router.get("", response_model=list[NoticeAdminOut])
async def admin_list(session: AsyncSession = Depends(get_session)):
    rows = (await session.scalars(select(Notice).order_by(Notice.created_at.desc()))).all()
    devices = (await session.scalars(select(Device).where(Device.disabled.is_(False)))).all()
    return [await _admin_out(session, n, devices) for n in rows]


@admin_router.get("/kinds")
async def admin_kinds():
    return [{"value": k, "label": notice_service.KIND_LABELS[k]} for k in notice_service.KINDS]


@admin_router.get("/{notice_id}", response_model=NoticeAdminOut)
async def admin_get(notice_id: str, session: AsyncSession = Depends(get_session)):
    return await _admin_out(session, await _get(session, notice_id))


@admin_router.post("", response_model=NoticeAdminOut, status_code=201)
async def admin_create(body: NoticeIn, session: AsyncSession = Depends(get_session), who: str = Depends(admin_name)):
    notice = Notice(id="nt_" + secrets.token_hex(8), author=who, status="draft")
    _apply(notice, body)
    session.add(notice)
    await session.commit()
    log.info("通知已创建：%s（%s）by %s", notice.id, notice.title, who)
    return await _admin_out(session, notice)


@admin_router.put("/{notice_id}", response_model=NoticeAdminOut)
async def admin_update(notice_id: str, body: NoticeIn, session: AsyncSession = Depends(get_session), who: str = Depends(admin_name)):
    notice = await _get(session, notice_id)
    _apply(notice, body)
    await session.commit()
    if notice.status == "published":
        config_events.bump()
    log.info("通知已修改：%s by %s", notice.id, who)
    return await _admin_out(session, notice)


@admin_router.post("/{notice_id}/publish", response_model=NoticeAdminOut)
async def admin_publish(notice_id: str, session: AsyncSession = Depends(get_session), who: str = Depends(admin_name)):
    notice = await _get(session, notice_id)
    end = _aware(notice.expire_at)
    if end and end <= _now():
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "过期时间已经过了，改一下再发布")
    notice.status = "published"
    notice.published_at = _now()
    await session.commit()
    config_events.bump()
    log.info("通知已发布：%s by %s", notice.id, who)
    return await _admin_out(session, notice)


@admin_router.post("/{notice_id}/withdraw", response_model=NoticeAdminOut)
async def admin_withdraw(notice_id: str, session: AsyncSession = Depends(get_session), who: str = Depends(admin_name)):
    notice = await _get(session, notice_id)
    notice.status = "withdrawn"
    await session.commit()
    config_events.bump()
    log.info("通知已撤回：%s by %s", notice.id, who)
    return await _admin_out(session, notice)


@admin_router.delete("/{notice_id}", status_code=204)
async def admin_delete(notice_id: str, session: AsyncSession = Depends(get_session), who: str = Depends(admin_name)):
    notice = await _get(session, notice_id)
    if notice.status == "published":
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "已发布的通知要先撤回再删除")
    await session.execute(delete(NoticeRead).where(NoticeRead.notice_id == notice.id))
    await session.delete(notice)
    await session.commit()
    log.info("通知已删除：%s by %s", notice_id, who)


@admin_router.get("/{notice_id}/reads", response_model=list[NoticeReaderOut])
async def admin_reads(notice_id: str, session: AsyncSession = Depends(get_session)):
    await _get(session, notice_id)
    rows = (await session.execute(
        select(NoticeRead, Device).join(Device, Device.id == NoticeRead.device_id)
        .where(NoticeRead.notice_id == notice_id).order_by(NoticeRead.read_at.desc())
    )).all()
    return [NoticeReaderOut(device_id=d.id, machine_name=d.machine_name, user_name=d.user_name,
                            department=getattr(d, "department", "") or "", read_at=r.read_at) for r, d in rows]


@admin_router.post("/media", response_model=NoticeMediaOut, status_code=201)
async def admin_upload(file: UploadFile = File(...), session: AsyncSession = Depends(get_session), who: str = Depends(admin_name)):
    head = await file.read(16)
    mime = notice_service.sniff_mime(head)
    if mime is None:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "只支持 PNG、JPG、GIF、WebP 图片和 MP4、WebM 视频")
    limit = notice_service.VIDEO_MAX if mime.startswith("video/") else notice_service.IMAGE_MAX
    media_id = secrets.token_hex(16)
    path = _media_dir() / media_id
    size = len(head)
    try:
        with path.open("wb") as out:
            out.write(head)
            while chunk := await file.read(1024 * 1024):
                size += len(chunk)
                if size > limit:
                    raise HTTPException(status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
                                        f"文件太大：{'视频' if mime.startswith('video/') else '图片'}最大 {limit // 1024 // 1024} MB")
                out.write(chunk)
    except BaseException:
        path.unlink(missing_ok=True)
        raise
    session.add(NoticeMedia(id=media_id, mime=mime, size=size, filename=(file.filename or "")[:200], uploaded_by=who))
    await session.commit()
    secret = get_settings().secret_key
    return NoticeMediaOut(id=media_id, mime=mime, size=size, src=notice_service.MEDIA_PREFIX + media_id,
                          preview=notice_service.sign(media_id, secret))


# ---------- 员工端 ----------


async def _visible(session: AsyncSession, device: Device) -> list[Notice]:
    rows = (await session.scalars(select(Notice).where(Notice.status == "published"))).all()
    now = _now()
    items = [n for n in rows if notice_service.visible(n, device, now)]
    # 重要的在前，其余按发布时间倒序
    items.sort(key=lambda n: (_aware(n.publish_at) or _aware(n.published_at) or n.created_at), reverse=True)
    items.sort(key=lambda n: not n.important)
    return items


def _client_out(n: Notice, read: bool, with_body: bool = False) -> NoticeOut:
    return NoticeOut(
        id=n.id, kind=n.kind, title=n.title, summary=n.summary or notice_service.plain_text(n.body_html),
        important=n.important, action_label=n.action_label, action_url=n.action_url,
        published_at=_aware(n.publish_at) or n.published_at, expire_at=n.expire_at, read=read,
        body_html=n.body_html if with_body else "",
    )


async def _read_ids(session: AsyncSession, device: Device) -> set[str]:
    return set((await session.scalars(select(NoticeRead.notice_id).where(NoticeRead.device_id == device.id))).all())


@client_router.get("", response_model=NoticeListOut)
async def client_list(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    items = await _visible(session, device)
    read = await _read_ids(session, device)
    out = [_client_out(n, n.id in read) for n in items[:200]]
    return NoticeListOut(items=out, unread=sum(1 for n in out if not n.read))


@client_router.get("/{notice_id}", response_model=NoticeOut)
async def client_get(notice_id: str, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    notice = await session.get(Notice, notice_id)
    # 不在范围内、没发布、过期的一律当不存在，不透露有这条
    if notice is None or not notice_service.visible(notice, device):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "通知不存在或已撤回")
    return _client_out(notice, notice.id in await _read_ids(session, device), with_body=True)


async def _mark(session: AsyncSession, device: Device, ids: list[str]) -> None:
    have = await _read_ids(session, device)
    for nid in ids:
        if nid not in have:
            session.add(NoticeRead(notice_id=nid, device_id=device.id))
            have.add(nid)
    await session.commit()


@client_router.post("/{notice_id}/read", status_code=204)
async def client_read(notice_id: str, device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    notice = await session.get(Notice, notice_id)
    if notice is None or not notice_service.visible(notice, device):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "通知不存在或已撤回")
    await _mark(session, device, [notice_id])


@client_router.post("/read-all", status_code=204)
async def client_read_all(device: Device = Depends(require_device), session: AsyncSession = Depends(get_session)):
    await _mark(session, device, [n.id for n in await _visible(session, device)])


# ---------- 媒体 ----------


async def _can_fetch(session: AsyncSession, authorization: str | None, media_id: str, exp: str, sig: str) -> bool:
    if sig and notice_service.verify(media_id, exp, sig, get_settings().secret_key):
        return True
    if not authorization:
        return False
    token = authorization.removeprefix("Bearer ").strip()
    device = await session.scalar(select(Device).where(Device.token_hash == hash_token(token)))
    if device is not None:
        return not device.disabled
    try:
        await require_admin(authorization, session)
        return True
    except HTTPException:
        return False


@media_router.get("/{media_id}")
async def media(
    media_id: str,
    exp: str = Query(default=""),
    sig: str = Query(default=""),
    authorization: str | None = Header(default=None),
    session: AsyncSession = Depends(get_session),
):
    if not (len(media_id) == 32 and all(c in "0123456789abcdef" for c in media_id)):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "文件不存在")
    if not await _can_fetch(session, authorization, media_id, exp, sig):
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "需要登录")
    row = await session.get(NoticeMedia, media_id)
    path = _media_dir() / media_id
    if row is None or not path.is_file():
        raise HTTPException(status.HTTP_404_NOT_FOUND, "文件不存在")
    # FileResponse 自带 Range（视频拖动进度条）
    return FileResponse(path, media_type=row.mime, headers={"Cache-Control": "private, max-age=86400",
                                                            "X-Content-Type-Options": "nosniff"})

