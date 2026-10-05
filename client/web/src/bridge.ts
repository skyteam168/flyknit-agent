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
  AttachmentRef,
  ConfirmChoice,
  Conversation,
  HostEvent,
  Mode,
  ModelInfo,
  SkillInfo,
  Theme,
  UiLanguage,
  UiMessage,
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

  private call<T>(method: string, params: Record<string, unknown> = {}, files?: File[]): Promise<T> {
    const id = `r${++this.seq}`
    const message = { kind: 'request', id, method, params }
    return new Promise<T>((resolve, reject) => {
      // 宿主 20 秒未响应视为失败，避免界面一直空白
      const timer = window.setTimeout(() => {
        if (this.pending.delete(id)) reject(new Error(`宿主未响应：${method}`))
      }, 20000)
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

  // ---------- 会话 ----------
  listConversations = (query = '', trash = false) =>
    this.call<Conversation[]>('conversations.list', { query, trash })
  createConversation = (mode: Mode, modelId: number | null) =>
    this.call<Conversation>('conversation.create', { mode, modelId })
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
  openSkillsFolder = () => this.call<void>('skills.openFolder')

  // ---------- 对话 ----------
  send = (conversationId: string, text: string, attachments: AttachmentRef[]) =>
    this.call<void>('chat.send', { conversationId, text, attachments })
  stop = (conversationId: string) => this.call<void>('chat.stop', { conversationId })
  confirm = (requestId: string, choice: ConfirmChoice) => this.call<void>('tool.confirm', { requestId, choice })

  // ---------- 文件 ----------
  pickFiles = () => this.call<AttachmentRef[]>('files.pick')
  /** 拖入窗口的文件：WebView2 通过 additionalObjects 把真实路径交给宿主 */
  addDroppedFiles = (files: File[]) => this.call<AttachmentRef[]>('files.dropped', {}, files)
  /** 粘贴的截图等没有路径的内容，以 base64 交给宿主保存为临时文件 */
  saveBlob = (fileName: string, mime: string, base64: string) =>
    this.call<AttachmentRef>('files.saveBlob', { fileName, mime, base64 })
  openPath = (path: string) => this.call<void>('files.open', { path })
}

export const bridge = new Bridge()
