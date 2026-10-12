from datetime import datetime
from typing import Annotated, Literal

from pydantic import AfterValidator, BaseModel, ConfigDict, Field, HttpUrl

Scene = Literal["chat", "agent", "translate", "title", "vision", "asr"]
# asr 是语音转文字，和对话模型不通用，所以它没有回退场景
# embedding：把文字转成向量，员工端用来按意思检索记忆；没配置时员工端只按字面匹配
SCENES: tuple[str, ...] = ("chat", "agent", "translate", "title", "vision", "asr", "embedding")


def Clip(n: int):  # noqa: N802  当类型用
    """员工端上报的字符串：超过数据库列宽就截断，不拒收。
    PostgreSQL 对超长值直接报错（SQLite 不管），拒收的话老版本员工端会一直重试同一批。"""
    return Annotated[str, AfterValidator(lambda v: v[:n])]


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
    #: 老办法：手填的注册密钥。和 ticket 二选一
    enrollment_key: str = ""
    #: 安装包里带的安装凭证
    ticket: str = ""
    #: domain / local：客户端怎么确认的员工身份（见 EnrollmentTicket）
    login_method: str = Field(default="", max_length=20)
    #: 域名（域账号登录时）
    domain: str = Field(default="", max_length=200)
    #: 同意的协议版本，原样回传 GET /legal 给的 versions
    agreed_legal: str = Field(default="", max_length=200)
    machine_name: str = Field(min_length=1, max_length=200)
    user_name: Clip(200) = ""
    os_version: Clip(200) = ""
    client_version: Clip(50) = ""
    ui_language: Clip(10) = "zh-CN"
    #: 系统标识（HKLM MachineGuid）。同一台电脑上同一个员工重新登录时，沿用原来那条设备记录
    machine_guid: str = Field(default="", max_length=64)


class MachineInfoIn(BaseModel):
    """客户端定期上报的本机信息。全部可选——采不到的字段不该让整次上报失败。"""

    machine_name: Clip(200) = ""
    user_name: Clip(200) = ""
    domain: Clip(200) = ""
    os_version: Clip(200) = ""
    client_version: Clip(50) = ""
    ui_language: Clip(10) = ""
    ip_addresses: list[str] = Field(default_factory=list, max_length=16)
    mac_address: Clip(64) = ""
    machine_guid: Clip(64) = ""


class DeviceRegisterOut(BaseModel):
    device_id: int
    token: str


class LegalDocOut(BaseModel):
    kind: str
    title: str
    content: str
    version: str
    updated_at: str | None = None
    updated_by: str = ""
    customized: bool = False


class LegalIndexOut(BaseModel):
    #: 当前版本，登录时带回 agreed_legal
    versions: str
    docs: list[LegalDocOut]


class LegalIn(BaseModel):
    content: str = Field(min_length=20, max_length=100_000)


class TicketIn(BaseModel):
    #: 员工电脑访问服务端用的地址，写进安装包
    server_url: str = Field(min_length=8, max_length=500)
    label: str = Field(default="", max_length=200)
    #: exe：安装程序（IT 双击一次，员工端和运维代理一起装好）；zip：解压即用（只有员工端）
    format: Literal["zip", "exe"] = "zip"


class TicketOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    label: str
    server_url: str
    created_by: str
    created_at: datetime
    revoked: bool
    uses: int
    last_used_at: datetime | None = None


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
    #: domain / local / key：这台电脑是怎么登录注册的
    login_method: str = ""
    legal_agreed: str = ""
    #: 员工在客户端退出了登录（这条设备记录不会再上线）
    signed_out_at: datetime | None = None


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
    conversation_id: Clip(64) = ""
    tool_name: Clip(100)
    arguments: str = ""
    risk: Literal["blocked", "confirm", "auto"]
    scene: Clip(20) = ""
    decision: Literal["blocked", "approved", "remembered", "rejected", "auto"]
    status: Clip(20) = ""
    summary: str = ""
    #: 专家执行时的子任务 ID；主助手自己的调用为空
    subtask_id: Clip(40) = ""
    #: main 或 expert:<专家 ID>
    actor: Clip(60) = ""
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
    subtask_id: str = ""
    actor: str = ""
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
    owner: str = ""
    department: str = ""
    disabled: bool = False
    tokens: int = 0
    requests: int = 0
    today_tokens: int = 0
    #: 单独设置的每日上限；None 表示跟全局走
    daily_tokens: int | None = None
    #: 实际生效的每日上限，0 表示不限
    daily_limit: int = 0


class DeviceQuotaIn(BaseModel):
    #: None 恢复跟全局走；0 不限制；大于 0 为这台电脑每天的上限
    daily_tokens: int | None = Field(default=None, ge=0, le=1_000_000_000)


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


class SkillFileIn(BaseModel):
    path: str = Field(min_length=1, max_length=200)
    content: str = ""


class SkillSourceIn(BaseModel):
    """后台编写的技能：SKILL.md 和其他文本文件。不能编辑的文件（图片等）保存时原样保留，除非列在 remove 里。"""

    skill_md: str = Field(min_length=1, max_length=256 * 1024)
    files: list[SkillFileIn] = Field(default_factory=list, max_length=50)
    remove: list[str] = Field(default_factory=list)
    required: bool | None = None


class SkillCreateIn(SkillSourceIn):
    name: str = Field(min_length=2, max_length=64)
    #: 后台手写 / AI 生成，记在来源里
    origin: str = Field(default="后台编写", max_length=100)


class SkillSourceFile(BaseModel):
    path: str
    content: str | None = None
    binary: bool = False
    size: int = 0


class SkillSourceOut(BaseModel):
    name: str
    skill_md: str
    files: list[SkillSourceFile] = []


class SkillDraftIn(BaseModel):
    text: str = Field(min_length=2, max_length=4000)
    #: 在已有技能上按描述修改
    name: str | None = None


class SkillDraftOut(BaseModel):
    name: str
    skill_md: str
    files: list[SkillFileIn] = []
    notes: list[str] = []


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
    #: 一段配置、一行命令，或者一个链接 / 注册表里的服务名
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
    #: 从页面或注册表顺带拿到的展示信息，前端回填到表单
    description: str = ""
    homepage: str = ""
    icon: str = ""
    #: config / page / endpoint / registry / server.json
    source: str = ""
    #: 给管理员的提示，例如「对方要求登录，已设为浏览器授权登录」
    notes: list[str] = []


class McpTestIn(BaseModel):
    #: 临时填的值（例如管理员自己的 API Key），只用于这次测试，不保存
    values: dict[str, str] = {}


class McpTestOut(BaseModel):
    ok: bool
    server_name: str = ""
    server_version: str = ""
    tools: list[dict] = []
    error: str = ""


# ---------- 专家 ----------
EXPERT_ID_PATTERN = r"^[a-z][a-z0-9_-]{1,39}$"


class ExpertFields(BaseModel):
    """专家的可编辑内容（草稿和发布的快照都是这一套）。"""

    name: str = Field(min_length=1, max_length=100)
    avatar: str = Field(default="", max_length=400_000)
    tagline: str = Field(default="", max_length=200)
    category: str = Field(default="", max_length=50)
    tags: list[str] = []
    author: str = Field(default="", max_length=100)
    description: str = Field(default="", max_length=4000)
    instructions: str = Field(default="", max_length=20000)
    knowledge: str = Field(default="", max_length=20000)
    examples: list[str] = []
    preferred_skills: list[str] = []
    mcp_servers: list[str] = []
    disallowed_tools: list[str] = []
    read_only: bool = False
    model_id: int | None = None
    allow_direct: bool = True
    allow_delegate: bool = True
    sort_order: int = 0


class ExpertIn(ExpertFields):
    id: str = Field(pattern=EXPERT_ID_PATTERN)


class ExpertOut(ExpertFields):
    id: str
    status: str
    version: int
    uses: int = 0
    users: int = 0
    #: 草稿和已发布的不一样（改了还没发布）
    dirty: bool = False
    published_at: datetime | None = None
    published_by: str = ""
    updated_by: str = ""
    updated_at: datetime | None = None


class ExpertPublishIn(BaseModel):
    note: str = Field(default="", max_length=300)


class ExpertVersionOut(BaseModel):
    version: int
    note: str = ""
    published_by: str = ""
    published_at: datetime
    snapshot: dict


class ExpertDraftIn(BaseModel):
    """一句话描述想要什么专家，由 AI 起草。"""

    text: str = Field(min_length=2, max_length=2000)


class ExpertDraftOut(ExpertFields):
    id: str = ""
    #: 给管理员的提示，例如「没有找到和 MES 相关的技能」
    notes: list[str] = []


class ExpertOption(BaseModel):
    value: str
    label: str
    detail: str = ""


class ExpertOptionsOut(BaseModel):
    """编辑专家时可选的工具、技能、连接器、模型。"""

    tools: list[ExpertOption]
    skills: list[ExpertOption]
    mcp: list[ExpertOption]
    models: list[ExpertOption]


class ExpertClientOut(ExpertFields):
    """员工端拿到的：已发布的快照，加上使用量。"""

    id: str
    version: int
    uses: int = 0
    users: int = 0
    published_at: datetime | None = None


# ---------- 专家派遣 ----------
class ExpertDelegationIn(BaseModel):
    enabled: bool


class ExpertDelegationOut(BaseModel):
    enabled: bool


class SubtaskCreateIn(BaseModel):
    run_id: str = Field(min_length=1, max_length=64)
    conversation_id: Clip(64) = ""
    expert_id: str = Field(min_length=1, max_length=40)
    expert_version: int = 0
    #: 任务说明。超长截断，不拒收
    task: Clip(20000) = ""
    #: 服务端会夹到 [1000, 400000]
    token_cap: int = 400_000
    #: 服务端会夹到 [60, 900]
    timeout_seconds: int = 900


class SubtaskExpertOut(BaseModel):
    id: str
    version: int
    published_version: int
    name: str


class SubtaskCreatedOut(BaseModel):
    id: str
    seq: int
    status: str
    expert: SubtaskExpertOut
    token_cap: int
    deadline_at: datetime


class SubtaskFinishIn(BaseModel):
    status: Literal["completed", "partial", "needs_input", "failed", "cancelled", "timed_out"]
    summary: Clip(2000) = ""
    #: 交卷全文，序列化后超过 64KB 截断并标记
    result: dict | list | str | None = None
    error_code: Clip(40) = ""
    error: Clip(2000) = ""
    tool_calls: int = Field(default=0, ge=0)
    local_tokens: int = Field(default=0, ge=0)


class SubtaskCancelIn(BaseModel):
    reason: Literal["user", "disabled", "parent", "timeout"] = "user"


class SubtaskTokens(BaseModel):
    prompt: int = 0
    completion: int = 0
    estimated_calls: int = 0


class SubtaskStateOut(BaseModel):
    """登记以外的三个接口统一返回这个。"""

    id: str
    status: str
    final: bool
    #: 这次请求之前就已经是终态，请求没有改动它
    already_final: bool = False
    tokens: SubtaskTokens
    error_code: str = ""


class SubtaskAdminOut(BaseModel):
    """后台「派遣记录」的一行。task / summary / error 属于聊天内容，没有查看权限时为空。"""

    id: str
    device_id: int | None
    machine_name: str
    user_name: str
    #: 后台填的使用者实名
    owner: str = ""
    conversation_id: str
    run_id: str
    seq: int
    expert_id: str
    expert_name: str
    expert_version: int
    published_version: int
    task: str
    status: str
    token_cap: int
    prompt_tokens: int
    completion_tokens: int
    estimated_calls: int
    tool_calls: int
    local_tokens: int
    summary: str
    error_code: str
    error: str
    cancel_reason: str
    result_truncated: bool
    created_at: datetime
    finished_at: datetime | None
    deadline_at: datetime
    #: 结束时间 - 开始时间；还在跑的为 None
    duration_ms: int | None
