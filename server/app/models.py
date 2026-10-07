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
    #: Windows 的 MachineGuid。员工端和运维代理各自注册，靠它对上是同一台电脑
    machine_guid: Mapped[str] = mapped_column(String(64), default="", index=True)

    # 管理员手工填写的台账信息。客户端心跳不会覆盖这几项（user_name 是 Windows 账号，自动上报）
    #: 使用者实名（和 Windows 账号区分开）
    owner: Mapped[str] = mapped_column(String(200), default="")
    #: 所属部门
    department: Mapped[str] = mapped_column(String(200), default="")
    #: 备注
    note: Mapped[str] = mapped_column(String(500), default="")


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
    #: 能不能给员工电脑下发运维任务（装软件、系统修复、重启……）。同样默认不能
    can_dispatch: Mapped[bool] = mapped_column(Boolean, default=False)
    #: 超级管理员：建号、改权限、改安全策略。上面两项权限都由他授予，所以他必须更难当
    is_owner: Mapped[bool] = mapped_column(Boolean, default=False)
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


class MachineAgent(Base):
    """
    装在员工电脑上的运维代理（Windows 服务，SYSTEM 权限）。

    和员工端分开注册：员工端跟着 Windows 账号走、只在有人登录时运行；代理跟着
    电脑走、开机就在。两边靠 machine_guid 对上是同一台电脑。
    """

    __tablename__ = "machine_agents"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    machine_guid: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    token_hash: Mapped[str] = mapped_column(String(64), unique=True, index=True)
    machine_name: Mapped[str] = mapped_column(String(200), default="")
    os_version: Mapped[str] = mapped_column(String(200), default="")
    agent_version: Mapped[str] = mapped_column(String(50), default="")
    disabled: Mapped[bool] = mapped_column(Boolean, default=False)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    last_seen: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    #: 最近一次「采集电脑信息」的结果：硬件、系统、软件清单……
    inventory: Mapped[dict] = mapped_column(JSON, default=dict)
    inventory_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)


class SoftwarePackage(Base):
    """后台上传的安装包。文件按 sha256 存在磁盘上，代理下载后先校验再安装。"""

    __tablename__ = "software_packages"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    name: Mapped[str] = mapped_column(String(100))
    version: Mapped[str] = mapped_column(String(50), default="")
    filename: Mapped[str] = mapped_column(String(255))
    kind: Mapped[str] = mapped_column(String(10))  # msi / exe
    size: Mapped[int] = mapped_column(Integer, default=0)
    sha256: Mapped[str] = mapped_column(String(64), index=True)
    #: exe 的静默安装参数（上传时定好，下发时不能改）。msi 固定用 /qn /norestart
    silent_args: Mapped[str] = mapped_column(String(300), default="")
    uploaded_by: Mapped[str] = mapped_column(String(64), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class ClientRelease(Base):
    """
    员工端的一个版本。上传的是 publish 出来的整个文件夹打成的 zip。

    发布之后所有电脑会自己发现、自己下载、自己装上——这是这套系统里影响面最大的
    动作，所以发布这件事限超级管理员。上传完还要显式 published=True 才下发，
    传错了可以先不发布。
    """

    __tablename__ = "client_releases"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    version: Mapped[str] = mapped_column(String(50), unique=True, index=True)
    #: 更新日志。员工点「更新日志」看到的就是这段
    notes: Mapped[str] = mapped_column(Text, default="")
    filename: Mapped[str] = mapped_column(String(255), default="")
    size: Mapped[int] = mapped_column(Integer, default=0)
    sha256: Mapped[str] = mapped_column(String(64), index=True)
    #: 没发布的版本客户端看不到。上传和发布分开，传错了还有回头的机会
    published: Mapped[bool] = mapped_column(Boolean, default=False)
    uploaded_by: Mapped[str] = mapped_column(String(64), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class AgentJob(Base):
    """一次下发：同一个任务发给一批电脑。每台电脑的执行情况在 AgentRun 里。"""

    __tablename__ = "agent_jobs"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    kind: Mapped[str] = mapped_column(String(40))
    params: Mapped[dict] = mapped_column(JSON, default=dict)
    title: Mapped[str] = mapped_column(String(200), default="")
    created_by: Mapped[str] = mapped_column(String(64), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, index=True)
    cancelled_by: Mapped[str] = mapped_column(String(64), default="")


class AgentRun(Base):
    """某个任务在某台电脑上的执行。"""

    __tablename__ = "agent_runs"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    job_id: Mapped[int] = mapped_column(ForeignKey("agent_jobs.id", ondelete="CASCADE"), index=True)
    agent_id: Mapped[int] = mapped_column(ForeignKey("machine_agents.id", ondelete="CASCADE"), index=True)
    #: pending → running → succeeded / failed；还有 cancelled（后台取消）、expired（太久没被领走）
    status: Mapped[str] = mapped_column(String(20), default="pending", index=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    started_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    exit_code: Mapped[int | None] = mapped_column(Integer, nullable=True)
    #: 执行过程的摘要（命令输出截断后存这里）
    output: Mapped[str] = mapped_column(Text, default="")
    #: 结构化结果，例如清理释放了多少空间、是否需要重启
    result: Mapped[dict] = mapped_column(JSON, default=dict)


class InstructionTemplate(Base):
    """常用指令模板：把一段自然语言指令存起来，下发时直接选。"""

    __tablename__ = "instruction_templates"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    name: Mapped[str] = mapped_column(String(100))
    prompt: Mapped[str] = mapped_column(Text, default="")
    created_by: Mapped[str] = mapped_column(String(64), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)


class RemoteInstruction(Base):
    """
    一次自然语言指令下发：把一段意图发给一批员工电脑，由电脑里的 AI agent 自行理解执行。

    和 AgentJob 分开：AgentJob 走运维代理（SYSTEM 服务）按固定目录执行；这里走员工端里的
    对话式 agent，内容是自由文本，执行结果是一段回答，不是退出码。
    """

    __tablename__ = "remote_instructions"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    prompt: Mapped[str] = mapped_column(Text)
    title: Mapped[str] = mapped_column(String(200), default="")
    created_by: Mapped[str] = mapped_column(String(64), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow, index=True)
    cancelled_by: Mapped[str] = mapped_column(String(64), default="")


class InstructionRun(Base):
    """某条指令在某台员工电脑上的执行。"""

    __tablename__ = "instruction_runs"

    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    instruction_id: Mapped[int] = mapped_column(ForeignKey("remote_instructions.id", ondelete="CASCADE"), index=True)
    device_id: Mapped[int] = mapped_column(ForeignKey("devices.id", ondelete="CASCADE"), index=True)
    #: pending → running → succeeded / failed；还有 cancelled（后台取消）、expired（太久没被领走）
    status: Mapped[str] = mapped_column(String(20), default="pending", index=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=utcnow)
    started_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True), nullable=True)
    #: agent 跑完后给出的最终回答（做了什么、结论）
    answer: Mapped[str] = mapped_column(Text, default="")
    #: 失败原因
    error: Mapped[str] = mapped_column(Text, default="")
    #: 对应员工端本地的会话 ID，便于在那台电脑上回溯完整对话
    conversation_id: Mapped[str] = mapped_column(String(64), default="")
