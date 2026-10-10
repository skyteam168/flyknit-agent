"""意见反馈：员工在客户端「设置 → 意见反馈」里写问题、贴截图、可选附上日志包。

截图和日志包存到 data_dir/feedback/{id}/，表里只记文件名。
- 截图是员工自己挑的，后台管理员都能看；
- 日志里可能有对话记录、设备信息，按聊天正文的规矩：要有查看聊天内容的权限，下载留访问记录。
"""

import logging
import shutil
from datetime import datetime, timedelta, timezone
from pathlib import Path

from fastapi import APIRouter, Depends, File, Form, HTTPException, Query, UploadFile, status
from fastapi.responses import FileResponse
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import func, or_, select
from sqlalchemy.ext.asyncio import AsyncSession

from ..config import get_settings
from ..db import get_session
from ..deps import admin_name, require_admin, require_chat_reader, require_device, require_owner
from ..models import AdminAccess, AdminUser, Device, Feedback

log = logging.getLogger("flyknit.feedback")
client_router = APIRouter(prefix="/api/v1", tags=["feedback"])
admin_router = APIRouter(prefix="/api/v1/admin", tags=["feedback"])

MAX_CONTENT = 10_000
MAX_IMAGES = 6
MAX_IMAGE_BYTES = 5 * 1024 * 1024
MAX_LOGS_BYTES = 20 * 1024 * 1024
#: 一台电脑一天最多提交这么多条，防止客户端出 bug 刷爆磁盘
DAILY_LIMIT = 20

# 只认这几种图片，按文件头判断，不信扩展名和 Content-Type
_IMAGE_TYPES = (
    (b"\x89PNG\r\n\x1a\n", "png", "image/png"),
    (b"\xff\xd8\xff", "jpg", "image/jpeg"),
    (b"GIF87a", "gif", "image/gif"),
    (b"GIF89a", "gif", "image/gif"),
)
_MEDIA = {"png": "image/png", "jpg": "image/jpeg", "gif": "image/gif", "webp": "image/webp"}


def _image_ext(data: bytes) -> str | None:
    for magic, ext, _ in _IMAGE_TYPES:
        if data.startswith(magic):
            return ext
    if len(data) > 12 and data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "webp"
    return None


def _root() -> Path:
    return Path(get_settings().data_dir) / "feedback"


def _dir(feedback_id: int) -> Path:
    return _root() / str(feedback_id)


async def _read_limited(upload: UploadFile, limit: int, too_big: str) -> bytes:
    data = await upload.read(limit + 1)
    if len(data) > limit:
        raise HTTPException(status.HTTP_413_REQUEST_ENTITY_TOO_LARGE, too_big)
    return data


class FeedbackOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    device_id: int | None
    machine_name: str
    user_name: str
    client_version: str
    content: str
    images: list[str]
    logs_size: int
    status: str
    note: str
    handled_by: str
    handled_at: datetime | None
    created_at: datetime


class FeedbackPage(BaseModel):
    items: list[FeedbackOut]
    total: int
    open: int


class FeedbackPatch(BaseModel):
    status: str | None = Field(default=None, pattern="^(open|done)$")
    note: str | None = Field(default=None, max_length=1000)


# ---------- 客户端 ----------
@client_router.post("/client/feedback")
async def submit_feedback(
    content: str = Form(""),
    images: list[UploadFile] = File(default=[]),
    logs: UploadFile | None = File(default=None),
    device: Device = Depends(require_device),
    session: AsyncSession = Depends(get_session),
):
    text = content.strip()
    if len(text) > MAX_CONTENT:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, f"问题描述最多 {MAX_CONTENT} 字")
    if not text and not images:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, "请描述一下遇到的问题")
    if len(images) > MAX_IMAGES:
        raise HTTPException(status.HTTP_400_BAD_REQUEST, f"最多上传 {MAX_IMAGES} 张图片")

    since = datetime.now(timezone.utc) - timedelta(days=1)
    recent = await session.scalar(
        select(func.count()).select_from(Feedback).where(Feedback.device_id == device.id, Feedback.created_at >= since))
    if (recent or 0) >= DAILY_LIMIT:
        raise HTTPException(status.HTTP_429_TOO_MANY_REQUESTS, "今天提交的反馈太多了，请明天再试或直接联系 IT")

    # 先把文件都读进来校验完，再落库落盘：不合格的请求不留半截记录
    pictures: list[tuple[str, bytes]] = []
    for upload in images:
        data = await _read_limited(upload, MAX_IMAGE_BYTES, "单张图片不能超过 5 MB")
        ext = _image_ext(data)
        if ext is None:
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "只支持 PNG、JPG、GIF、WebP 图片")
        pictures.append((ext, data))
    log_bytes = b""
    if logs is not None:
        log_bytes = await _read_limited(logs, MAX_LOGS_BYTES, "日志包不能超过 20 MB")
        if log_bytes and not log_bytes.startswith(b"PK\x03\x04"):
            raise HTTPException(status.HTTP_400_BAD_REQUEST, "日志包格式不对")

    row = Feedback(
        device_id=device.id,
        machine_name=device.machine_name,
        user_name=device.user_name,
        client_version=device.client_version,
        content=text,
        logs_size=len(log_bytes),
    )
    session.add(row)
    await session.flush()
    folder = _dir(row.id)
    folder.mkdir(parents=True, exist_ok=True)
    names = []
    for i, (ext, data) in enumerate(pictures):
        name = f"image-{i + 1}.{ext}"
        (folder / name).write_bytes(data)
        names.append(name)
    if log_bytes:
        (folder / "logs.zip").write_bytes(log_bytes)
    row.images = names
    await session.commit()
    log.info("收到反馈 #%s：%s（%s），%d 张图，日志 %d 字节",
             row.id, device.machine_name, device.user_name, len(names), len(log_bytes))
    return {"id": row.id}


# ---------- 管理端 ----------
@admin_router.get("/feedback", response_model=FeedbackPage, dependencies=[Depends(require_admin)])
async def list_feedback(
    status_: str | None = Query(None, alias="status", pattern="^(open|done)$"),
    q: str = "",
    limit: int = Query(20, ge=1, le=200),
    offset: int = Query(0, ge=0),
    session: AsyncSession = Depends(get_session),
):
    query = select(Feedback)
    if status_:
        query = query.where(Feedback.status == status_)
    if q.strip():
        like = f"%{q.strip()}%"
        query = query.where(or_(Feedback.content.ilike(like), Feedback.machine_name.ilike(like), Feedback.user_name.ilike(like)))
    total = await session.scalar(select(func.count()).select_from(query.subquery()))
    rows = await session.scalars(query.order_by(Feedback.id.desc()).limit(limit).offset(offset))
    open_count = await session.scalar(select(func.count()).select_from(Feedback).where(Feedback.status == "open"))
    return FeedbackPage(items=list(rows), total=total or 0, open=open_count or 0)


async def _get(session: AsyncSession, feedback_id: int) -> Feedback:
    row = await session.get(Feedback, feedback_id)
    if row is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "这条反馈不存在")
    return row


@admin_router.get("/feedback/{feedback_id}/images/{name}", dependencies=[Depends(require_admin)])
async def feedback_image(feedback_id: int, name: str, session: AsyncSession = Depends(get_session)):
    row = await _get(session, feedback_id)
    # 只给表里记着的文件名，路径里塞 ../ 也拿不到别的
    if name not in (row.images or []):
        raise HTTPException(status.HTTP_404_NOT_FOUND, "图片不存在")
    path = _dir(row.id) / name
    if not path.is_file():
        raise HTTPException(status.HTTP_404_NOT_FOUND, "图片文件已丢失")
    return FileResponse(path, media_type=_MEDIA.get(path.suffix.lstrip("."), "application/octet-stream"))


@admin_router.get("/feedback/{feedback_id}/logs")
async def feedback_logs(
    feedback_id: int,
    reader: AdminUser = Depends(require_chat_reader),
    session: AsyncSession = Depends(get_session),
):
    """日志里可能有对话记录：只有能看聊天内容的账号能下，下载留痕。"""
    row = await _get(session, feedback_id)
    path = _dir(row.id) / "logs.zip"
    if not row.logs_size or not path.is_file():
        raise HTTPException(status.HTTP_404_NOT_FOUND, "这条反馈没有附带日志")
    session.add(AdminAccess(user_id=reader.id, username=reader.username, action="feedback_logs",
                            target=f"feedback:{row.id}", detail=f"{row.machine_name} {row.user_name}"[:300]))
    await session.commit()
    stamp = row.created_at.strftime("%Y%m%d")
    return FileResponse(path, media_type="application/zip", filename=f"feedback-{row.id}-{row.machine_name}-{stamp}.zip")


@admin_router.patch("/feedback/{feedback_id}", response_model=FeedbackOut)
async def update_feedback(
    feedback_id: int,
    data: FeedbackPatch,
    who: str = Depends(admin_name),
    session: AsyncSession = Depends(get_session),
):
    row = await _get(session, feedback_id)
    if data.status is not None and data.status != row.status:
        row.status = data.status
        row.handled_by = who if data.status == "done" else ""
        row.handled_at = datetime.now(timezone.utc) if data.status == "done" else None
    if data.note is not None:
        row.note = data.note.strip()
    await session.commit()
    return row


@admin_router.delete("/feedback/{feedback_id}", status_code=204)
async def delete_feedback(
    feedback_id: int,
    owner: AdminUser | None = Depends(require_owner),
    session: AsyncSession = Depends(get_session),
):
    row = await _get(session, feedback_id)
    await session.delete(row)
    await session.commit()
    shutil.rmtree(_dir(feedback_id), ignore_errors=True)
    log.warning("%s 删除了反馈 #%s", owner.username if owner else "admin_token", feedback_id)

