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

export interface LearnedSkillInfo {
  name: string
  description: string
  path: string
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

export type PreviewKind = 'text' | 'markdown' | 'table' | 'sections' | 'image' | 'pdf' | 'diagram' | 'listing' | 'none'

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

export type SecurityDecision = 'blocked' | 'approved' | 'remembered' | 'rejected'

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
  connected: boolean
  serverMessage: string
  modelName: string
  defaultModelId: number | null
  defaultWorkspace: string
  defaultPermission: Permission
  workspaces: WorkspaceInfo[]
  learning: boolean
  notifications: boolean
  notificationSound: string
  fontScale: number
  autoStart: boolean
  proxyMode: string
  proxyUrl: string
  proxyUser: string
  dataDir: string
  shortcuts: ShortcutInfo[]
  maximized: boolean
}

/** 界面上的工具卡片状态 */
export interface ToolActivity {
  callId: string
  name: string
  summary: string
  risk: Risk
  state: 'running' | 'waiting' | 'done' | 'failed' | 'blocked' | 'rejected'
  output?: string
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

export type ConfirmChoice = 'allowOnce' | 'allowAlways' | 'reject'

// ---------- 宿主推送的事件 ----------
export type HostEvent =
  | { type: 'chat.delta'; conversationId: string; text: string }
  | { type: 'chat.reasoning'; conversationId: string; text: string }
  | { type: 'chat.message'; conversationId: string; message: UiMessage }
  | { type: 'tool.started'; conversationId: string; callId: string; name: string; summary: string; risk: Risk }
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
  | { type: 'schedules.changed' }
  | { type: 'chat.error'; conversationId: string; message: string }
  | { type: 'conversation.updated'; conversation: Conversation }
  | { type: 'files.added'; attachments: AttachmentRef[] }
  | { type: 'app.status'; connected: boolean; serverMessage: string; modelName: string }
  | { type: 'app.focusInput' }
