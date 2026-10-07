from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, HttpUrl

Scene = Literal["chat", "agent", "translate", "title", "vision", "asr"]
# asr 是语音转文字，和对话模型不通用，所以它没有回退场景
SCENES: tuple[str, ...] = ("chat", "agent", "translate", "title", "vision", "asr")


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
    # model_ 是 pydantic 的保留前缀；这几个字段名是对的，关掉这条检查，
    # 免得每次启动刷三条 UserWarning 把真正要紧的告警挤下去
    model_config = ConfigDict(protected_namespaces=())

    model_id: int | None
    fallback_model_id: int | None = None


class RouteOut(BaseModel):
    model_config = ConfigDict(protected_namespaces=())

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


class MachineInfoIn(BaseModel):
    """客户端定期上报的本机信息。全部可选——采不到的字段不该让整次上报失败。"""

    machine_name: str = ""
    user_name: str = ""
    domain: str = ""
    os_version: str = ""
    client_version: str = ""
    ui_language: str = ""
    ip_addresses: list[str] = Field(default_factory=list, max_length=16)
    mac_address: str = ""
    machine_guid: str = ""


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
    domain: str = ""
    ip_addresses: str = ""
    observed_ip: str = ""
    mac_address: str = ""
    machine_guid: str = ""
    owner: str = ""
    department: str = ""
    note: str = ""


class DevicePatch(BaseModel):
    disabled: bool | None = None
    owner: str | None = None
    department: str | None = None
    note: str | None = None


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
    scene: str = ""
    decision: Literal["blocked", "approved", "remembered", "rejected", "auto"]
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
    scene: str = ""
    risk: str
    decision: str
    status: str
    summary: str
    occurred_at: datetime
    created_at: datetime


# ---------- 客户端配置 ----------
class SceneInfo(BaseModel):
    model_config = ConfigDict(protected_namespaces=())

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
    #: 安全中心每一项的最终值和锁状态（全厂默认叠加这台机器的单独设置）
    security: dict = Field(default_factory=dict)
    #: 配置版本号，客户端用它做长轮询，一变就立刻拉新配置
    revision: int = 0
    #: 管理员在后台填的台账：使用者实名、部门。客户端界面上显示
    owner: str = ""
    department: str = ""


class DevicePolicyIn(BaseModel):
    """给某台机器单独放开或锁死某几项。只传要改的，没传的跟全厂走。"""

    overrides: dict = Field(default_factory=dict)
    locks: dict = Field(default_factory=dict)
    note: str = ""


class SecurityDefaultsIn(BaseModel):
    """全厂默认。"""

    values: dict = Field(default_factory=dict)
    locks: dict = Field(default_factory=dict)


class ClientModelOut(BaseModel):
    id: int
    name: str
    model: str
    provider: str
    supports_tools: bool
    supports_vision: bool


class QuotaIn(BaseModel):
    daily_tokens: int | None = Field(default=None, ge=0)
    contact_name: str | None = None
    contact_email: str | None = None
    contact_phone: str | None = None


class QuotaOut(BaseModel):
    daily_tokens: int = 0
    contact_name: str = ""
    contact_email: str = ""
    contact_phone: str = ""


class SceneUsage(BaseModel):
    scene: str
    prompt: int = 0
    completion: int = 0
    total: int = 0
    requests: int = 0


class DayUsage(BaseModel):
    day: str
    tokens: int = 0


class UsageOut(BaseModel):
    day: str
    today_tokens: int = 0
    daily_limit: int = 0
    remaining: int = 0
    exceeded: bool = False
    by_scene: list[SceneUsage] = []
    by_day: list[DayUsage] = []
    contact_name: str = ""
    contact_email: str = ""
    contact_phone: str = ""


class DeviceUsageOut(BaseModel):
    device_id: int | None = None
    machine_name: str = ""
    user_name: str = ""
    tokens: int = 0
    requests: int = 0
    today_tokens: int = 0


class SkillOut(BaseModel):
    name: str
    description: str = ""
    version: str = ""
    author: str = ""
    origin: str = ""
    size: int = 0
    file_count: int = 0
    required: bool = False
    enabled: bool = True
    updated_at: datetime

    model_config = ConfigDict(from_attributes=True)


class SkillImportIn(BaseModel):
    """从链接导入：GitHub 仓库/子目录页面链接，或任意技能包 zip 的下载链接。"""

    url: str
    required: bool = False


class SkillPatch(BaseModel):
    required: bool | None = None
    enabled: bool | None = None


class SkillImportResult(BaseModel):
    imported: list[str] = []
    skipped: list[str] = []


class SyncResult(BaseModel):
    total: int
    added: list[str]
    skipped: int


# ---------- 管理端账号与聊天记录 ----------
class AdminUserIn(BaseModel):
    username: str = Field(min_length=3, max_length=64)
    password: str = Field(min_length=8, max_length=200)
    display_name: str = ""
    #: 看聊天正文是额外授予的权限，默认不给
    can_read_chats: bool = False
    can_dispatch: bool = False
    #: 超级管理员：能建号改权限、能改安全策略。只有超级管理员能授予
    is_owner: bool = False


class AdminUserOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    username: str
    display_name: str
    can_read_chats: bool
    can_dispatch: bool = False
    is_owner: bool = False
    must_change_password: bool
    disabled: bool
    created_at: datetime
    last_login: datetime | None


class AdminUserPatch(BaseModel):
    display_name: str | None = None
    can_read_chats: bool | None = None
    can_dispatch: bool | None = None
    is_owner: bool | None = None
    disabled: bool | None = None
    #: 重置成新的初始密码，对方下次登录必须再改
    password: str | None = Field(default=None, min_length=8, max_length=200)


class AdminLoginIn(BaseModel):
    username: str
    password: str


class AdminLoginOut(BaseModel):
    token: str
    display_name: str
    can_read_chats: bool
    must_change_password: bool


class ChangePasswordIn(BaseModel):
    old_password: str
    new_password: str = Field(min_length=8, max_length=200)


class ChatConversationOut(BaseModel):
    """会话列表项：只有元数据，没有正文。"""

    model_config = ConfigDict(from_attributes=True)

    conversation_id: str
    device_id: int | None = None
    machine_name: str
    user_name: str
    scene: str
    #: 用户发言次数（任务模式里调工具产生的中间回答不算）
    turns: int
    tokens: int = 0
    model: str = ""
    started_at: datetime
    last_at: datetime


class ChatRecordOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    conversation_id: str
    machine_name: str
    user_name: str
    scene: str
    model: str
    user_content: str
    assistant_content: str
    attachments: int
    prompt_tokens: int
    completion_tokens: int
    created_at: datetime


class ReleaseOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    version: str
    notes: str
    filename: str
    size: int
    sha256: str
    published: bool
    uploaded_by: str
    created_at: datetime


class ReleasePatch(BaseModel):
    #: 置 True 之后所有电脑会自己发现并装上，所以上传和发布分两步
    published: bool | None = None
    notes: str | None = Field(default=None, max_length=20000)


class ClientUpdateOut(BaseModel):
    """有没有新版本。available=False 时其余字段都不用看。"""

    available: bool
    version: str = ""
    notes: str = ""
    size: int = 0
    #: 客户端下完自己算一遍，对不上就不装
    sha256: str = ""


# ---------- MCP 连接器 ----------
class McpField(BaseModel):
    """员工连接时要填的一项，例如 API Key。key 对应配置里的 ${KEY}。"""

    key: str = Field(min_length=1, max_length=41)
    label: str = ""
    secret: bool = True
    required: bool = True
    placeholder: str = ""
    help: str = ""


class McpVendorIn(BaseModel):
    id: str = Field(min_length=2, max_length=40)
    name: str = Field(min_length=1, max_length=100)
    description: str = Field(default="", max_length=300)
    detail: str = ""
    icon: str = ""
    publisher: str = Field(default="", max_length=100)
    category: str = Field(default="", max_length=50)
    homepage: str = Field(default="", max_length=500)
    transport: Literal["http", "sse", "stdio"] = "http"
    url: str = Field(default="", max_length=1000)
    command: str = Field(default="", max_length=500)
    args: list[str] = []
    env: dict[str, str] = {}
    headers: dict[str, str] = {}
    auth: Literal["none", "fields", "oauth"] = "none"
    fields: list[McpField] = []
    #: 管理员统一填的值。更新时不传 = 不改；某一项传空字符串 = 清掉这一项
    preset: dict[str, str] | None = None
    oauth: dict[str, str] = {}
    examples: list[str] = []
    timeout_ms: int = Field(default=60000, ge=5000, le=600000)
    sort_order: int = 0
    enabled: bool = True


class McpVendorPatch(BaseModel):
    enabled: bool | None = None
    sort_order: int | None = None


class McpVendorOut(BaseModel):
    """后台看到的：预填的值只露掩码。"""

    id: str
    name: str
    description: str = ""
    detail: str = ""
    icon: str = ""
    publisher: str = ""
    category: str = ""
    homepage: str = ""
    transport: str = "http"
    url: str = ""
    command: str = ""
    args: list[str] = []
    env: dict[str, str] = {}
    headers: dict[str, str] = {}
    auth: str = "none"
    fields: list[McpField] = []
    preset_masked: dict[str, str] = {}
    oauth: dict[str, str] = {}
    examples: list[str] = []
    timeout_ms: int = 60000
    sort_order: int = 0
    enabled: bool = True
    updated_at: datetime


class McpClientVendorOut(BaseModel):
    """员工端看到的：预填的值是明文——员工电脑要拿它去连接，本来就得知道。"""

    id: str
    name: str
    description: str = ""
    detail: str = ""
    icon: str = ""
    publisher: str = ""
    category: str = ""
    homepage: str = ""
    transport: str = "http"
    url: str = ""
    command: str = ""
    args: list[str] = []
    env: dict[str, str] = {}
    headers: dict[str, str] = {}
    auth: str = "none"
    fields: list[McpField] = []
    preset: dict[str, str] = {}
    #: 员工还得自己填的项；为空表示点「连接」就能连
    needs_input: list[str] = []
    oauth: dict[str, str] = {}
    examples: list[str] = []
    timeout_ms: int = 60000
    updated_at: datetime


class McpImportIn(BaseModel):
    text: str = Field(min_length=1, max_length=50000)


class McpDraftOut(BaseModel):
    id: str
    name: str
    transport: str
    url: str = ""
    command: str = ""
    args: list[str] = []
    env: dict[str, str] = {}
    headers: dict[str, str] = {}
    auth: str = "none"
    fields: list[McpField] = []
    #: 从配置里挪出来的密钥，前端回填到「管理员预填」里，保存时一起加密
    preset: dict[str, str] = {}
    exists: bool = False


class McpTestIn(BaseModel):
    #: 临时填的值（例如管理员自己的 API Key），只用于这次测试，不保存
    values: dict[str, str] = {}


class McpTestOut(BaseModel):
    ok: bool
    server_name: str = ""
    server_version: str = ""
    tools: list[dict] = []
    error: str = ""
