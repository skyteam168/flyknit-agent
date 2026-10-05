from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, HttpUrl

Scene = Literal["chat", "agent", "translate", "title", "vision"]
SCENES: tuple[str, ...] = ("chat", "agent", "translate", "title", "vision")


# ---------- 模型提供方 ----------
class ProviderIn(BaseModel):
    name: str = Field(min_length=1, max_length=100)
    base_url: HttpUrl
    api_key: str = ""
    enabled: bool = True


class ProviderPatch(BaseModel):
    name: str | None = None
    base_url: HttpUrl | None = None
    api_key: str | None = None  # 传空字符串表示清空，不传表示不修改
    enabled: bool | None = None


class ProviderOut(BaseModel):
    id: int
    name: str
    base_url: str
    api_key_masked: str
    enabled: bool
    created_at: datetime


# ---------- 模型 ----------
class ModelIn(BaseModel):
    provider_id: int
    name: str = Field(min_length=1, max_length=100)
    model: str = Field(min_length=1, max_length=200)
    supports_tools: bool = True
    supports_vision: bool = False
    context_length: int = 131072
    extra_body: dict = Field(default_factory=dict)
    enabled: bool = True


class ModelPatch(BaseModel):
    name: str | None = None
    model: str | None = None
    supports_tools: bool | None = None
    supports_vision: bool | None = None
    context_length: int | None = None
    extra_body: dict | None = None
    enabled: bool | None = None


class ModelOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    provider_id: int
    name: str
    model: str
    supports_tools: bool
    supports_vision: bool
    context_length: int
    extra_body: dict
    enabled: bool


# ---------- 路由 ----------
class RouteIn(BaseModel):
    model_id: int | None
    fallback_model_id: int | None = None


class RouteOut(BaseModel):
    scene: str
    model_id: int | None
    fallback_model_id: int | None


# ---------- 设备 ----------
class DeviceRegisterIn(BaseModel):
    enrollment_key: str
    machine_name: str = Field(min_length=1, max_length=200)
    user_name: str = ""
    os_version: str = ""
    client_version: str = ""
    ui_language: str = "zh-CN"


class DeviceRegisterOut(BaseModel):
    device_id: int
    token: str


class DeviceOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    machine_name: str
    user_name: str
    os_version: str
    client_version: str
    ui_language: str
    disabled: bool
    created_at: datetime
    last_seen: datetime | None


class DevicePatch(BaseModel):
    disabled: bool


# ---------- SMB ----------
class SmbIn(BaseModel):
    domain: str = ""
    username: str
    password: str | None = None  # 不传表示保持原密码
    share_root: str = ""  # 例如 \\fileserver\software


class SmbOut(BaseModel):
    domain: str
    username: str
    password_set: bool
    share_root: str


# ---------- 审计 ----------
class AuditItem(BaseModel):
    conversation_id: str = ""
    tool_name: str
    arguments: str = ""
    risk: Literal["blocked", "confirm", "auto"]
    decision: Literal["blocked", "approved", "rejected", "auto"]
    status: str = ""
    summary: str = ""
    occurred_at: datetime | None = None


class AuditBatchIn(BaseModel):
    items: list[AuditItem] = Field(max_length=500)


class AuditOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    device_id: int | None
    machine_name: str
    user_name: str
    conversation_id: str
    tool_name: str
    arguments: str
    risk: str
    decision: str
    status: str
    summary: str
    occurred_at: datetime
    created_at: datetime


# ---------- 客户端配置 ----------
class SceneInfo(BaseModel):
    scene: str
    available: bool
    model_name: str = ""
    supports_tools: bool = False
    supports_vision: bool = False
    context_length: int = 0


class ClientConfigOut(BaseModel):
    server_version: str
    scenes: list[SceneInfo]
    policy: dict


class ClientModelOut(BaseModel):
    id: int
    name: str
    model: str
    provider: str
    supports_tools: bool
    supports_vision: bool


class SyncResult(BaseModel):
    total: int
    added: list[str]
    skipped: int
