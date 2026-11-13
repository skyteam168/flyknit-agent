/**
 * 与 WPF 宿主通信。
 *
 * 请求：页面 → 宿主  { kind: 'request', id, method, params }
 * 响应：宿主 → 页面  { kind: 'response', id, ok, result | error }
 * 事件：宿主 → 页面  { kind: 'event', event: HostEvent }
 *
 * 在普通浏览器中运行（npm run dev）时，使用 mockHost 模拟宿主。
 */
import type { DocModel } from './documents'
import type {
  RepoInfo,
  RefCard,
  PrWatchView,
  SubscriptionView,
  SandboxStatus,
  SlashCommand,
  PluginInfo,
  WorkspaceEntry,
  WorkspaceListing,
  DocumentInfo,
  DocumentSaveResult,
  ChannelsInfo,
  MobileInfo,
  MobilePairCode,
  NetworkReport,
  UpdateCheckResult,
  AppInfo,
  ApprovalInfo,
  UpdateInfo,
  MemoryAddResult,
  MemoryConsolidateResult,
  MemoryKind,
  LibrarySkill,
  McpVendor,
  MemoryOverview,
  SkillActionResult,
  SkillInspection,
  SkillInstallOutcome,
  ScheduledTask,
  SecurityEvent,
  ExportResult,
  NetworkAllowlist,
  OutputFile,
  PreviewDoc,
  ShortcutInfo,
  StorageInfo,
  UsageStats,
  Permission,
  WorkspaceInfo,
  AttachmentRef,
  AskAnswerInput,
  ContextUsage,
  ConfirmChoice,
  Conversation,
  Expert,
  HostEvent,
  Mode,
  ModelInfo,
  SecurityItem,
  SkillInfo,
  SpeechResult,
  Theme,
  UiLanguage,
  UiMessage,
  LibraryFolder,
  LibraryItem,
  LearnedSkillStatus,
  MemoryMetrics,
  PlanItem,
  KeepAwakeMode,
  PersonaInfo,
  PersonaUpdate,
  SubtaskDto,
  InterjectMode,
  PendingInput,
  RunsList,
  ChangeTurn,
  TerminalRun,
  RevertOutcome,
} from './types'
import { createMockHost } from './mockHost'
import { i18n } from './i18n'

type Pending = { resolve: (v: unknown) => void; reject: (e: Error) => void }
type Listener = (e: HostEvent) => void

export interface HostTransport {
  send(message: unknown): void
  sendWithFiles(message: unknown, files: File[]): void
  onMessage(handler: (data: unknown) => void): void
}

function webViewTransport(): HostTransport | null {
  const wv = window.chrome?.webview
  if (!wv) return null
  return {
    send: (m) => wv.postMessage(m),
    sendWithFiles: (m, files) => wv.postMessageWithAdditionalObjects(m, files),
    onMessage: (h) => wv.addEventListener('message', (e) => h(e.data)),
  }
}

class Bridge {
  private seq = 0
  private pending = new Map<string, Pending>()
  private listeners = new Set<Listener>()
  readonly isMock: boolean
  private transport: HostTransport

  constructor() {
    const real = webViewTransport()
    this.isMock = real === null
    this.transport = real ?? createMockHost()
    this.transport.onMessage((data) => this.receive(data))
  }

  private receive(raw: unknown) {
    console.debug('[flyknit] host →', raw)
    const data = (typeof raw === 'string' ? JSON.parse(raw) : raw) as {
      kind: string
      id?: string
      ok?: boolean
      result?: unknown
      error?: string
      event?: HostEvent
    }
    if (data.kind === 'response' && data.id) {
      const p = this.pending.get(data.id)
      if (!p) return
      this.pending.delete(data.id)
      if (data.ok) p.resolve(data.result)
      else p.reject(new Error(data.error || 'Unknown error'))
    } else if (data.kind === 'event' && data.event) {
      for (const l of this.listeners) l(data.event)
    }
  }

  /** timeoutMs 为 0 表示不限时（弹系统对话框等用户操作的调用） */
  private call<T>(method: string, params: Record<string, unknown> = {}, files?: File[], timeoutMs = 20000): Promise<T> {
    const id = `r${++this.seq}`
    const message = { kind: 'request', id, method, params }
    return new Promise<T>((resolve, reject) => {
      // 宿主 20 秒未响应视为失败，避免界面一直空白；要等浏览器登录这类调用自己给更长的时间。
      // 给用户看的是一句友好的话，具体是哪个调用写到控制台里
      const timer =
        timeoutMs > 0
          ? window.setTimeout(() => {
              if (!this.pending.delete(id)) return
              console.warn(`[flyknit] host timeout: ${method} (${timeoutMs}ms)`)
              reject(new Error(i18n.global.t('ui.common.hostTimeout')))
            }, timeoutMs)
          : undefined
      this.pending.set(id, {
        resolve: (v) => {
          clearTimeout(timer)
          ;(resolve as (v: unknown) => void)(v)
        },
        reject: (e) => {
          clearTimeout(timer)
          reject(e)
        },
      })
      if (files && files.length) this.transport.sendWithFiles(message, files)
      else this.transport.send(message)
    })
  }

  on(listener: Listener): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  // ---------- 应用 ----------
  init = () => this.call<AppInfo>('app.init')
  setLanguage = (language: UiLanguage) => this.call<void>('settings.setLanguage', { language })
  setTheme = (theme: Theme) => this.call<void>('settings.setTheme', { theme })
  openMemoryFolder = () => this.call<void>('settings.openMemoryFolder')
  hideWindow = () => this.call<void>('window.hide')
  minimizeWindow = () => this.call<void>('window.minimize')
  toggleTopmost = () => this.call<boolean>('window.toggleTopmost')
  toggleMaximize = () => this.call<boolean>('window.toggleMaximize')
  /** 从窗口边缘开始调整大小（无边框窗口自己实现） */
  startResize = (direction: string) => this.call<void>('window.startResize', { direction })
  /** WebView2 运行时太旧、页面拖动区域不可用时由宿主拖动窗口（appRegion.ts） */
  startDrag = () => this.call<void>('window.startDrag')
  setLearning = (enabled: boolean) => this.call<void>('settings.setLearning', { enabled })
  setMaxSteps = (value: number) => this.call<number>('settings.setMaxSteps', { value })
  setWindowBackground = (color: string) => this.call<void>('window.setBackground', { color })
  setNotifications = (enabled: boolean) =>
    this.call<{ ok: boolean; message: string; enabled: boolean }>('settings.setNotifications', { enabled })

  // ---------- 记忆 ----------
  memoryOverview = () => this.call<MemoryOverview>('memory.list')
  deleteMemory = (id: string) => this.call<void>('memory.delete', { id })
  pinMemory = (id: string, pinned: boolean) => this.call<boolean>('memory.pin', { id, pinned })
  /** 过期的记忆仍然有效：从今天起重新算有效期 */
  renewMemory = (id: string) => this.call<boolean>('memory.renew', { id })
  addMemory = (kind: MemoryKind, text: string) => this.call<MemoryAddResult>('memory.add', { kind, text })
  // 整理要请求模型好几次，宿主那边最多等 8 分钟，这里多留一点
  consolidateMemory = () => this.call<MemoryConsolidateResult>('memory.consolidate', {}, undefined, 9 * 60_000)
  deleteEpisode = (id: string) => this.call<void>('episodes.delete', { id })
  deleteLearnedSkill = (name: string) => this.call<void>('skills.deleteLearned', { name })
  setLearnedSkillStatus = (name: string, status: LearnedSkillStatus) =>
    this.call<boolean>('skills.setLearnedStatus', { name, status })
  memoryMetrics = () => this.call<MemoryMetrics>('memory.metrics')

  // ---------- 会话 ----------
  listConversations = (query = '', trash = false) =>
    this.call<Conversation[]>('conversations.list', { query, trash })
  createConversation = (mode: Mode, modelId: number | null, workspace: string | null, permission: Permission, expertId: string | null = null) =>
    this.call<Conversation>('conversation.create', { mode, modelId, workspace, permission, expertId })
  listExperts = () => this.call<Expert[]>('experts.list')
  enhancePrompt = (text: string, mode: Mode, conversationId: string | null, expertId: string | null) =>
    this.call<{ text: string }>('prompt.enhance', { text, mode, conversationId, expertId }, undefined, 90_000)
  setWorkspace = (id: string, path: string | null) => this.call<void>('conversation.setWorkspace', { id, path })
  setPermission = (id: string, permission: Permission) => this.call<void>('conversation.setPermission', { id, permission })
  setModel = (id: string, modelId: number | null) => this.call<void>('conversation.setModel', { id, modelId })
  renameConversation = (id: string, title: string) => this.call<void>('conversation.rename', { id, title })
  deleteConversation = (id: string) => this.call<void>('conversation.delete', { id })
  restoreConversation = (id: string) => this.call<void>('conversation.restore', { id })
  purgeConversation = (id: string) => this.call<void>('conversation.purge', { id })
  pinConversation = (id: string, pinned: boolean) => this.call<void>('conversation.pin', { id, pinned })
  setMode = (id: string, mode: Mode) => this.call<void>('conversation.setMode', { id, mode })
  setTranslate = (id: string, from: string, to: string) =>
    this.call<void>('conversation.setTranslate', { id, from, to })
  loadMessages = (id: string) => this.call<UiMessage[]>('messages.load', { id })
  /** 对话保存的任务计划（重新打开对话时显示） */
  loadPlan = (id: string) => this.call<PlanItem[]>('conversations.plan', { id })

  // ---------- 模型与技能 ----------
  listModels = (refresh = false) => this.call<ModelInfo[]>('models.list', { refresh })
  setDefaultModel = (modelId: number | null) => this.call<void>('settings.setDefaultModel', { modelId })
  listSkills = () => this.call<SkillInfo[]>('skills.list')
  /** 不传路径时弹文件夹选择框 */
  openSkillsFolder = (path?: string) => this.call<void>('skills.openFolder', path ? { path } : {}, undefined, path ? undefined : 0)
  setSkillEnabled = (name: string, enabled: boolean) => this.call<SkillActionResult>('skills.setEnabled', { name, enabled })
  uninstallSkill = (name: string) => this.call<SkillActionResult>('skills.uninstall', { name })
  /** 选择 zip 文件安装；取消选择返回 null */
  inspectSkill = (path?: string) =>
    this.call<{ path: string; inspection: SkillInspection } | null>('skills.inspect', path ? { path } : {}, undefined, path ? 60_000 : 0)
  installSkill = (path?: string) => this.call<SkillInstallOutcome | null>('skills.install', path ? { path } : {}, undefined, path ? 180_000 : 0)
  installSkillFolder = (path?: string) =>
    this.call<SkillInstallOutcome | null>('skills.installFolder', path ? { path } : {}, undefined, path ? 180_000 : 0)
  installSkillFromUrl = (url: string) => this.call<SkillInstallOutcome>('skills.installFromUrl', { url }, undefined, 200_000)
  skillLibrary = () => this.call<LibrarySkill[]>('skills.library')

  // ---------- MCP 连接器（和技能是两组调用，互不影响） ----------
  listMcp = (refresh = false) => this.call<McpVendor[]>('mcp.list', { refresh })
  /** 可能要等浏览器登录，最长几分钟；结果（成功或原因）在返回值里 */
  connectMcp = (id: string, values: Record<string, string>) =>
    this.call<{ ok: boolean; message: string; vendor: McpVendor | null }>('mcp.connect', { id, values }, undefined, 6 * 60_000)
  cancelMcp = (id: string) => this.call<void>('mcp.cancel', { id })
  disconnectMcp = (id: string, forget: boolean) => this.call<McpVendor | null>('mcp.disconnect', { id, forget })
  openMcpLog = (id: string) => this.call<boolean>('mcp.openLog', { id })
  // 从技能库下载安装，宿主最多等 3 分钟
  installSkillFromLibrary = (name: string) => this.call<SkillInstallOutcome>('skills.installFromLibrary', { name }, undefined, 200_000)

  // ---------- 工作区与权限 ----------
  listWorkspaces = () => this.call<WorkspaceInfo[]>('workspaces.list')
  /** 不传 path 时弹出文件夹选择框；取消返回 null */
  addWorkspace = (path?: string) => this.call<string | null>('workspaces.add', path ? { path } : {}, undefined, path ? undefined : 0)
  removeWorkspace = (path: string) => this.call<WorkspaceInfo[]>('workspaces.remove', { path })
  reorderWorkspaces = (paths: string[]) => this.call<WorkspaceInfo[]>('workspaces.reorder', { paths })
  reorderConversations = (ids: string[]) => this.call<void>('conversations.reorder', { ids })
  setDefaultWorkspace = (path: string | null) => this.call<void>('settings.setDefaultWorkspace', { path })
  setDefaultPermission = (permission: Permission) => this.call<void>('settings.setDefaultPermission', { permission })
  // ---------- 定时任务 ----------
  listSchedules = () => this.call<ScheduledTask[]>('schedules.list')
  saveSchedule = (task: Partial<ScheduledTask>) => this.call<ScheduledTask>('schedules.save', task as Record<string, unknown>)
  setScheduleEnabled = (id: string, enabled: boolean) => this.call<ScheduledTask | null>('schedules.setEnabled', { id, enabled })
  deleteSchedule = (id: string) => this.call<void>('schedules.delete', { id })
  runSchedule = (id: string) => this.call<{ ok: boolean; message: string; conversationId: string | null }>('schedules.run', { id })

  usageStats = () => this.call<UsageStats>('usage.stats')

  setFontScale = (scale: number) => this.call<number>('settings.setFontScale', { scale })
  setNotificationSound = (sound: string) =>
    this.call<{ ok: boolean; message: string; sound: string }>('settings.setNotificationSound', { sound })
  previewSound = (sound: string) => this.call<void>('settings.previewSound', { sound })
  setAutoStart = (enabled: boolean) => this.call<{ ok: boolean; message: string; enabled: boolean }>('settings.setAutoStart', { enabled })
  setExplorerMenu = (enabled: boolean) => this.call<void>('settings.setExplorerMenu', { enabled })
  setKeepAwake = (mode: KeepAwakeMode) => this.call<{ ok: boolean; message: string; mode: KeepAwakeMode }>('settings.setKeepAwake', { mode })
  getPersona = () => this.call<PersonaInfo>('persona.get')
  setPersona = (update: PersonaUpdate) =>
    this.call<{ ok: boolean; message: string; persona: PersonaInfo | null }>('persona.set', update as Record<string, unknown>)
  setProxy = (mode: string, url: string, user: string, password: string) =>
    this.call<{ ok: boolean; message: string }>('settings.setProxy', { mode, url, user, password })
  testProxy = () => this.call<{ ok: boolean; message: string }>('settings.testProxy')
  storageInfo = () => this.call<StorageInfo>('storage.info')
  openDataFolder = () => this.call<void>('storage.openDataFolder')
  listShortcuts = () => this.call<ShortcutInfo[]>('shortcuts.list')
  setShortcut = (id: string, binding: string) =>
    this.call<{ ok: boolean; reason: string; conflictsWith?: string; shortcuts?: ShortcutInfo[] }>('shortcuts.set', { id, binding })
  resetShortcuts = (id?: string) => this.call<ShortcutInfo[]>('shortcuts.reset', { id })

  /** 安全中心：读取全部条目 / 改一项（锁住的会被宿主拒绝） */
  securitySettings = () => this.call<SecurityItem[]>('security.settings', {})
  setSecurityItem = (key: string, value: boolean | number) =>
    this.call<{ ok: boolean; message: string; items: SecurityItem[] }>('security.setItem', { key, value })
  openBackupFolder = () => this.call<void>('security.openBackups', {})

  /** 语音输入：开始录音 / 停止并转写 / 放弃 */
  startSpeech = () => this.call<{ ok: boolean; reason?: string; message?: string }>('speech.start', {})
  stopSpeech = (language: string) => this.call<SpeechResult>('speech.stop', { language })
  cancelSpeech = () => this.call<void>('speech.cancel', {})

  /** 用系统默认应用打开 */
  launchFile = (path: string) => this.call<{ ok: boolean; message: string }>('files.launch', { path })
  /** 在资源管理器中定位 */
  revealFile = (path: string) => this.call<{ ok: boolean; message: string }>('files.reveal', { path })
  /** 读取预览内容 */
  previewFile = (path: string) => this.call<PreviewDoc>('files.preview', { path }, undefined, 40_000)
  /** 告诉宿主当前打开的是哪个任务（决定要不要弹系统通知） */
  setActiveConversation = (id: string | null) => this.call<void>('ui.activeConversation', { id })
  securityEvents = (decision = '', limit?: number) => this.call<SecurityEvent[]>('security.list', { decision, limit })
  clearSecurityEvents = () => this.call<void>('security.clear')
  /** 把本机安全记录导出成 CSV（宿主弹另存为对话框） */
  exportSecurityEvents = (ids?: number[]) => this.call<ExportResult>('security.export', ids ? { ids } : {}, undefined, 0)
  networkAllowlist = () => this.call<NetworkAllowlist>('security.network')
  listApprovals = () => this.call<ApprovalInfo[]>('approvals.list')
  revokeApproval = (id: string) => this.call<void>('approvals.revoke', { id })
  clearApprovals = () => this.call<void>('approvals.clear')

  /** 深度思考 / 联网开关（写本地，下一轮生效） */
  setChatPrefs = (prefs: { deepThinking?: boolean; webSearch?: boolean }) =>
    this.call<void>('settings.setChatPrefs', prefs)

  // ---------- 对话 ----------
  send = (conversationId: string, text: string, attachments: AttachmentRef[], messageId: string) =>
    this.call<{ pending?: boolean } | null>('chat.send', { conversationId, text, attachments, messageId })
  regenerate = (conversationId: string) => this.call<void>('chat.regenerate', { conversationId })
  editMessage = (conversationId: string, messageId: string, text: string, newMessageId: string) =>
    this.call<void>('chat.edit', { conversationId, messageId, text, newMessageId })
  feedback = (conversationId: string, id: string, value: number | null) =>
    this.call<void>('message.feedback', { conversationId, id, value })
  stop = (conversationId: string) => this.call<void>('chat.stop', { conversationId })
  /** 运行中又发一句。返回 false：已经不在运行了，照常发送 */
  interject = (conversationId: string, text: string, mode: InterjectMode, messageId: string) =>
    this.call<boolean>('chat.interject', { conversationId, text, mode, messageId })
  /** 把 AI 浏览器窗口叫到前面 */
  showBrowser = () => this.call<void>('browser.show')
  /** 侧聊：顺便问一句，读任务对话、不写回。答案通过 sidechat.delta / done / error 事件推回来 */
  askSide = (conversationId: string, requestId: string, question: string, history: { role: string; content: string }[]) =>
    this.call<void>('sidechat.ask', { conversationId, requestId, question, history })
  stopSide = (requestId: string) => this.call<void>('sidechat.stop', { requestId })
  /** 终端：这个对话里 AI 执行过的命令（带输出） */
  listTerminal = (conversationId: string) => this.call<TerminalRun[]>('terminal.list', { conversationId })
  /** 快捷指令：IT 发布的 + 自己的 */
  listCommands = () => this.call<{ it: SlashCommand[]; mine: SlashCommand[] }>('commands.list', {}, undefined, 15_000)
  listPlugins = () => this.call<{ ok: boolean; items: PluginInfo[] }>('plugins.list', {}, undefined, 15_000)
  saveCommands = (items: SlashCommand[]) => this.call<{ ok: boolean; message: string }>('commands.save', { items })
  /** 右侧「文件」标签：列工作区里的一个文件夹（path 是相对工作区的路径，空 = 工作区本身） */
  listWorkspace = (conversationId: string, path: string) => this.call<WorkspaceListing>('workspace.list', { conversationId, path })
  // 仓库和 PR（开发者工具开着时）
  repoInfo = (conversationId: string) => this.call<RepoInfo | null>('repo.info', { conversationId })
  repoBranches = (conversationId: string) => this.call<string[]>('repo.branches', { conversationId })
  repoBranch = (conversationId: string, name: string, create: boolean) =>
    this.call<{ ok: boolean; message: string }>('repo.branch', { conversationId, name, create }, undefined, 60_000)
  /** 聊天里 PR / Issue 链接的悬停卡片；开发者工具没开、gh 查不到时返回 null */
  refCard = (url: string) => this.call<RefCard | null>('repo.card', { url }, undefined, 30_000)
  repoOpen = (conversationId: string, what: 'repo' | 'branch' | 'pr', id = '') => this.call<boolean>('repo.open', { conversationId, what, id })
  // 沙箱任务（本机 Docker）
  sandboxStatus = (conversationId: string) => this.call<SandboxStatus>('sandbox.status', { conversationId }, undefined, 30_000)
  /** 关的时候要删容器，可能要几秒 */
  setSandbox = (conversationId: string, on: boolean) =>
    this.call<{ ok: boolean; message: string }>('sandbox.set', { conversationId, on }, undefined, 60_000)
  prList = (conversationId: string) => this.call<PrWatchView[]>('pr.list', { conversationId })
  prCheck = (id: string) => this.call<PrWatchView | null>('pr.check', { id }, undefined, 120_000)
  prStop = (id: string) => this.call<PrWatchView | null>('pr.stop', { id })
  /** 会话信息：这个对话订阅的外部事件（本期只有盯着的 PR） */
  subscriptions = (conversationId: string) => this.call<SubscriptionView[]>('subscriptions.list', { conversationId })
  cancelSubscription = (conversationId: string, id: string) => this.call<boolean>('subscriptions.cancel', { conversationId, id })
  prAutoFix = (id: string, on: boolean) => this.call<PrWatchView | null>('pr.autoFix', { id, on })
  searchWorkspace = (conversationId: string, query: string) =>
    this.call<{ truncated: boolean; entries: WorkspaceEntry[] }>('workspace.search', { conversationId, query }, undefined, 30_000)
  /** 工作区里的文件加进输入框（成为附件） */
  attachWorkspaceFile = (conversationId: string, path: string) => this.call<AttachmentRef | null>('workspace.attach', { conversationId, path })
  /** 文档画布：员工从对话的文件里打开一篇 Markdown（只认这个对话改过的、或工作区里的文件） */
  openDocument = (conversationId: string, path: string) => this.call<{ ok: boolean }>('document.open', { conversationId, path })
  loadDocument = (conversationId: string, path: string) => this.call<DocumentInfo>('document.load', { conversationId, path })
  saveDocument = (conversationId: string, path: string, text: string, version: string) =>
    this.call<DocumentSaveResult>('document.save', { conversationId, path, text, version })
  /** 导出 Word：宿主弹「另存为」，按块写成 .docx */
  exportDocx = (path: string, doc: DocModel) =>
    this.call<{ ok: boolean; cancelled: boolean; path: string; message: string }>('document.exportDocx', { path, doc }, undefined, 120_000)
  /** 停掉终端里还在跑的命令 */
  stopTerminal = (conversationId: string, runId: string) => this.call<boolean>('terminal.stop', { conversationId, runId })
  /** 清掉已经结束的命令记录 */
  clearTerminal = (conversationId: string) => this.call<void>('terminal.clear', { conversationId })
  /** 这个对话里每一轮任务改动的文件 */
  listChanges = (conversationId: string) => this.call<ChangeTurn[]>('changes.list', { conversationId })
  /** 把某一轮的改动退回到那一轮之前；paths 为空表示全部 */
  revertChanges = (conversationId: string, turnId: string, paths?: string[]) =>
    this.call<RevertOutcome>('changes.revert', { conversationId, turnId, paths }, undefined, 60_000)
  /** 长任务模式开关。IT 关掉了这个功能时打不开（返回 false） */
  setLongMode = (id: string, on: boolean) => this.call<boolean>('conversation.setLongMode', { id, on })
  /** 输入框 /export：宿主把对话存成 Markdown 放进工作区，返回文件路径 */
  exportConversation = (id: string) => this.call<{ ok: boolean; path: string; message: string }>('conversation.exportMarkdown', { id }, undefined, 60_000)
  /** 从某条消息分叉出新对话（复制到那条为止） */
  forkConversation = (id: string, messageId: string, title: string) => this.call<Conversation | null>('conversation.fork', { id, messageId, title })
  /** 计划模式（先出方案）：只在这次打开程序期间有效 */
  setPlanMode = (id: string, on: boolean) => this.call<boolean>('conversation.setPlanMode', { id, on })
  /** 任务中心：正在跑的、上次没跑完的、最近跑完的 */
  listRuns = () => this.call<RunsList>('runs.list')
  /** 接着做上次没做完的任务 */
  resumeRun = (conversationId: string) => this.call<void>('runs.resume', { conversationId })
  /** 上次没做完的任务：不用了，不再提示 */
  dismissRun = (conversationId: string) => this.call<void>('runs.dismiss', { conversationId })
  /** 暂停：正在做的这一步做完后停住；继续后接着往下走 */
  pause = (conversationId: string) => this.call<boolean>('chat.pause', { conversationId })
  resume = (conversationId: string) => this.call<boolean>('chat.resume', { conversationId })
  pendingInputs = (conversationId: string) => this.call<PendingInput[]>('chat.pending', { conversationId })
  /** AI 读到之前改一句；已经被读走返回 false */
  updatePending = (conversationId: string, id: string, text: string, mode?: 'steer' | 'queue') =>
    this.call<boolean>('chat.pending.update', { conversationId, id, text, mode })
  removePending = (conversationId: string, id: string) => this.call<boolean>('chat.pending.remove', { conversationId, id })
  confirm = (requestId: string, choice: ConfirmChoice) => this.call<void>('tool.confirm', { requestId, choice })
  /** 会话信息面板：最近一次回答时的上下文用量（没回答过返回 null） */
  contextUsage = (conversationId: string) => this.call<ContextUsage | null>('context.usage', { conversationId })
  /** 手动整理对话：较早的内容压成摘要 */
  /** focus：/compact 后面附的「重点保留什么」，可选 */
  compactContext = (conversationId: string, focus?: string) =>
    this.call<{ ok: boolean; message: string }>('context.compact', focus ? { conversationId, focus } : { conversationId }, undefined, 0)
  /** 回答 AI 的选项提问；answers 为 null 表示跳过 */
  answerAsk = (requestId: string, answers: AskAnswerInput[] | null) =>
    this.call<void>('tool.answer', answers ? { requestId, answers } : { requestId, skip: true })
  /** 对话里派给专家的子任务（不含执行记录）：打开对话、页面重载时恢复专家任务卡片 */
  subtasksList = (conversationId: string) => this.call<SubtaskDto[]>('subtasks.list', { conversationId })
  /** 一个子任务的完整结果（含专家的执行记录） */
  subtasksGet = (id: string) => this.call<SubtaskDto>('subtasks.get', { id })

  // ---------- 文件 ----------
  pickFiles = () => this.call<AttachmentRef[]>('files.pick', {}, undefined, 0)
  /** 拖入窗口的文件：WebView2 通过 additionalObjects 把真实路径交给宿主 */
  addDroppedFiles = (files: File[]) => this.call<AttachmentRef[]>('files.dropped', {}, files)

  // ---------- 资料库 ----------
  libraryList = (q: { tab: string; folderId?: string | null; conversationId?: string | null; search?: string; kind?: string; sort?: string; hidden?: boolean }) =>
    this.call<{ items: LibraryItem[]; folders: LibraryFolder[] }>('library.list', q)
  // 先弹文件选择框，再上传：等用户选文件的时间不限
  libraryUpload = (folderId: string | null, accept?: 'image') => this.call<LibraryItem[]>('library.upload', { folderId, accept }, undefined, 0)
  libraryAddDropped = (files: File[], folderId: string | null) => this.call<LibraryItem[]>('library.addDropped', { folderId }, files, 10 * 60_000)
  libraryAddNote = (title: string, text: string, folderId: string | null) => this.call<LibraryItem>('library.addNote', { title, text, folderId })
  libraryUpdateNote = (id: string, text: string) => this.call<boolean>('library.updateNote', { id, text })
  libraryRename = (id: string, name: string) => this.call<boolean>('library.rename', { id, name })
  libraryFavorite = (ids: string[], favorite: boolean) => this.call<number>('library.favorite', { ids, favorite })
  libraryMove = (ids: string[], folderId: string | null) => this.call<number>('library.move', { ids, folderId })
  libraryDelete = (ids: string[]) => this.call<number>('library.delete', { ids })
  libraryRestore = (ids: string[]) => this.call<number>('library.restore', { ids })
  libraryPurge = (ids: string[]) => this.call<number>('library.purge', { ids }, undefined, 60_000)
  libraryEmptyTrash = () => this.call<number>('library.emptyTrash', {}, undefined, 60_000)
  libraryCreateFolder = (name: string) => this.call<LibraryFolder>('library.createFolder', { name })
  libraryRenameFolder = (id: string, name: string) => this.call<boolean>('library.renameFolder', { id, name })
  libraryDeleteFolder = (id: string) => this.call<boolean>('library.deleteFolder', { id })
  // 弹另存为对话框后再复制文件，不限时
  libraryDownload = (ids: string[]) => this.call<number>('library.download', { ids }, undefined, 0)
  libraryShare = (ids: string[]) => this.call<number>('library.share', { ids })
  channels = () => this.call<ChannelsInfo>('channels.get')
  channelBindCode = () => this.call<{ code: string; expiresAt: string }>('channels.bindCode')
  channelUnbind = (id: number) => this.call<void>('channels.unbind', { id })
  mobile = () => this.call<MobileInfo>('mobile.get')
  mobilePairCode = () => this.call<MobilePairCode>('mobile.pairCode')
  mobileUnpair = (id: number) => this.call<void>('mobile.unpair', { id })
  libraryReveal = (id: string) => this.call<{ ok: boolean; message: string }>('library.reveal', { id })
  libraryPreview = (id: string) => this.call<PreviewDoc>('library.preview', { id }, undefined, 40_000)
  libraryAttachments = (ids: string[]) => this.call<AttachmentRef[]>('library.attachments', { ids })
  /** 粘贴的截图等没有路径的内容，以 base64 交给宿主保存为临时文件 */
  saveBlob = (fileName: string, mime: string, base64: string) =>
    this.call<AttachmentRef>('files.saveBlob', { fileName, mime, base64 })
  /** 文件不存在时宿主返回 ok=false 和原因 */
  openPath = (path: string) => this.call<{ ok: boolean; message?: string } | void>('files.open', { path })

  // ---------- 自动更新 ----------
  updateState = () => this.call<UpdateInfo>('update.state')
  /** 立刻装上。宿主会在更新器起来之后让程序退出 */
  applyUpdate = () => this.call<{ ok: boolean; message: string }>('update.apply')
  /** 设置里「检查更新」：等服务器回话，告诉用户结果。下载在后台继续 */
  /** 退出登录：服务端作废令牌，宿主清掉本机令牌后重启到登录窗口 */
  logout = () => this.call<void>('account.logout')
  /** 网络检测：最近一次的结果（没测过为 null）/ 重新检测 / 报告目录 / 导出 / 定位 */
  lastNetworkReport = () => this.call<NetworkReport | null>('network.last')
  diagnoseNetwork = () => this.call<NetworkReport>('network.diagnose', {}, undefined, 90_000)
  openNetworkReports = () => this.call<void>('network.openReports')
  revealNetworkReport = () => this.call<void>('network.revealReport')
  exportNetworkReport = () => this.call<{ ok: boolean; path: string }>('network.exportReport', {}, undefined, 0)
  checkUpdate = () => this.call<UpdateCheckResult>('update.check')
  /** 意见反馈。images 是 data: URL；logs 为 true 时宿主把最近几天的日志打包一起传 */
  submitFeedback = (req: { content: string; images: string[]; logs: boolean }) =>
    this.call<{ ok: boolean; message: string }>('feedback.submit', { ...req }, undefined, 90_000)
  /** 服务器上 IT 维护的用户协议 / 隐私政策（Markdown） */
  legal = (kind: 'terms' | 'privacy') => this.call<{ title: string; content: string } | null>('legal.get', { kind })
}

export const bridge = new Bridge()
