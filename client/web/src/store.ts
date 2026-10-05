import { computed, reactive } from 'vue'
import { bridge } from './bridge'
import { applyLanguage, i18n } from './i18n'
import type {
  AppInfo,
  AttachmentRef,
  ConfirmChoice,
  Conversation,
  HostEvent,
  Mode,
  PlanItem,
  Theme,
  ToolActivity,
  UiLanguage,
  UiMessage,
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
})

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
  const app = await bridge.init()
  state.app = app
  applyLanguage(app.uiLanguage)
  applyTheme(app.theme)
  await refreshList()
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

/** 新建对话只在本地生成草稿，发送第一条消息时才真正创建，避免产生空会话。 */
export function newConversation() {
  state.currentId = null
  state.pending = []
}

export const draftMode = reactive({ mode: 'agent' as Mode, translateFrom: 'auto', translateTo: 'vi' })

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

export async function setMode(mode: Mode) {
  const c = current.value
  if (!c) {
    draftMode.mode = mode
    return
  }
  c.mode = mode
  await bridge.setMode(c.id, mode).catch(fail)
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
      const c = await bridge.createConversation(draftMode.mode)
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
    s.messages.push({ id: `local-${Date.now()}`, role: 'user', content: body, attachments, createdAt: new Date().toISOString() })
    s.busy = true
    s.notice = null
    s.draft = { content: '', reasoning: '' }
    await bridge.send(id, body, attachments)
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
        t.confirm = { requestId: e.requestId, reason: e.reason, rationale: e.rationale }
      }
      break
    }
    case 'tool.finished': {
      const t = convState(e.conversationId).tools[e.callId]
      if (t) {
        t.output = e.output
        t.confirm = undefined
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
      void refreshList()
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
