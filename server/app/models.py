from datetime import datetime, timezone

from sqlalchemy import JSON, Boolean, DateTime, ForeignKey, Integer, String, Text, UniqueConstraint
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

    # 资产台账：客户端定期重报，不是注册时采一次就完
    domain: Mapped[str] = mapped_column(String(200), default="")
    #: 客户端自己枚举的内网地址，逗号分隔（多网卡很常见）
    ip_addresses: Mapped[str] = mapped_column(String(300), default="")
    #: 服务端从连接上看到的地址。客户端伪造不了，跨 NAT 时和上面那列不一样
    observed_ip: Mapped[str] = mapped_column(String(64), default="")
    mac_address: Mapped[str] = mapped_column(String(64), default="")


class Setting(Base):
    """键值配置：policy、smb 等。"""

    __tablename__ = "settings"

    key: Mapped[str] = mapped_column(String(100), primary_key=True)
    value: Mapped[dict] = mapped_column(JSON, default=dict)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, onupdate=utcnow)


class UsageDaily(Base):
    """Token 用量：每台电脑、每天、每个场景一行。"""

    __tablename__ = "usage_daily"
    __table_args__ = (UniqueConstraint("device_id", "day", "scene", name="uq_usage_device_day_scene"),)

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    device_id: Mapped[int | None] = mapped_column(ForeignKey("devices.id", ondelete="CASCADE"), nullable=True, index=True)
    day: Mapped[str] = mapped_column(String(10), index=True)  # YYYY-MM-DD（本地时区）
    scene: Mapped[str] = mapped_column(String(20))
    prompt_tokens: Mapped[int] = mapped_column(Integer, default=0)
    completion_tokens: Mapped[int] = mapped_column(Integer, default=0)
    requests: Mapped[int] = mapped_column(Integer, default=0)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, onupdate=utcnow)


class SkillPackage(Base):
    """公司技能库里的一个技能包（zip 存在磁盘上，这里只存元数据）。"""

    __tablename__ = "skill_packages"

    name: Mapped[str] = mapped_column(String(64), primary_key=True)
    description: Mapped[str] = mapped_column(Text, default="")
    version: Mapped[str] = mapped_column(String(50), default="")
    author: Mapped[str] = mapped_column(String(100), default="")
    origin: Mapped[str] = mapped_column(String(500), default="")  # 导入来源（链接或上传的文件名）
    size: Mapped[int] = mapped_column(Integer, default=0)
    file_count: Mapped[int] = mapped_column(Integer, default=0)
    required: Mapped[bool] = mapped_column(Boolean, default=False)  # 所有电脑必装
    enabled: Mapped[bool] = mapped_column(Boolean, default=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
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
    scene: Mapped[str] = mapped_column(String(20), default="")  # agent / chat / translate
    risk: Mapped[str] = mapped_column(String(20))  # blocked / confirm / auto
    decision: Mapped[str] = mapped_column(String(20))  # blocked / approved / remembered / rejected / auto
    status: Mapped[str] = mapped_column(String(20), default="")  # ok / error / skipped
    summary: Mapped[str] = mapped_column(Text, default="")
    occurred_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, index=True)


class ChatRecord(Base):
    """
    聊天记录。内容本来就从网关过，所以在那里落库，客户端不用再传一份。

    只存用户说的话和 AI 的回答——系统提示词、记忆注入、工具输出每轮都在重复，
    存了只会把库撑大，后台要查工具调用有 audit_logs。
    """

    __tablename__ = "chat_records"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    device_id: Mapped[int | None] = mapped_column(ForeignKey("devices.id", ondelete="SET NULL"), nullable=True)
    machine_name: Mapped[str] = mapped_column(String(200), default="")
    user_name: Mapped[str] = mapped_column(String(200), default="")
    #: 客户端的会话 id，用来把一轮一轮归到一次对话下
    conversation_id: Mapped[str] = mapped_column(String(64), default="", index=True)
    scene: Mapped[str] = mapped_column(String(20), default="")
    model: Mapped[str] = mapped_column(String(100), default="")
    user_content: Mapped[str] = mapped_column(Text, default="")
    assistant_content: Mapped[str] = mapped_column(Text, default="")
    #: 附件只记数量，不存内容——base64 图片动辄几百 KB，存了也没法看
    attachments: Mapped[int] = mapped_column(Integer, default=0)
    prompt_tokens: Mapped[int] = mapped_column(Integer, default=0)
    completion_tokens: Mapped[int] = mapped_column(Integer, default=0)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, index=True)


class AdminUser(Base):
    """
    管理端的账号。

    员工端不需要登录（Windows 账号就是身份边界），但管理端需要：聊天内容进了
    后台之后，「谁看了谁的对话」必须答得上来，一个全组共用的 token 答不了。
    """

    __tablename__ = "admin_users"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    username: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    display_name: Mapped[str] = mapped_column(String(100), default="")
    password_hash: Mapped[str] = mapped_column(String(255), default="")
    #: 首次登录强制改密，否则建号的人一直知道所有人的密码，审计就失去意义
    must_change_password: Mapped[bool] = mapped_column(Boolean, default=True)
    #: 能不能查看聊天正文。默认不能——看内容是额外授予的，不是当管理员就自带的
    can_read_chats: Mapped[bool] = mapped_column(Boolean, default=False)
    disabled: Mapped[bool] = mapped_column(Boolean, default=False)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    last_login: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)


class AdminSession(Base):
    """管理端登录后的会话令牌。"""

    __tablename__ = "admin_sessions"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("admin_users.id", ondelete="CASCADE"))
    token_hash: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))


class AdminAccess(Base):
    """
    管理员查看聊天正文的记录。

    列表页只给元数据不记这里；点开某台机器的某段对话才记。没有这张表，
    「谁看了谁的对话」就查无对证，那样的后台不该拿到聊天内容。
    """

    __tablename__ = "admin_access"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    user_id: Mapped[int | None] = mapped_column(ForeignKey("admin_users.id", ondelete="SET NULL"), nullable=True)
    username: Mapped[str] = mapped_column(String(64), default="")
    action: Mapped[str] = mapped_column(String(40), default="")  # read_chat / export_chat
    target: Mapped[str] = mapped_column(String(200), default="")  # 会话 id 或设备
    detail: Mapped[str] = mapped_column(String(300), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, index=True)


class DevicePolicy(Base):
    """
    某台机器单独的安全设置。

    「后台统一配置 + 可以给某台机器单独设置」——全厂的默认值存在 settings 表里，
    这张表只存某台机器和全厂不一样的那几项，所以改全厂默认值时，没被单独设过的
    机器会自动跟着变。
    """

    __tablename__ = "device_policies"

    device_id: Mapped[int] = mapped_column(
        ForeignKey("devices.id", ondelete="CASCADE"), primary_key=True
    )
    #: 只存和全厂不一样的项
    overrides: Mapped[dict] = mapped_column(JSON, default=dict)
    #: 只存锁状态和全厂不一样的项。给某台机器放开某一项，就写在这里
    locks: Mapped[dict] = mapped_column(JSON, default=dict)
    note: Mapped[str] = mapped_column(String(300), default="")
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, onupdate=utcnow)
