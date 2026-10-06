import { computed, reactive, watch } from 'vue'
import { bridge } from './bridge'
import { applyLanguage, i18n } from './i18n'
import type {
  AppInfo,
  AttachmentRef,
  ConfirmChoice,
  Conversation,
  HostEvent,
  Mode,
  ModelInfo,
  Permission,
  PlanItem,
  ScheduledTask,
  SkillInfo,
  Theme,
  ToolActivity,
  UiLanguage,
  UiMessage,
  OutputFile,
  PreviewDoc,
  UsageStats,
  WorkspaceInfo,
} from './types'

interface ConversationState {
  messages: UiMessage[]
  loaded: boolean
  busy: boolean
  /** 正在流式输出的回答 */
  draft: { content: string; reasoning: string } | null
  tools: Record<string, ToolActivity>
  plan: PlanItem[]
  notice: { kind: 'stopped' | 'error' | 'maxSteps' | 'tooManyFailures'; text?: string } | null
}

export const state = reactive({
  app: null as AppInfo | null,
  conversations: [] as Conversation[],
  trash: [] as Conversation[],
  showTrash: false,
  query: '',
  currentId: null as string | null,
  byId: {} as Record<string, ConversationState>,
  pending: [] as AttachmentRef[],
  toast: '' as string,
  settingsOpen: false,
  skillsOpen: false,
  /** 侧栏任务列表按模式筛选 */
  filter: 'all' as 'all' | Mode,
  models: [] as ModelInfo[],
  skills: [] as SkillInfo[],
  workspaces: [] as WorkspaceInfo[],
  memoryOpen: false,
  usageOpen: false,
  schedulesOpen: false,
  schedules: [] as ScheduledTask[],
  maximized: false,
  /** 正在压缩上下文的会话（界面上显示进度条） */
  compacting: null as { conversationId: string; percent: number; phase: string; messages: number } | null,
  /** 今日用量，用于输入框上方的提醒条 */
  usage: null as UsageStats | null,
  /** 用户关掉提醒条后，这一档不再提醒 */
  usageDismissedAt: 0,
  /** 右侧分屏预览 */
  preview: null as { file: OutputFile; doc: PreviewDoc | null; loading: boolean; error: string } | null,
  /** 分屏宽度（像素），拖拽后记住 */
  previewWidth: readPreviewWidth(),
  /** 产出文件后自动打开分屏 */
  autoPreview: true,
})

const PreviewWidthKey = 'flyknit.previewWidth'
export const MinPreviewWidth = 320
export const MaxPreviewRatio = 0.72

function readPreviewWidth(): number {
  try {
    const saved = Number(localStorage.getItem(PreviewWidthKey))
    if (Number.isFinite(saved) && saved >= MinPreviewWidth) return saved
  } catch {
    // 隐私模式下读不到，用默认值就好
  }
  return 520
}

export function setPreviewWidth(px: number) {
  const max = Math.max(MinPreviewWidth, window.innerWidth * MaxPreviewRatio)
  state.previewWidth = Math.round(Math.min(Math.max(px, MinPreviewWidth), max))
  try {
    localStorage.setItem(PreviewWidthKey, String(state.previewWidth))
  } catch {
    // 存不住就算了，只是下次要重新拖
  }
}

/** 在右侧分屏里打开一个文件 */
export async function openPreview(file: OutputFile) {
  state.preview = { file, doc: null, loading: true, error: '' }
  try {
    const doc = await bridge.previewFile(file.path)
    if (state.preview?.file.path !== file.path) return // 用户已经点了别的文件
    state.preview = { file, doc, loading: false, error: doc.error ?? '' }
  } catch (e) {
    if (state.preview?.file.path !== file.path) return
    state.preview = { file, doc: null, loading: false, error: String(e) }
  }
}

export function closePreview() {
  state.preview = null
}

export async function launchFile(file: OutputFile) {
  const r = await bridge.launchFile(file.path).catch(() => ({ ok: false, message: '打开失败' }))
  if (!r.ok && r.message) toast(r.message)
}

export async function revealFile(file: OutputFile) {
  const r = await bridge.revealFile(file.path).catch(() => ({ ok: false, message: '打开失败' }))
  if (!r.ok && r.message) toast(r.message)
}

/** 今日额度用掉的百分比；管理员没设上限时返回 0 */
export const usagePercent = computed(() => {
  const u = state.usage
  if (!u || !u.dailyLimit) return 0
  return Math.min(100, Math.round((u.todayTokens / u.dailyLimit) * 100))
})

/** 用掉 90% 以上才提醒，且同一档只提醒一次 */
export const showUsageWarning = computed(
  () => usagePercent.value >= 90 && usagePercent.value > state.usageDismissedAt,
)

export function dismissUsageWarning() {
  state.usageDismissedAt = usagePercent.value
}

/** 每次任务结束后刷新一次，别为了这个提醒条反复打服务端 */
export async function refreshUsage() {
  state.usage = await bridge.usageStats().catch(() => null)
}

/** 消息 ID 由界面生成，与宿主数据库保持一致（编辑、重新生成时需要） */
export function newMessageId(): string {
  const c = window.crypto
  if (c?.randomUUID) return c.randomUUID().replace(/-/g, '')
  return Array.from({ length: 32 }, () => Math.floor(Math.random() * 16).toString(16)).join('')
}

export const current = computed(() => state.conversations.find((c) => c.id === state.currentId) ?? null)
export const currentState = computed(() => (state.currentId ? convState(state.currentId) : null))

function convState(id: string): ConversationState {
  if (!state.byId[id]) {
    state.byId[id] = { messages: [], loaded: false, busy: false, draft: null, tools: {}, plan: [], notice: null }
  }
  return state.byId[id]
}

let toastTimer: number | undefined
export function toast(text: string) {
  state.toast = text
  clearTimeout(toastTimer)
  toastTimer = window.setTimeout(() => (state.toast = ''), 3200)
}

function fail(e: unknown) {
  toast(i18n.global.t('error.generic', { msg: e instanceof Error ? e.message : String(e) }))
}

// ---------- 主题 ----------
const media = window.matchMedia('(prefers-color-scheme: dark)')
export function applyTheme(theme: Theme) {
  const dark = theme === 'dark' || (theme === 'system' && media.matches)
  document.documentElement.dataset.theme = dark ? 'dark' : 'light'
}
media.addEventListener('change', () => state.app && applyTheme(state.app.theme))

// ---------- 初始化 ----------
export async function init() {
  bridge.on(onHostEvent)
  // 宿主要知道界面当前打开的是哪个任务，才能判断系统通知该不该弹
  watch(
    () => state.currentId,
    (id) => {
      void bridge.setActiveConversation(id).catch(() => {})
      state.preview = null // 换任务时收起分屏，免得看着上一个任务的产出
    },
  )
  const app = await bridge.init()
  state.app = app
  applyLanguage(app.uiLanguage)
  applyTheme(app.theme)
  draftMode.modelId = app.defaultModelId
  draftMode.workspace = app.defaultWorkspace
  draftMode.permission = app.defaultPermission
  state.workspaces = app.workspaces ?? []
  state.maximized = app.maximized ?? false
  await refreshList()
  void loadModels()
  void loadSkills()
  void refreshUsage()
}

export async function loadModels(refresh = false) {
  try {
    state.models = await bridge.listModels(refresh)
    // 默认模型已被管理员停用时回到自动
    if (draftMode.modelId !== null && !state.models.some((m) => m.id === draftMode.modelId)) draftMode.modelId = null
  } catch (e) {
    fail(e)
  }
}

export async function loadSchedules() {
  try {
    state.schedules = await bridge.listSchedules()
  } catch (e) {
    fail(e)
  }
}

export async function loadSkills() {
  try {
    state.skills = await bridge.listSkills()
  } catch {
    state.skills = []
  }
}

/** 当前对话（或新任务草稿）使用的模型 */
export const selectedModelId = computed<number | null>(() =>
  current.value ? current.value.modelId : draftMode.modelId,
)

export async function selectModel(modelId: number | null) {
  const c = current.value
  if (c) {
    c.modelId = modelId
    await bridge.setModel(c.id, modelId).catch(fail)
  } else {
    draftMode.modelId = modelId
  }
  // 最近一次选择作为以后新任务的默认模型
  if (state.app) state.app.defaultModelId = modelId
  await bridge.setDefaultModel(modelId).catch(fail)
}

/** 当前任务用到的技能（从 load_skill 调用里取），显示在标题旁边 */
export const usedSkills = computed<string[]>(() => {
  const s = currentState.value
  if (!s) return []
  const names = new Set<string>()
  for (const t of Object.values(s.tools)) {
    if (t.name === 'load_skill' && t.summary) names.add(t.summary)
  }
  for (const m of s.messages) {
    for (const c of m.toolCalls ?? []) {
      if (c.name !== 'load_skill') continue
      try {
        const n = JSON.parse(c.arguments)?.name
        if (n) names.add(String(n))
      } catch {
        // 参数不是合法 JSON 时忽略
      }
    }
  }
  return [...names]
})

/** 侧栏状态：运行中 / 等待确认 / 空闲 */
export function runStatus(id: string): 'running' | 'waiting' | 'idle' {
  const s = state.byId[id]
  if (!s?.busy) return 'idle'
  return Object.values(s.tools).some((t) => t.state === 'waiting') ? 'waiting' : 'running'
}

export async function refreshList() {
  try {
    state.conversations = await bridge.listConversations(state.query)
    if (state.showTrash) state.trash = await bridge.listConversations('', true)
  } catch (e) {
    fail(e)
  }
}

export async function setLanguage(lang: UiLanguage) {
  applyLanguage(lang)
  if (state.app) state.app.uiLanguage = lang
  await bridge.setLanguage(lang).catch(fail)
}

export async function setTheme(theme: Theme) {
  if (state.app) state.app.theme = theme
  applyTheme(theme)
  await bridge.setTheme(theme).catch(fail)
}

// ---------- 会话操作 ----------
export async function openConversation(id: string) {
  state.currentId = id
  const s = convState(id)
  if (!s.loaded) {
    try {
      const messages = await bridge.loadMessages(id)
      s.messages = messages
      s.tools = rebuildTools(messages)
      s.loaded = true
    } catch (e) {
      fail(e)
    }
  }
}

/** 新建任务只在本地生成草稿，发送第一条消息时才真正创建，避免产生空会话。 */
export function newConversation(mode?: Mode) {
  state.currentId = null
  state.pending = []
  if (mode) draftMode.mode = mode
}

export const draftMode = reactive({
  mode: 'agent' as Mode,
  translateFrom: 'auto',
  translateTo: 'vi',
  modelId: null as number | null,
  workspace: '' as string,
  permission: 'workspace' as Permission,
})

// ---------- 工作区与权限 ----------
/** 当前任务（或新任务草稿）的工作区 */
export const selectedWorkspace = computed<string>(() => current.value?.workspace || draftMode.workspace || state.app?.defaultWorkspace || '')
export const selectedPermission = computed<Permission>(() => current.value?.permission ?? draftMode.permission)

export async function loadWorkspaces() {
  try {
    state.workspaces = await bridge.listWorkspaces()
  } catch (e) {
    fail(e)
  }
}

export async function selectWorkspace(path: string) {
  const c = current.value
  if (c) {
    c.workspace = path
    await bridge.setWorkspace(c.id, path).catch(fail)
  } else {
    draftMode.workspace = path
  }
  // 最近一次选择作为以后新任务的默认工作区
  if (state.app) state.app.defaultWorkspace = path
  await bridge.setDefaultWorkspace(path).catch(fail)
}

/** 弹出文件夹选择框添加工作区，添加后直接切换过去 */
export async function addWorkspace() {
  try {
    const path = await bridge.addWorkspace()
    if (!path) return
    await loadWorkspaces()
    await selectWorkspace(path)
  } catch (e) {
    fail(e)
  }
}

export async function removeWorkspace(path: string) {
  try {
    state.workspaces = await bridge.removeWorkspace(path)
    if (selectedWorkspace.value === path) {
      const fallback = state.workspaces.find((w) => w.isDefault)?.path ?? ''
      await selectWorkspace(fallback)
    }
  } catch (e) {
    fail(e)
  }
}

/** 切换权限。完全权限只对当前任务生效，不会成为以后新任务的默认值。 */
export async function setPermission(permission: Permission) {
  const c = current.value
  if (c) {
    c.permission = permission
    await bridge.setPermission(c.id, permission).catch(fail)
  } else {
    draftMode.permission = permission
  }
  if (permission !== 'full') {
    if (state.app) state.app.defaultPermission = permission
    await bridge.setDefaultPermission(permission).catch(fail)
  }
}

export async function renameConversation(id: string, title: string) {
  const t = title.trim()
  const c = state.conversations.find((x) => x.id === id)
  if (!t || !c || t === c.title) return
  c.title = t
  c.titleSource = 'manual'
  await bridge.renameConversation(id, t).catch(fail)
}

export async function deleteConversation(id: string) {
  await bridge.deleteConversation(id).catch(fail)
  if (state.currentId === id) state.currentId = null
  await refreshList()
}

export async function restoreConversation(id: string) {
  await bridge.restoreConversation(id).catch(fail)
  await refreshList()
}

export async function purgeConversation(id: string) {
  await bridge.purgeConversation(id).catch(fail)
  delete state.byId[id]
  await refreshList()
}

export async function togglePin(c: Conversation) {
  c.pinned = !c.pinned
  await bridge.pinConversation(c.id, c.pinned).catch(fail)
  await refreshList()
}

/**
 * 切换模式：每种模式是独立的任务。新任务页面直接切换；
 * 在已有对话中切换时，开一个新任务（不把不同模式的消息混在同一个对话里）。
 */
export async function setMode(mode: Mode) {
  const c = current.value
  if (!c) {
    draftMode.mode = mode
    return
  }
  if (c.mode === mode) return
  if (c.messageCount === 0 && (state.byId[c.id]?.messages.length ?? 0) === 0) {
    c.mode = mode
    await bridge.setMode(c.id, mode).catch(fail)
    return
  }
  newConversation(mode)
}

export async function setTranslate(from: string, to: string) {
  const c = current.value
  if (!c) {
    draftMode.translateFrom = from
    draftMode.translateTo = to
    return
  }
  c.translateFrom = from
  c.translateTo = to
  await bridge.setTranslate(c.id, from, to).catch(fail)
}

// ---------- 发送 ----------
export async function send(text: string) {
  const body = text.trim()
  if (!body && state.pending.length === 0) return
  let id = state.currentId
  try {
    if (!id) {
      const c = await bridge.createConversation(draftMode.mode, draftMode.modelId, selectedWorkspace.value || null, draftMode.permission)
      if (draftMode.mode === 'translate') {
        await bridge.setTranslate(c.id, draftMode.translateFrom, draftMode.translateTo)
        c.translateFrom = draftMode.translateFrom
        c.translateTo = draftMode.translateTo
      }
      state.conversations.unshift(c)
      id = c.id
      state.currentId = id
      convState(id).loaded = true
    }
    const s = convState(id)
    if (s.busy) return
    const attachments = state.pending.slice()
    state.pending = []
    const messageId = newMessageId()
    s.messages.push({ id: messageId, role: 'user', content: body, attachments, createdAt: new Date().toISOString() })
    beginRun(s)
    await bridge.send(id, body, attachments, messageId)
  } catch (e) {
    if (id) {
      const s = convState(id)
      s.busy = false
      s.draft = null
      s.notice = { kind: 'error', text: e instanceof Error ? e.message : String(e) }
    } else {
      fail(e)
    }
  }
}

function beginRun(s: ConversationState) {
  s.busy = true
  s.notice = null
  s.draft = { content: '', reasoning: '' }
}

/** 从界面上移除某个位置之后的消息（以及它们的工具卡片） */
function dropFrom(s: ConversationState, index: number) {
  const removed = s.messages.splice(index)
  for (const m of removed) for (const c of m.toolCalls ?? []) delete s.tools[c.id]
}

/** 重新生成最后一个问题的回答 */
export async function regenerate() {
  const id = state.currentId
  if (!id) return
  const s = convState(id)
  if (s.busy) return
  let last = -1
  for (let i = s.messages.length - 1; i >= 0; i--) {
    if (s.messages[i].role === 'user') {
      last = i
      break
    }
  }
  if (last < 0) return
  dropFrom(s, last + 1)
  beginRun(s)
  try {
    await bridge.regenerate(id)
  } catch (e) {
    s.busy = false
    s.draft = null
    s.notice = { kind: 'error', text: e instanceof Error ? e.message : String(e) }
  }
}

/** 编辑某条用户消息后重新发送：这条消息之后的内容会被替换 */
export async function editAndResend(messageId: string, text: string) {
  const id = state.currentId
  const body = text.trim()
  if (!id || !body) return
  const s = convState(id)
  if (s.busy) return
  const index = s.messages.findIndex((m) => m.id === messageId)
  if (index < 0) return
  const original = s.messages[index]
  dropFrom(s, index)
  const newId = newMessageId()
  s.messages.push({ id: newId, role: 'user', content: body, attachments: original.attachments, createdAt: new Date().toISOString() })
  beginRun(s)
  try {
    await bridge.editMessage(id, messageId, body, newId)
  } catch (e) {
    s.busy = false
    s.draft = null
    s.notice = { kind: 'error', text: e instanceof Error ? e.message : String(e) }
  }
}

/** 赞 / 踩；再次点击同一个按钮取消 */
export async function setFeedback(m: UiMessage, value: 1 | -1) {
  const next = m.feedback === value ? null : value
  m.feedback = next
  if (!state.currentId) return
  await bridge.feedback(state.currentId, m.id, next).catch(fail)
  if (next === 1) toast(i18n.global.t('ui.feedback.thanks'))
  if (next === -1) toast(i18n.global.t('ui.feedback.learn'))
}

export async function toggleMaximize() {
  state.maximized = await bridge.toggleMaximize().catch(() => state.maximized)
}

export async function setLearning(enabled: boolean) {
  if (state.app) state.app.learning = enabled
  await bridge.setLearning(enabled).catch(fail)
}

export async function setNotifications(enabled: boolean) {
  if (state.app) state.app.notifications = enabled
  await bridge.setNotifications(enabled).catch(fail)
}

export async function stop() {
  if (state.currentId) await bridge.stop(state.currentId).catch(fail)
}

export async function answerConfirm(conversationId: string, callId: string, choice: ConfirmChoice) {
  const tool = convState(conversationId).tools[callId]
  if (!tool?.confirm) return
  const requestId = tool.confirm.requestId
  tool.confirm = undefined
  tool.state = choice === 'reject' ? 'rejected' : 'running'
  await bridge.confirm(requestId, choice).catch(fail)
}

// ---------- 附件 ----------
export async function addFiles(files: File[]) {
  if (files.length === 0) return
  try {
    const refs = await bridge.addDroppedFiles(files)
    for (const r of refs) {
      const f = files.find((x) => x.name === r.fileName)
      if (f && f.type.startsWith('image/')) r.preview = await readAsDataUrl(f)
    }
    state.pending.push(...refs)
  } catch (e) {
    fail(e)
  }
}

export async function pickFiles() {
  try {
    state.pending.push(...(await bridge.pickFiles()))
  } catch (e) {
    fail(e)
  }
}

/** 粘贴截图：没有文件路径，交给宿主保存为临时文件 */
export async function addPastedImage(blob: Blob) {
  const dataUrl = await readAsDataUrl(blob)
  const ext = blob.type.split('/')[1] || 'png'
  const name = `screenshot-${new Date().toISOString().replace(/[:.]/g, '-')}.${ext}`
  try {
    const ref = await bridge.saveBlob(name, blob.type, dataUrl.split(',')[1])
    ref.preview = dataUrl
    state.pending.push(ref)
  } catch (e) {
    fail(e)
  }
}

function readAsDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader()
    r.onload = () => resolve(String(r.result))
    r.onerror = () => reject(r.error)
    r.readAsDataURL(blob)
  })
}

// ---------- 宿主事件 ----------
function onHostEvent(e: HostEvent) {
  switch (e.type) {
    case 'chat.delta': {
      const s = convState(e.conversationId)
      s.draft ??= { content: '', reasoning: '' }
      s.draft.content += e.text
      break
    }
    case 'chat.reasoning': {
      const s = convState(e.conversationId)
      s.draft ??= { content: '', reasoning: '' }
      s.draft.reasoning += e.text
      break
    }
    case 'chat.message': {
      const s = convState(e.conversationId)
      s.messages.push(e.message)
      if (e.message.role === 'assistant') s.draft = { content: '', reasoning: '' }
      break
    }
    case 'tool.started':
      convState(e.conversationId).tools[e.callId] = {
        callId: e.callId,
        name: e.name,
        summary: e.summary,
        risk: e.risk,
        state: e.risk === 'blocked' ? 'blocked' : 'running',
      }
      break
    case 'tool.confirm': {
      const t = convState(e.conversationId).tools[e.callId]
      if (t) {
        t.state = 'waiting'
        t.confirm = {
          requestId: e.requestId,
          reason: e.reason,
          rationale: e.rationale,
          rememberable: e.rememberable ?? false,
          ruleDisplay: e.ruleDisplay,
          effect: e.effect ?? 'unknown',
        }
      }
      break
    }
    case 'tool.finished': {
      const t = convState(e.conversationId).tools[e.callId]
      if (t) {
        t.output = e.output
        t.confirm = undefined
        t.remembered = e.decision === 'remembered'
        t.state =
          e.decision === 'blocked' ? 'blocked' : e.decision === 'rejected' ? 'rejected' : e.ok ? 'done' : 'failed'
      }
      break
    }
    case 'plan.updated':
      convState(e.conversationId).plan = e.plan
      break
    case 'chat.done': {
      const s = convState(e.conversationId)
      s.busy = false
      s.draft = null
      if (e.stopReason === 'Cancelled') s.notice = { kind: 'stopped' }
      else if (e.stopReason === 'MaxSteps') s.notice = { kind: 'maxSteps' }
      else if (e.stopReason === 'TooManyFailures') s.notice = { kind: 'tooManyFailures' }
      for (const t of Object.values(s.tools)) if (t.state === 'waiting' || t.state === 'running') t.state = 'failed'
      if (e.modelName && state.app) state.app.modelName = e.modelName
      if (state.compacting?.conversationId === e.conversationId) state.compacting = null
      void refreshList()
      void refreshUsage()
      break
    }
    case 'chat.error': {
      const s = convState(e.conversationId)
      s.busy = false
      s.draft = null
      s.notice = { kind: 'error', text: e.message }
      break
    }
    case 'conversation.updated': {
      const i = state.conversations.findIndex((c) => c.id === e.conversation.id)
      if (i >= 0) state.conversations[i] = e.conversation
      else state.conversations.unshift(e.conversation)
      break
    }
    case 'files.added':
      state.pending.push(...e.attachments)
      break
    case 'app.status':
      if (state.app) {
        state.app.connected = e.connected
        state.app.serverMessage = e.serverMessage
        if (e.modelName) state.app.modelName = e.modelName
      }
      break
    case 'tool.confirmResolved': {
      // 在系统通知里确认或拒绝
      const t = convState(e.conversationId).tools[e.callId]
      if (t) {
        t.confirm = undefined
        t.state = e.choice === 'reject' ? 'rejected' : 'running'
      }
      break
    }
    case 'files.produced': {
      // 产出文件挂到当前这条回答上；第一个能预览的自动在右侧打开
      const s = convState(e.conversationId)
      const last = [...s.messages].reverse().find((m) => m.role === 'assistant')
      if (last) last.outputs = [...(last.outputs ?? []), ...e.files]
      if (state.autoPreview && e.conversationId === state.currentId && !state.preview) {
        const first = e.files.find((f) => f.previewable && f.exists)
        if (first) void openPreview(first)
      }
      break
    }
    case 'context.compacting': {
      if (e.phase === 'done') {
        // 进度条跑到 100% 停一下再消失，不然一闪而过看不清
        state.compacting = { conversationId: e.conversationId, percent: 100, phase: e.phase, messages: e.messages }
        setTimeout(() => {
          if (state.compacting?.phase === 'done') state.compacting = null
        }, 1200)
      } else {
        state.compacting = { conversationId: e.conversationId, percent: e.percent, phase: e.phase, messages: e.messages }
      }
      break
    }
    case 'context.compacted': {
      const c = state.conversations.find((x) => x.id === e.conversationId)
      if (c) c.summaryUpto = e.uptoMessageId
      if (e.conversationId === state.currentId) toast(i18n.global.t('ui.context.compacted'))
      break
    }
    case 'memory.learned': {
      const n = e.items.length
      if (e.skill) toast(i18n.global.t('ui.memory.learnedSkill', { name: e.skill }))
      else if (n > 0) toast(i18n.global.t('ui.memory.learned', { n }))
      break
    }
    case 'app.openConversation':
      void (async () => {
        if (!state.conversations.some((c) => c.id === e.conversationId)) await refreshList()
        await openConversation(e.conversationId)
      })()
      break
    case 'window.state':
      state.maximized = e.maximized
      break
    case 'skills.changed':
      // 技能目录被安装、卸载或手动改动，重新注册后刷新界面
      void loadSkills()
      break
    case 'schedules.changed':
      // 定时任务到点运行、状态变化
      if (state.schedulesOpen) void loadSchedules()
      break
    case 'app.focusInput':
      window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
      break
  }
}

/** 从历史消息恢复工具卡片 */
function rebuildTools(messages: UiMessage[]): Record<string, ToolActivity> {
  const tools: Record<string, ToolActivity> = {}
  for (const m of messages) {
    for (const c of m.toolCalls ?? []) {
      tools[c.id] = { callId: c.id, name: c.name, summary: summarize(c.name, c.arguments), risk: 'auto', state: 'done' }
    }
    if (m.role === 'tool' && m.toolCallId && tools[m.toolCallId]) {
      const t = tools[m.toolCallId]
      t.output = m.content
      if (m.content.startsWith('已被安全策略阻止')) t.state = 'blocked'
      else if (m.content.startsWith('用户拒绝')) t.state = 'rejected'
      else if (/^(错误|执行失败|文件不存在|目录不存在|路径不存在)/.test(m.content)) t.state = 'failed'
    }
  }
  return tools
}

function summarize(name: string, args: string): string {
  try {
    const a = JSON.parse(args)
    return a.command ?? a.path ?? a.name ?? a.fact ?? ''
  } catch {
    return ''
  }
}
