/**
 * 与 WPF 宿主通信。
 *
 * 请求：页面 → 宿主  { kind: 'request', id, method, params }
 * 响应：宿主 → 页面  { kind: 'response', id, ok, result | error }
 * 事件：宿主 → 页面  { kind: 'event', event: HostEvent }
 *
 * 在普通浏览器中运行（npm run dev）时，使用 mockHost 模拟宿主。
 */
import type {
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
  ConfirmChoice,
  Conversation,
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
} from './types'
import { createMockHost } from './mockHost'

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

  private call<T>(method: string, params: Record<string, unknown> = {}, files?: File[], timeoutMs = 20000): Promise<T> {
    const id = `r${++this.seq}`
    const message = { kind: 'request', id, method, params }
    return new Promise<T>((resolve, reject) => {
      // 宿主 20 秒未响应视为失败，避免界面一直空白；要等浏览器登录这类调用自己给更长的时间
      const timer = window.setTimeout(() => {
        if (this.pending.delete(id)) reject(new Error(`宿主未响应：${method}`))
      }, timeoutMs)
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
  setLearning = (enabled: boolean) => this.call<void>('settings.setLearning', { enabled })
  setMaxSteps = (value: number) => this.call<number>('settings.setMaxSteps', { value })
  setWindowBackground = (color: string) => this.call<void>('window.setBackground', { color })
  setNotifications = (enabled: boolean) =>
    this.call<{ ok: boolean; message: string; enabled: boolean }>('settings.setNotifications', { enabled })

  // ---------- 记忆 ----------
  memoryOverview = () => this.call<MemoryOverview>('memory.list')
  deleteMemory = (id: string) => this.call<void>('memory.delete', { id })
  pinMemory = (id: string, pinned: boolean) => this.call<boolean>('memory.pin', { id, pinned })
  addMemory = (kind: MemoryKind, text: string) => this.call<MemoryAddResult>('memory.add', { kind, text })
  // 整理要请求模型好几次，宿主那边最多等 8 分钟，这里多留一点
  consolidateMemory = () => this.call<MemoryConsolidateResult>('memory.consolidate', {}, undefined, 9 * 60_000)
  deleteEpisode = (id: string) => this.call<void>('episodes.delete', { id })
  deleteLearnedSkill = (name: string) => this.call<void>('skills.deleteLearned', { name })

  // ---------- 会话 ----------
  listConversations = (query = '', trash = false) =>
    this.call<Conversation[]>('conversations.list', { query, trash })
  createConversation = (mode: Mode, modelId: number | null, workspace: string | null, permission: Permission) =>
    this.call<Conversation>('conversation.create', { mode, modelId, workspace, permission })
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

  // ---------- 模型与技能 ----------
  listModels = (refresh = false) => this.call<ModelInfo[]>('models.list', { refresh })
  setDefaultModel = (modelId: number | null) => this.call<void>('settings.setDefaultModel', { modelId })
  listSkills = () => this.call<SkillInfo[]>('skills.list')
  /** 不传路径时弹文件夹选择框 */
  openSkillsFolder = (path?: string) => this.call<void>('skills.openFolder', path ? { path } : {})
  setSkillEnabled = (name: string, enabled: boolean) => this.call<SkillActionResult>('skills.setEnabled', { name, enabled })
  uninstallSkill = (name: string) => this.call<SkillActionResult>('skills.uninstall', { name })
  /** 选择 zip 文件安装；取消选择返回 null */
  inspectSkill = (path?: string) => this.call<{ path: string; inspection: SkillInspection } | null>('skills.inspect', path ? { path } : {})
  installSkill = (path?: string) => this.call<SkillInstallOutcome | null>('skills.install', path ? { path } : {})
  installSkillFolder = (path?: string) => this.call<SkillInstallOutcome | null>('skills.installFolder', path ? { path } : {})
  installSkillFromUrl = (url: string) => this.call<SkillInstallOutcome>('skills.installFromUrl', { url })
  skillLibrary = () => this.call<LibrarySkill[]>('skills.library')

  // ---------- MCP 连接器（和技能是两组调用，互不影响） ----------
  listMcp = (refresh = false) => this.call<McpVendor[]>('mcp.list', { refresh })
  /** 可能要等浏览器登录，最长几分钟；结果（成功或原因）在返回值里 */
  connectMcp = (id: string, values: Record<string, string>) =>
    this.call<{ ok: boolean; message: string; vendor: McpVendor | null }>('mcp.connect', { id, values }, undefined, 6 * 60_000)
  cancelMcp = (id: string) => this.call<void>('mcp.cancel', { id })
  disconnectMcp = (id: string, forget: boolean) => this.call<McpVendor | null>('mcp.disconnect', { id, forget })
  installSkillFromLibrary = (name: string) => this.call<SkillInstallOutcome>('skills.installFromLibrary', { name })

  // ---------- 工作区与权限 ----------
  listWorkspaces = () => this.call<WorkspaceInfo[]>('workspaces.list')
  /** 不传 path 时弹出文件夹选择框；取消返回 null */
  addWorkspace = (path?: string) => this.call<string | null>('workspaces.add', path ? { path } : {})
  removeWorkspace = (path: string) => this.call<WorkspaceInfo[]>('workspaces.remove', { path })
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
  previewFile = (path: string) => this.call<PreviewDoc>('files.preview', { path })
  /** 告诉宿主当前打开的是哪个任务（决定要不要弹系统通知） */
  setActiveConversation = (id: string | null) => this.call<void>('ui.activeConversation', { id })
  securityEvents = (decision = '', limit?: number) => this.call<SecurityEvent[]>('security.list', { decision, limit })
  clearSecurityEvents = () => this.call<void>('security.clear')
  /** 把本机安全记录导出成 CSV（宿主弹另存为对话框） */
  exportSecurityEvents = (ids?: number[]) => this.call<ExportResult>('security.export', ids ? { ids } : {})
  networkAllowlist = () => this.call<NetworkAllowlist>('security.network')
  listApprovals = () => this.call<ApprovalInfo[]>('approvals.list')
  revokeApproval = (id: string) => this.call<void>('approvals.revoke', { id })
  clearApprovals = () => this.call<void>('approvals.clear')

  // ---------- 对话 ----------
  send = (conversationId: string, text: string, attachments: AttachmentRef[], messageId: string) =>
    this.call<void>('chat.send', { conversationId, text, attachments, messageId })
  regenerate = (conversationId: string) => this.call<void>('chat.regenerate', { conversationId })
  editMessage = (conversationId: string, messageId: string, text: string, newMessageId: string) =>
    this.call<void>('chat.edit', { conversationId, messageId, text, newMessageId })
  feedback = (conversationId: string, id: string, value: number | null) =>
    this.call<void>('message.feedback', { conversationId, id, value })
  stop = (conversationId: string) => this.call<void>('chat.stop', { conversationId })
  confirm = (requestId: string, choice: ConfirmChoice) => this.call<void>('tool.confirm', { requestId, choice })

  // ---------- 文件 ----------
  pickFiles = () => this.call<AttachmentRef[]>('files.pick')
  /** 拖入窗口的文件：WebView2 通过 additionalObjects 把真实路径交给宿主 */
  addDroppedFiles = (files: File[]) => this.call<AttachmentRef[]>('files.dropped', {}, files)

  // ---------- 资料库 ----------
  libraryList = (q: { tab: string; folderId?: string | null; search?: string; kind?: string; sort?: string; hidden?: boolean }) =>
    this.call<{ items: LibraryItem[]; folders: LibraryFolder[] }>('library.list', q)
  libraryUpload = (folderId: string | null, accept?: 'image') => this.call<LibraryItem[]>('library.upload', { folderId, accept }, undefined, 10 * 60_000)
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
  libraryDownload = (ids: string[]) => this.call<number>('library.download', { ids }, undefined, 10 * 60_000)
  libraryShare = (ids: string[]) => this.call<number>('library.share', { ids })
  libraryReveal = (id: string) => this.call<{ ok: boolean; message: string }>('library.reveal', { id })
  libraryPreview = (id: string) => this.call<PreviewDoc>('library.preview', { id }, undefined, 40_000)
  libraryAttachments = (ids: string[]) => this.call<AttachmentRef[]>('library.attachments', { ids })
  /** 粘贴的截图等没有路径的内容，以 base64 交给宿主保存为临时文件 */
  saveBlob = (fileName: string, mime: string, base64: string) =>
    this.call<AttachmentRef>('files.saveBlob', { fileName, mime, base64 })
  openPath = (path: string) => this.call<void>('files.open', { path })

  // ---------- 自动更新 ----------
  updateState = () => this.call<UpdateInfo>('update.state')
  /** 立刻装上。宿主会在更新器起来之后让程序退出 */
  applyUpdate = () => this.call<{ ok: boolean; message: string }>('update.apply')
  checkUpdate = () => this.call<void>('update.check')
}

export const bridge = new Bridge()
