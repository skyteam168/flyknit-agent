from datetime import datetime, timezone

from sqlalchemy import JSON, Boolean, DateTime, ForeignKey, Integer, String, Text
from sqlalchemy.orm import Mapped, mapped_column, relationship

from .db import Base


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


class Provider(Base):
    """模型提供方：内部推理服务、阿里云百炼等，统一按 OpenAI 兼容接口访问。"""

    __tablename__ = "providers"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    name: Mapped[str] = mapped_column(String(100))
    base_url: Mapped[str] = mapped_column(String(500))
    api_key_enc: Mapped[str] = mapped_column(Text, default="")
    enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)

    models: Mapped[list["ModelConfig"]] = relationship(back_populates="provider", cascade="all, delete-orphan")


class ModelConfig(Base):
    __tablename__ = "models"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    provider_id: Mapped[int] = mapped_column(ForeignKey("providers.id", ondelete="CASCADE"))
    name: Mapped[str] = mapped_column(String(100))  # 展示名
    model: Mapped[str] = mapped_column(String(200))  # 上游模型名
    supports_tools: Mapped[bool] = mapped_column(Boolean, default=True)
    supports_vision: Mapped[bool] = mapped_column(Boolean, default=False)
    context_length: Mapped[int] = mapped_column(Integer, default=131072)
    extra_body: Mapped[dict] = mapped_column(JSON, default=dict)  # 透传给上游的额外参数
    enabled: Mapped[bool] = mapped_column(Boolean, default=True)

    provider: Mapped[Provider] = relationship(back_populates="models", lazy="joined")


class RouteRule(Base):
    """场景 → 模型 的映射。scene: chat / agent / translate / title / vision"""

    __tablename__ = "routes"

    scene: Mapped[str] = mapped_column(String(32), primary_key=True)
    model_id: Mapped[int | None] = mapped_column(ForeignKey("models.id", ondelete="SET NULL"), nullable=True)
    fallback_model_id: Mapped[int | None] = mapped_column(
        ForeignKey("models.id", ondelete="SET NULL"), nullable=True
    )


class Device(Base):
    __tablename__ = "devices"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    token_hash: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    machine_name: Mapped[str] = mapped_column(String(200))
    user_name: Mapped[str] = mapped_column(String(200), default="")
    os_version: Mapped[str] = mapped_column(String(200), default="")
    client_version: Mapped[str] = mapped_column(String(50), default="")
    ui_language: Mapped[str] = mapped_column(String(10), default="zh-CN")
    disabled: Mapped[bool] = mapped_column(Boolean, default=False)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    last_seen: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)


class Setting(Base):
    """键值配置：policy、smb 等。"""

    __tablename__ = "settings"

    key: Mapped[str] = mapped_column(String(100), primary_key=True)
    value: Mapped[dict] = mapped_column(JSON, default=dict)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, onupdate=utcnow)


class AuditLog(Base):
    __tablename__ = "audit_logs"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    device_id: Mapped[int | None] = mapped_column(ForeignKey("devices.id", ondelete="SET NULL"), nullable=True)
    machine_name: Mapped[str] = mapped_column(String(200), default="")
    user_name: Mapped[str] = mapped_column(String(200), default="")
    conversation_id: Mapped[str] = mapped_column(String(64), default="")
    tool_name: Mapped[str] = mapped_column(String(100))
    arguments: Mapped[str] = mapped_column(Text, default="")
    risk: Mapped[str] = mapped_column(String(20))  # blocked / confirm / auto
    decision: Mapped[str] = mapped_column(String(20))  # blocked / approved / rejected / auto
    status: Mapped[str] = mapped_column(String(20), default="")  # ok / error / skipped
    summary: Mapped[str] = mapped_column(Text, default="")
    occurred_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, index=True)
