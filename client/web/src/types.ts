// 与 client/src/Flyknit.Client/Bridge/WebBridge.cs 中的 DTO 保持一致

export type Mode = 'chat' | 'translate' | 'agent'
export type UiLanguage = 'zh-CN' | 'vi-VN' | 'en-US'
export type Theme = 'system' | 'light' | 'dark'
export type Risk = 'auto' | 'confirm' | 'blocked'

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
}

export interface ConfirmPrompt {
  requestId: string
  reason: string
  rationale: string
}

export type ConfirmChoice = 'allowOnce' | 'allowForConversation' | 'reject'

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
    }
  | { type: 'tool.finished'; conversationId: string; callId: string; ok: boolean; output: string; decision: string }
  | { type: 'plan.updated'; conversationId: string; plan: PlanItem[] }
  | { type: 'chat.done'; conversationId: string; stopReason: string; modelName?: string }
  | { type: 'chat.error'; conversationId: string; message: string }
  | { type: 'conversation.updated'; conversation: Conversation }
  | { type: 'files.added'; attachments: AttachmentRef[] }
  | { type: 'app.status'; connected: boolean; serverMessage: string; modelName: string }
  | { type: 'app.focusInput' }
