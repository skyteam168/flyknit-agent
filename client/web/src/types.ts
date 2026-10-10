// 与 client/src/Flyknit.Client/Bridge/WebBridge.cs 中的 DTO 保持一致

export type Mode = 'chat' | 'translate' | 'agent'
export type UiLanguage = 'zh-CN' | 'vi-VN' | 'en-US'
export type Theme = 'system' | 'light' | 'dark'
export type Risk = 'auto' | 'confirm' | 'blocked'
export type Permission = 'readonly' | 'workspace' | 'full'

export interface Conversation {
  id: string
  title: string
  titleSource: 'auto' | 'manual'
  mode: Mode
  pinned: boolean
  translateFrom: string
  translateTo: string
  createdAt: string
  updatedAt: string
  deletedAt: string | null
  messageCount: number
  modelId: number | null
  workspace: string | null
  permission: Permission
  /** 较早对话已压缩为摘要，摘要覆盖到这条消息（含） */
  summaryUpto?: string | null
}

export type MemoryKind = 'preference' | 'fact' | 'success' | 'lesson'

export interface MemoryItem {
  id: string
  kind: MemoryKind
  text: string
  date: string | null
  /** 最近一次被再次确认的日期 */
  lastSeen?: string | null
  /** 被确认的次数 */
  proofCount?: number
  uses?: number
  feedback?: number
  source?: string
  /** 被取代的旧说法（从新到旧） */
  history?: string[]
  /** 置顶：用户要求一直遵守，每次都带上 */
  pinned?: boolean
  /** user_said / user_confirmed / inferred / ''（早期记下、来源不明） */
  origin?: string
  /** 依据：用户原话 */
  evidence?: string
  /** 偏好槽位：同一类只留最新的一条 */
  slot?: string | null
  slotLabel?: string | null
  /** 有效期到哪天（容易变的信息才有） */
  validUntil?: string | null
  /** 过了有效期，等用户确认 */
  expired?: boolean
}

export interface MemoryAddResult {
  outcome: 'added' | 'reinforced' | 'updated' | 'rejected'
  reason: string | null
}

export interface MemoryConsolidateResult {
  groups: number
  merged: number
  errors: string[]
}

export interface EpisodeInfo {
  id: string
  conversationId: string
  title: string
  task: string
  summary: string
  outcome: 'success' | 'partial' | 'failure'
  procedure: string
  lessons: string[]
  feedback: number
  uses: number
  createdAt: string
}

export type LearnedSkillStatus = 'candidate' | 'active' | 'retired'

export interface LearnedSkillInfo {
  name: string
  description: string
  path: string
  status: LearnedSkillStatus
  version: number
  uses: number
  successes: number
  failures: number
  /** 最近一次被用上的时间（ISO），从没用过为 null */
  lastUsed?: string | null
}

/** 记忆指标：注入量、利用率、新鲜度（最近 days 天） */
export interface MemoryMetrics {
  days: number
  active: number
  pinned: number
  inferred: number
  answers: number
  answersWithMemory: number
  avgItems: number
  avgTokens: number
  maxTokens: number
  usedRecently: number
  usedShare: number
  neverUsed: number
  liked: number
  disliked: number
  fresh30: number
  fresh90: number
  stale: number
  medianAgeDays: number
  episodes: number
  episodesReused: number
  episodesRecent: number
  skillsActive: number
  skillsCandidate: number
  skillsRetired: number
  /** 过了有效期、等确认的条数 */
  expired: number
  /** 语义检索最近一次是否可用（服务端配了向量模型） */
  semantic: boolean
  /** 办事任务的效果（最近 days 天） */
  runs: RunStats | null
}

export interface RunStats {
  total: number
  completed: number
  /** 步数用完、连续失败、原地打转被暂停的 */
  paused: number
  cancelled: number
  errors: number
  completionRate: number
  avgSteps: number
  disliked: number
  liked: number
  /** 过程中产出文件检查发现过问题的任务数 / 交付时仍有问题的 */
  outputProblems: number
  outputProblemsAtEnd: number
  planNudges: number
}

export interface MemoryOverview {
  items: MemoryItem[]
  episodes: EpisodeInfo[]
  skills: LearnedSkillInfo[]
  learning: boolean
}

export interface WorkspaceInfo {
  path: string
  name: string
  exists: boolean
  isDefault: boolean
}

export interface ApprovalInfo {
  id: string
  tool: string
  shell: string
  /** 命令前缀，例如 "npm run" */
  prefix: string
  /** 适用的工作区，"*" 表示所有工作区 */
  scope: string
  display: string
  approvedAt: string
  lastUsedAt: string
  uses: number
}

/** 任务产出的一个文件 */
export interface OutputFile {
  path: string
  name: string
  extension: string
  size: number
  modifiedAt: string
  /** 能不能在右侧分屏里预览 */
  previewable: boolean
  exists: boolean
}

export type PreviewKind = 'text' | 'markdown' | 'table' | 'sections' | 'image' | 'pdf' | 'diagram' | 'listing' | 'html' | 'none'

export interface PreviewSection {
  title: string
  text?: string | null
  rows?: string[][] | null
}

export interface PreviewDoc {
  path: string
  name: string
  kind: PreviewKind
  text?: string | null
  language?: string | null
  dataUrl?: string | null
  notice?: string | null
  error?: string | null
  sections: PreviewSection[]
}

export interface TraceStep {
  index: number
  /** model / tool / compact */
  kind: string
  name: string
  summary: string
  /** ok / error / blocked / stopped / skipped */
  status: string
  startedAt: string
  durationMs: number
  promptTokens: number
  completionTokens: number
}

/** 一次任务的执行链路 */
export interface TraceInfo {
  id: string
  startedAt: string
  durationMs: number
  stopReason: string
  steps: number
  modelCalls: number
  toolCalls: number
  errors: number
  promptTokens: number
  completionTokens: number
  slowest: string | null
  items: TraceStep[]
}

/** 一条快捷键命令（表来自宿主） */
export interface ShortcutInfo {
  id: string
  /** task / chat / view / panel / window */
  group: string
  binding: string
  default: string
  global: boolean
  fixed: boolean
  customized: boolean
}

export interface StorageInfo {
  dataDir: string
  bytes: number
  files: number
  diskTotal: number
  diskUsed: number
  diskFree: number
  workspace: string
}

export interface ModelInfo {
  id: number
  name: string
  model: string
  provider: string
  supportsTools: boolean
  supportsVision: boolean
}

export type ScheduleKind = 'manual' | 'once' | 'hourly' | 'daily' | 'weekdays' | 'weekly' | 'monthly'

/** 定时任务。到点后 Agent 新建一个会话自动执行。 */
export interface ScheduledTask {
  id: string
  name: string
  instructions: string
  kind: ScheduleKind
  hour: number
  minute: number
  /** 周日 = 0 */
  weekday: number
  dayOfMonth: number
  at: string | null
  enabled: boolean
  workspace: string | null
  permission: Permission
  modelId: number | null
  /** 错过的任务开机后补跑 */
  catchUp: boolean
  nextRunAt: string | null
  lastRunAt: string | null
  /** ok / failed / stopped / running */
  lastStatus: string
  lastSummary: string
  lastConversationId: string | null
  runCount: number
}

export interface SceneUsage {
  scene: string
  prompt: number
  completion: number
  total: number
  requests: number
}

export interface UsageStats {
  day: string
  todayTokens: number
  /** 0 表示管理员没有设上限 */
  dailyLimit: number
  remaining: number
  exceeded: boolean
  byScene: SceneUsage[]
  byDay: { day: string; tokens: number }[]
  contactName: string
  contactEmail: string
  contactPhone: string
}

export type SecurityDecision = 'blocked' | 'approved' | 'remembered' | 'rejected' | 'allowed' | 'changed'

export interface SecurityEvent {
  id: number
  conversationId: string
  title: string
  /** agent / chat / translate */
  scene: string
  tool: string
  detail: string
  decision: SecurityDecision
  reason: string
  createdAt: string
}

/** 网络白名单的当前内容，由服务端下发 */
export interface NetworkAllowlist {
  enabled: boolean
  domains: string[]
  allowPrivate: boolean
}

export interface ExportResult {
  ok: boolean
  cancelled: boolean
  message: string
  path: string
  count: number
}

export type SkillSourceKind = 'personal' | 'organization' | 'learned'

export interface SkillInfo {
  name: string
  description: string
  version: string
  author: string
  license: string
  homepage: string
  /** 安装来源：本地文件名、公司技能库、下载链接 */
  origin: string
  source: SkillSourceKind
  organization: boolean
  learned: boolean
  /** 企业要求安装，不能停用或卸载 */
  required: boolean
  enabled: boolean
  directory: string
  files: string[]
  scripts: string[]
  bytes: number
}

/** 公司技能库里的一个技能 */
export interface LibrarySkill {
  name: string
  description: string
  version: string
  author: string
  origin: string
  size: number
  required: boolean
  installed: boolean
  updatable: boolean
}

// ---------- MCP 连接器 ----------
/** 连接器状态 */
export type McpStatus = 'disconnected' | 'connecting' | 'authorizing' | 'connected' | 'needsauth' | 'failed'

export interface McpField {
  key: string
  label: string
  secret: boolean
  required: boolean
  placeholder: string
  help: string
  /** 管理员已经统一填好了，员工不用填 */
  preset: boolean
  /** 员工以前填过（密钥不回显） */
  hasValue: boolean
  /** 不是密钥的项回显上次填的值 */
  value: string
}

export interface McpTool {
  name: string
  title: string
  description: string
  readOnly: boolean
}

/** 管理员上架的一个 MCP 连接器，以及它在这台电脑上的状态 */
export interface McpVendor {
  id: string
  name: string
  description: string
  detail: string
  icon: string
  publisher: string
  category: string
  homepage: string
  transport: string
  auth: 'none' | 'fields' | 'oauth' | string
  examples: string[]
  fields: McpField[]
  needsInput: string[]
  status: McpStatus
  error: string
  enabled: boolean
  serverName: string
  transportUsed: string
  connectedAt: string | null
  tools: McpTool[]
}

/** 安装前的检查结果 */
export interface SkillInspection {
  ok: boolean
  error: string | null
  name: string
  description: string
  version: string
  files: string[]
  scripts: string[]
  bytes: number
  warnings: string[]
  replaces: boolean
}

export interface SkillInstallOutcome {
  ok: boolean
  installed: string[]
  messages: string[]
  warnings: string[]
  skills: SkillInfo[]
}

export interface SkillActionResult {
  ok: boolean
  message: string
  skills: SkillInfo[]
}

export type LibraryKind = 'image' | 'document' | 'sheet' | 'slides' | 'pdf' | 'audio' | 'video' | 'archive' | 'code' | 'note' | 'other'
export type LibraryTab = 'recent' | 'favorites' | 'folders' | 'images' | 'all' | 'trash'

/** 资料库里的一个文件 */
export interface LibraryItem {
  id: string
  name: string
  /** 文件在磁盘上的位置 */
  path: string
  kind: LibraryKind
  mime: string
  size: number
  /** upload / paste / output / library / note */
  source: string
  conversationId: string | null
  folderId: string | null
  favorite: boolean
  managed: boolean
  hidden: boolean
  exists: boolean
  createdAt: string
  updatedAt: string
  deletedAt: string | null
  /** 原文件地址（宿主拦截 files.flyknit.local 提供） */
  url: string
  /** 图片缩略图地址 */
  thumbUrl: string | null
}

export interface LibraryFolder {
  id: string
  name: string
  count: number
  createdAt: string
  updatedAt: string
}

export interface AttachmentRef {
  fileName: string
  localPath: string
  mime: string
  size: number
  /** 图片缩略图（data URL），仅用于界面显示 */
  preview?: string
}

export interface ToolCallRef {
  id: string
  name: string
  arguments: string
}

export interface UiMessage {
  id: string
  role: 'user' | 'assistant' | 'tool'
  content: string
  reasoning?: string | null
  attachments?: AttachmentRef[]
  toolCalls?: ToolCallRef[]
  toolCallId?: string | null
  toolName?: string | null
  createdAt: string
  feedback?: number | null
  /** 助手消息：使用的模型与本次调用的 token 用量 */
  modelName?: string | null
  promptTokens?: number | null
  completionTokens?: number | null
  /** 这条回答产出的文件 */
  outputs?: OutputFile[] | null
  /** 这一轮的执行链路（JSON 字符串） */
  trace?: string | null
}

export interface PlanItem {
  step: string
  status: 'pending' | 'in_progress' | 'completed'
}

export interface AppInfo {
  version: string
  uiLanguage: UiLanguage
  theme: Theme
  userName: string
  machineName: string
  department?: string
  owner?: string
  connected: boolean
  serverMessage: string
  modelName: string
  defaultModelId: number | null
  defaultWorkspace: string
  defaultPermission: Permission
  workspaces: WorkspaceInfo[]
  learning: boolean
  /** 办事模式每轮最多步数 */
  maxSteps: number
  notifications: boolean
  notificationSound: string
  /** IT 锁住了完成通知 / 提示音，本机改不了 */
  notificationsLocked?: boolean
  soundLocked?: boolean
  fontScale: number
  autoStart: boolean
  /** 锁屏运行：off / tasks / awake / screen */
  keepAwake: KeepAwakeMode
  /** IT 允不允许阻止睡眠、允不允许屏幕常亮 */
  keepAwakeAllowed: boolean
  keepScreenAllowed: boolean
  proxyMode: string
  proxyUrl: string
  proxyUser: string
  dataDir: string
  shortcuts: ShortcutInfo[]
  maximized: boolean
  /** 这台机器有没有麦克风。没有就不显示语音按钮 */
  micAvailable: boolean
  /** 工作区隔离是否生效。开着时「完全权限」不可选 */
  sandboxed: boolean
}

/** 自动更新的状态 */
export interface UpdateInfo {
  /** none / downloading / ready / needsit */
  stage: string
  version: string
  /** 更新日志 */
  notes: string
  /** 0~1，下载进度 */
  progress: number
  /** needsit 时是程序所在目录，用来告诉 IT 哪里写不了 */
  message: string
}

/** 安全中心里的一项 */
export interface SecurityItem {
  key: string
  value: boolean | number
  /** true = 由 IT 统一配置，本机改不了 */
  locked: boolean
  /** bool / int */
  kind: string
  title: string
  /** 关掉它的后果。用户要关时原样显示 */
  risk: string
  min: number | null
  max: number | null
}

/** 一次语音输入的结果。失败时 reason 说明原因，界面据此给不同的提示 */
export interface SpeechResult {
  ok: boolean
  text: string
  /** device / tooShort / silent / empty / cancelled / failed */
  reason: string
  message: string
}

/** 界面上的工具卡片状态 */
export interface ToolActivity {
  callId: string
  name: string
  summary: string
  risk: Risk
  state: 'running' | 'waiting' | 'done' | 'failed' | 'blocked' | 'rejected'
  output?: string
  /** 调用参数（JSON 原文），展开卡片时逐项显示 */
  args?: string
  confirm?: ConfirmPrompt
  /** 之前授权过的同样操作，本次自动通过 */
  remembered?: boolean
}

export interface ConfirmPrompt {
  requestId: string
  reason: string
  rationale: string
  /** 能否“以后同样的操作自动允许” */
  rememberable: boolean
  ruleDisplay?: string
  effect?: string
}

export type ConfirmChoice = 'allowOnce' | 'allowForSession' | 'allowAlways' | 'reject'

// ---------- 宿主推送的事件 ----------
export type HostEvent =
  | { type: 'chat.delta'; conversationId: string; text: string }
  | { type: 'chat.reasoning'; conversationId: string; text: string }
  | { type: 'chat.message'; conversationId: string; message: UiMessage }
  | { type: 'tool.started'; conversationId: string; callId: string; name: string; summary: string; args?: string; risk: Risk }
  | {
      type: 'tool.confirm'
      conversationId: string
      requestId: string
      callId: string
      reason: string
      rationale: string
      rememberable?: boolean
      /** 勾选「以后自动执行」后生效的规则说明 */
      ruleDisplay?: string
      /** read / write / destructive / unknown */
      effect?: string
    }
  | { type: 'tool.finished'; conversationId: string; callId: string; ok: boolean; output: string; decision: string }
  | { type: 'plan.updated'; conversationId: string; plan: PlanItem[] }
  | { type: 'files.produced'; conversationId: string; files: OutputFile[] }
  | { type: 'chat.trace'; conversationId: string; messageId: string; trace: string }
  | { type: 'speech.tick'; level: number; elapsedMs: number; maxMs: number }
  | { type: 'speech.autoStop' }
  | { type: 'chat.notice'; conversationId: string; text: string }
  | {
      type: 'context.compacting'
      conversationId: string
      /** scanning / summarizing / done */
      phase: string
      percent: number
      messages: number
    }
  | {
      type: 'chat.done'
      conversationId: string
      stopReason: string
      modelName?: string
      usage?: { promptTokens: number; completionTokens: number } | null
    }
  | { type: 'tool.confirmResolved'; conversationId: string; callId: string; choice: 'allow' | 'reject' }
  | { type: 'context.compacted'; conversationId: string; uptoMessageId: string; tokensBefore: number; tokensAfter: number }
  | { type: 'memory.learned'; conversationId: string; items: { kind: MemoryKind; text: string }[]; skill: string | null }
  | { type: 'app.openConversation'; conversationId: string }
  | { type: 'window.state'; maximized: boolean }
  | { type: 'skills.changed' }
  | { type: 'mcp.changed'; id: string; vendor: McpVendor | null }
  | { type: 'schedules.changed' }
  | { type: 'chat.error'; conversationId: string; message: string }
  | { type: 'conversation.updated'; conversation: Conversation }
  | { type: 'files.added'; attachments: AttachmentRef[] }
  | { type: 'library.changed' }
  | ({ type: 'app.status'; connected: boolean; serverMessage: string; modelName: string; department?: string; owner?: string } & Partial<
      Pick<AppInfo, 'sandboxed' | 'notifications' | 'notificationSound' | 'notificationsLocked' | 'soundLocked' | 'keepAwakeAllowed' | 'keepScreenAllowed'>
    >)
  | { type: 'app.focusInput' }
  | { type: 'app.translate'; text: string; from: string; to: string }
  | ({ type: 'update.state' } & UpdateInfo)

export type KeepAwakeMode = 'off' | 'tasks' | 'awake' | 'screen'

export interface AboutMe {
  department: string
  position: string
  language: string
  systems: string
  folders: string
  other: string
}

/** 个性化：回复语气、称呼和名字、关于我 */
export interface PersonaInfo {
  /** 选的语气（default / 各预设 / custom） */
  preset: string
  /** 实际生效的（IT 关掉了选中的那个时退回 default） */
  effective: string
  /** 自定义语气的文字（soul.md） */
  custom: string
  callName: string
  assistantName: string
  playfulAllowed: boolean
  customAllowed: boolean
  presets: { key: string; playful: boolean }[]
  about: AboutMe
}

export interface PersonaUpdate {
  preset?: string
  custom?: string
  callName?: string
  assistantName?: string
  about?: AboutMe
}

/** 「检查更新」的结果 */
export interface UpdateCheckResult {
  outcome: 'upToDate' | 'downloading' | 'ready' | 'needsIt' | 'failed'
  version: string
  current: string
  message: string
}
