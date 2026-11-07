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
  /** 呼叫的专家。为空是普通助手 */
  expertId?: string | null
  /** 侧栏手动排序的位置（小的在前）。没拖过为空 */
  sortOrder?: number | null
  /** 长任务模式：步数上限放宽，走到上限自动接着做 */
  longMode?: boolean
}

/** 管理员发布的专家（界面只拿展示用的部分） */
export interface Expert {
  id: string
  name: string
  /** 一个 emoji，或图片链接 / data: URL */
  avatar: string
  tagline: string
  category: string
  tags: string[]
  /** 署名：谁维护这位专家 */
  author: string
  /** 名片：负责什么、适合什么、不负责什么、需要什么信息 */
  description: string
  examples: string[]
  readOnly: boolean
  version: number
  /** 全公司累计使用次数 */
  uses: number
  /** 用过的设备数 */
  users: number
  publishedAt: string | null
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
  /** 平均每次任务用掉的 token（输入 + 输出），最多的一次 */
  avgTokens?: number
  maxTokens?: number
  /** 超出预算 / 空转被暂停的次数，被提醒过空转或预算的任务数 */
  budgetStops?: number
  stuckStops?: number
  guardNudgeRuns?: number
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
  /** model / tool / compact / guard / verify / subtask（派遣专家：name 是专家名，detail 是子任务 ID） */
  kind: string
  name: string
  summary: string
  /** 补充说明：模型调用这一步输入 token 的构成（系统提示 / 工具定义 / 对话 / 工具输出） */
  detail?: string
  /** ok / error / blocked / stopped / skipped / warning */
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

export type ScheduleKind = 'manual' | 'once' | 'hourly' | 'daily' | 'weekdays' | 'weekly' | 'monthly' | 'folder'

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
  /** 文件夹触发：监视的文件夹、只看哪些文件（*.xlsx;*.csv，空 = 全部） */
  watchPath?: string
  pattern?: string
  enabled: boolean
  workspace: string | null
  permission: Permission
  modelId: number | null
  /** 错过的任务开机后补跑 */
  catchUp: boolean
  /** 预演：只读调研，列出会做的修改，不真的执行 */
  rehearsal: boolean
  nextRunAt: string | null
  lastRunAt: string | null
  /** ok / rehearsed / failed / stopped / running */
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
  /** 今天的额度什么时候清零（ISO 时间）；老服务端没有时为 null */
  resetsAt?: string | null
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
  /** global（IT 在后台建的）/ plugin（插件带来的） */
  sourceType?: 'global' | 'plugin' | string
  plugin?: string
  sourcePluginVersion?: string
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
  /** 最近一次健康检查的往返耗时（毫秒） */
  latencyMs?: number | null
  /** 有这个连接器的日志 */
  hasLog?: boolean
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
  /** 员工粘贴的长文本：界面上是卡片，发给模型时整段放进消息 */
  pasted?: boolean
  /** 粘贴的图片还在后台存成文件（界面先显示缩略图，存好之前不能发送、不能打开） */
  saving?: boolean
  /** 粘贴的全文，只在本机界面里用（编辑、查看），不发给宿主；从历史加载的消息没有，要看时向宿主读 */
  pasteText?: string
}

export interface ToolCallRef {
  id: string
  name: string
  arguments: string
}

export interface WebSource {
  title: string
  url: string
  snippet?: string
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
  /** 联网搜索来源 */
  sources?: WebSource[] | null
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
  /** 连接的服务器地址 */
  serverUrl?: string
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
  /** 资源管理器右键菜单里的 FlyknitBuddy */
  explorerMenu?: boolean
  /** 锁屏运行：off / tasks / awake / screen */
  keepAwake: KeepAwakeMode
  /** IT 允不允许阻止睡眠、允不允许屏幕常亮 */
  keepAwakeAllowed: boolean
  /** 会话信息面板能不能展开详细拆分（IT 可以关） */
  contextDetails?: boolean
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
  /** IT 允不允许开长任务模式 */
  longTasks?: boolean
  /** IT 打开了「开发者工具」：办事模式有 grep / git 工具，输入框能用 /init */
  developerTools?: boolean
  /** 后台联网搜索已就绪 */
  webSearchEnabled?: boolean
  /** 本地偏好：深度思考 */
  deepThinking?: boolean
  /** 本地偏好：联网搜索（仅对话） */
  webSearch?: boolean
}

/** 任务中心：正在跑的任务 */
export interface RunningRun {
  conversationId: string
  title: string
  step: string
  done: number
  total: number
  paused: boolean
  startedAt: string
  remote: boolean
}

/** 上次没跑完就被关掉的任务。origin：ui / remote / headless */
export interface InterruptedRunInfo {
  conversationId: string
  title: string
  startedAt: string
  origin: string
}

/** 最近跑完的一轮 */
export interface RecentRunInfo {
  conversationId: string
  title: string
  stopReason: string
  steps: number
  toolCalls: number
  finishedAt: string
}

export interface RunsList {
  running: RunningRun[]
  interrupted: InterruptedRunInfo[]
  recent: RecentRunInfo[]
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
  /** AI 用选项向员工提问（ask_user），等员工点选 */
  ask?: AskPrompt
  /** 之前授权过的同样操作，本次自动通过 */
  remembered?: boolean
  /** 专家执行时请求确认的那一步（不是主助手自己的调用）：不单独画卡片，显示在专家任务卡片里 */
  actor?: ConfirmActor
}

/** 上下文里的一块（会话信息面板） */
export interface ContextPart {
  key: string
  label: string
  tokens: number
  items: ContextPart[]
}

/** 一个对话最近一次回答时上下文的用量（宿主按字数估算） */
export interface ContextUsage {
  contextLength: number
  reserveOutput: number
  compactAt: number
  parts: ContextPart[]
  used: number
  lastPromptTokens: number | null
  measuredAt: string
}

/** AI 用选项提问：一次 1～4 个问题，每个 2～4 个选项；界面自动加「其他（自己输入）」 */
export interface AskPrompt {
  requestId: string
  questions: AskQuestionView[]
}

export interface AskQuestionView {
  header: string
  question: string
  multiSelect: boolean
  options: { label: string; description: string }[]
}

/** 员工对一个问题的回答 */
export interface AskAnswerInput {
  selected: string[]
  other: string
}

/** 谁在请求确认：专家派遣时是哪位专家 */
export interface ConfirmActor {
  kind: 'expert'
  id: string
  name: string
  avatar: string
  subtaskId: string
}

// ---------- 专家派遣（docs/expert-delegation-contract.md 第 6 节） ----------
export type SubtaskStatus =
  | 'running'
  | 'waiting_confirmation'
  | 'completed'
  | 'partial'
  | 'needs_input'
  | 'failed'
  | 'cancelled'
  | 'timed_out'

/** 终态：到了这几种就不会再变 */
export const SubtaskFinal: SubtaskStatus[] = ['completed', 'partial', 'needs_input', 'failed', 'cancelled', 'timed_out']

export interface SubtaskExpert {
  id: string
  name: string
  avatar: string
  version: number
}

export type FindingVerification = 'supported' | 'source_verified' | 'unverified' | 'verification_failed'

export interface SubtaskFinding {
  id: string
  statement: string
  evidence: { ref: string; kind: 'tool' | 'file'; label: string; check: 'ok' | 'missing' | 'failed_call' }[]
  verification: FindingVerification
  note: string
}

/** 一次专家派遣（子任务）的结果，宿主 subtasks.list / subtasks.get / expert.finished 给的 */
export interface SubtaskDto {
  id: string
  callId: string
  conversationId: string
  expert: SubtaskExpert
  task: string
  context: string
  status: SubtaskStatus
  summary: string
  /** 完整的文字结果（专家、子代理最后那段回答） */
  report?: string
  findings: SubtaskFinding[]
  openQuestions: string[]
  nextSuggestions: string[]
  artifacts: { path: string; name: string; exists: boolean }[]
  confidence: number | null
  toolCalls: { name: string; count: number; failed: number }[]
  tokens: { prompt: number; completion: number; estimated: boolean }
  startedAt: string
  finishedAt: string | null
  durationMs: number
  errorCode: string
  error: string
  /** TraceDto JSON，和消息上的 trace 同格式。list 不带，get 才带 */
  trace: string | null
}

export type ExpertStepKind = 'tool_started' | 'tool_completed' | 'waiting_confirmation' | 'thinking'

export interface ExpertStep {
  kind: ExpertStepKind
  name: string
  summary: string
  ok?: boolean
}

/** 界面上一张专家任务卡片的状态（按主助手那次 ask_expert 的 callId 记） */
export interface SubtaskView {
  callId: string
  subtaskId: string
  /** 已收到的最大 seq，不大于它的事件丢掉 */
  seq: number
  status: SubtaskStatus
  expert: SubtaskExpert | null
  task: string
  startedAt: string | null
  /** 最近一步：卡片上显示「正在调用 xx…」 */
  step: ExpertStep | null
  /** 结束后的完整结果 */
  result: SubtaskDto | null
}

export interface ConfirmPrompt {
  requestId: string
  reason: string
  rationale: string
  /** 能否“以后同样的操作自动允许” */
  rememberable: boolean
  ruleDisplay?: string
  effect?: string
  /** 专家在请求确认（主助手自己的调用为空） */
  actor?: ConfirmActor
}

export type ConfirmChoice = 'allowOnce' | 'allowForSession' | 'allowAlways' | 'reject'

// ---------- 宿主推送的事件 ----------
export type HostEvent =
  | { type: 'chat.delta'; conversationId: string; text: string }
  | { type: 'chat.reasoning'; conversationId: string; text: string }
  | { type: 'chat.sources'; conversationId: string; sources: WebSource[] }
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
      /** 专家执行时请求的确认 */
      actor?: ConfirmActor
    }
  | {
      type: 'expert.started'
      conversationId: string
      callId: string
      subtaskId: string
      seq: number
      expert: SubtaskExpert
      task: string
      startedAt: string
    }
  | ({ type: 'expert.step'; conversationId: string; callId: string; subtaskId: string; seq: number } & ExpertStep)
  | { type: 'expert.finished'; conversationId: string; callId: string; subtaskId: string; seq: number; subtask: SubtaskDto }
  | { type: 'tool.finished'; conversationId: string; callId: string; ok: boolean; output: string; decision: string }
  | { type: 'plan.updated'; conversationId: string; plan: PlanItem[] }
  | { type: 'files.produced'; conversationId: string; files: OutputFile[] }
  | { type: 'chat.trace'; conversationId: string; messageId: string; trace: string }
  | { type: 'speech.tick'; level: number; elapsedMs: number; maxMs: number }
  | { type: 'speech.autoStop' }
  | { type: 'chat.notice'; conversationId: string; text: string }
  | { type: 'chat.pending'; conversationId: string; items: PendingInput[] }
  | { type: 'chat.paused'; conversationId: string; paused: boolean }
  | { type: 'runs.changed' }
  | { type: 'browser.state'; active: boolean; url: string; title: string; action?: string; shot?: string }
  | { type: 'terminal.started'; run: TerminalRun }
  /** AI 写了 / 改了 Markdown 文稿：在文档画布里打开或刷新 */
  | { type: 'document.changed'; conversationId: string; path: string; name: string; by: 'ai' }
  | { type: 'terminal.output'; conversationId: string; runId: string; text: string }
  | { type: 'terminal.ended'; run: TerminalRun }
  | { type: 'sidechat.delta'; requestId: string; text: string }
  | { type: 'sidechat.done'; requestId: string; content?: string; stopped?: boolean }
  | { type: 'sidechat.error'; requestId: string; message: string }
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
      /** 因预算停下时：true 是 token 到了上限，false 是连续没有进展 */
      budgetByTokens?: boolean
      budgetTokens?: number
      budgetLimit?: number
      modelName?: string
      usage?: { promptTokens: number; completionTokens: number } | null
    }
  | { type: 'tool.confirmResolved'; conversationId: string; callId: string; choice: 'allow' | 'reject' }
  | { type: 'tool.ask'; conversationId: string; requestId: string; callId: string; questions: AskQuestionView[] }
  | { type: 'tool.askResolved'; conversationId: string; callId: string }
  | { type: 'context.usage'; conversationId: string; usage: ContextUsage }
  | { type: 'context.compacted'; conversationId: string; uptoMessageId: string; tokensBefore: number; tokensAfter: number }
  | { type: 'memory.learned'; conversationId: string; items: { kind: MemoryKind; text: string }[]; skill: string | null }
  | { type: 'app.openConversation'; conversationId: string }
  | { type: 'window.state'; maximized: boolean }
  | { type: 'skills.changed' }
  | { type: 'experts.changed' }
  | { type: 'mcp.changed'; id: string; vendor: McpVendor | null }
  | { type: 'schedules.changed' }
  | { type: 'chat.error'; conversationId: string; message: string }
  | { type: 'conversation.updated'; conversation: Conversation }
  | { type: 'files.added'; attachments: AttachmentRef[] }
  | { type: 'screenshot.taken'; attachment: AttachmentRef }
  /** 资源管理器右键菜单：文件发过来提问 / 总结 / 翻译，或者在某个文件夹里办事 */
  | { type: 'shell.request'; action: 'ask' | 'summarize' | 'translate' | 'workspace'; attachments?: AttachmentRef[]; path?: string }
  | { type: 'library.changed' }
  | ({ type: 'app.status'; connected: boolean; serverMessage: string; modelName: string; department?: string; owner?: string } & Partial<
      Pick<AppInfo, 'sandboxed' | 'notifications' | 'notificationSound' | 'notificationsLocked' | 'soundLocked' | 'keepAwakeAllowed' | 'keepScreenAllowed' | 'contextDetails' | 'longTasks' | 'developerTools' | 'webSearchEnabled' | 'deepThinking' | 'webSearch'>
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

export type CheckStatus = 'pass' | 'warn' | 'fail' | 'skip'

/** 设置 → 网络检测的一项结果。code 是结果代号，界面按它翻译 */
export interface NetworkCheck {
  id: 'proxy' | 'hosts' | 'dns' | 'http' | 'auth' | 'tcp' | 'loss' | 'endpoint'
  status: CheckStatus
  code: string
  value: string
  ms: number | null
  error: string
}

export interface NetworkReport {
  at: string
  endpoint: string
  host: string
  port: number
  scheme: string
  proxyMode: string
  proxyUrl: string
  overall: CheckStatus
  checks: NetworkCheck[]
  /** 本机报告文件，写失败时为空 */
  reportPath: string
  reportDir: string
}

/** 钉钉通道：这台电脑绑定的钉钉账号 */
export interface ChannelBindingInfo {
  id: number
  channel: string
  name: string
  createdAt: string
  lastUsed: string | null
}

export interface ChannelsInfo {
  /** 管理员开了钉钉通道 */
  dingtalk: boolean
  /** 管理员开了企业微信通道 */
  wecom: boolean
  /** 连着公司服务器 */
  online: boolean
  bindings: ChannelBindingInfo[]
}

/** 绑定在这台电脑上的一部手机（FlyknitBuddy 安卓 App） */
export interface MobilePhone {
  id: number
  name: string
  appVersion: string
  createdAt: string
  lastSeen: string | null
  /** App 现在连着服务器 */
  connected: boolean
}

export interface MobileInfo {
  /** 管理员开了手机端 */
  enabled: boolean
  /** 连着公司服务器 */
  online: boolean
  phones: MobilePhone[]
  /** 后台发布过的最新 App：下载地址（扫码直接下载） */
  app: { versionName: string; url: string } | null
}

/** 手机扫的二维码：服务器地址 + 一次性配对码 */
export interface MobilePairCode {
  code: string
  expiresAt: string
  payload: string
  server: string
}

/** 运行中又发的话：插话 steer（下一步交给 AI）/ 排队 queue（这一轮结束后再发）。AI 读到之前能改、能撤回 */
export interface PendingInput {
  id: string
  text: string
  mode: 'steer' | 'queue'
  at: string
}

export type InterjectMode = 'steer' | 'queue' | 'interrupt'

/** 一轮任务改动的一个文件。kind：created 新建 / modified 修改 / deleted 删除 */
export interface FileChangeInfo {
  path: string
  name: string
  kind: 'created' | 'modified' | 'deleted'
  /** 哪个工具改的：write_file / delete_path / run_shell */
  source: string
  /** 这一轮里改了几次 */
  times: number
  /** 改之前那一份的备份（对比、还原用） */
  backup?: string | null
  emptyBefore?: boolean
  restorable: boolean
  exists: boolean
  reverted: boolean
}

/** 一轮任务（一句话引起的）改动的文件。turnId 是那条用户消息的 ID */
export interface ChangeTurn {
  turnId: string
  at: string
  files: FileChangeInfo[]
}

/** 终端里的一条命令（AI 执行的；后台命令一直跑到被停掉） */
export interface TerminalRun {
  id: string
  conversationId: string
  command: string
  shell: string
  cwd: string
  background: boolean
  startedAt: string
  endedAt?: string | null
  running: boolean
  stopped: boolean
  exitCode?: number | null
  /** terminal.list 时带着；事件里没有，靠 terminal.output 一段段拼 */
  output?: string | null
}

export interface RevertOutcome {
  reverted: string[]
  skipped: { path: string; reason: string }[]
}

/** 文档画布里打开的文档（宿主 document.load） */
export interface DocumentInfo {
  ok: boolean
  path: string
  name: string
  text: string
  /** 内容指纹：保存时带上，文件在这期间被改过就不覆盖 */
  version: string
  exists: boolean
  tooLarge: boolean
}

export interface DocumentSaveResult {
  ok: boolean
  /** 文件在编辑期间被改过（多半是 AI 用 edit_file 改的）：currentText / version 是现在的内容 */
  conflict: boolean
  version: string
  currentText: string
  message: string
}

/** 右侧「文件」标签里工作区的一项（宿主 workspace.list / workspace.search） */
export interface WorkspaceEntry {
  name: string
  /** 工作区里的相对路径（展开文件夹时传回去） */
  path: string
  fullPath: string
  isDir: boolean
  size: number
  modifiedAt: string
  /** 依赖、编译输出这类文件夹：标灰 */
  noise: boolean
  previewable: boolean
}

export interface WorkspaceListing {
  root: string
  exists: boolean
  truncated: boolean
  error: string
  entries: WorkspaceEntry[]
}

/** 快捷指令：输入框里打 / 选的提示词模板。模板里的 {参数} 按顺序替换 */
export interface SlashCommand {
  name: string
  title: string
  description: string
  template: string
  /** IT 指令里由哪个插件带来的（插件名） */
  source?: string
}

/** IT 按部门下发的插件（设置 →「插件」只读展示） */
export interface PluginInfo {
  id: string
  name: string
  version: string
  description: string
  author: string
  required: boolean
  counts: { skills: number; commands: number; experts: number; connectors: number; rules: number }
  skills: string[]
  commands: string[]
  experts: string[]
  connectors: string[]
}
