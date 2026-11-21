/**
 * 编码模式（右侧「代码」标签，Monaco 编辑器）。
 *
 * 和 Cursor 的体验一样：AI 在对话里改代码（write_file / edit_file / multi_edit），这里实时打开那个文件、
 * 用对比视图标出改了哪里；员工也可以自己打开工作区里的文件改、Ctrl+S 保存。
 * 读写都经宿主 code.load / code.save（Core/Code/CodeFeature）：只在这个对话的工作区里，二进制、太大、
 * 链接到工作区外的文件只读。保存带版本号，期间被 AI 改过就不覆盖，让员工选「用 AI 的」还是「用我的」。
 *
 * 只在开发者工具开着（IT 在安全中心打开）、办事对话选了工作区时出现。
 *
 * 界面定位成 AI IDE（AI Agent Workspace）：
 * - 左边：项目文件 / 技能 / MCP 连接器 / 知识（资料库）
 * - 中间：Monaco 编辑器（多标签、AI 改动对比）
 * - 右边：AI Agent（就是这个对话本身：消息 + 输入框）
 * - 底部：终端 / 日志 / Agent Trace（这一轮 AI 每一步做了什么）
 */
import { computed, reactive, watch } from 'vue'
import { bridge } from '../../bridge'
import { convState, current, draftMode, loadChanges, openConversation, registerFeature, state, toast } from '../../store'
import type { ChangeTurn, CodeFile, HostEvent } from '../../types'

export interface CodeTab {
  /** 工作区里的相对路径（读写用） */
  path: string
  fullPath: string
  name: string
  /** 磁盘上的内容（上次读到 / 保存后的） */
  saved: string
  /** 编辑器里现在的内容 */
  draft: string
  version: string
  exists: boolean
  readOnly: boolean
  reason: string
  /** AI 改之前的内容：有它时显示对比视图 */
  before: string | null
  /** AI 改过（标签上打个点） */
  aiChanged: boolean
  /** 员工有没保存的修改时 AI 也改了这个文件：AI 那一版的内容，等员工决定 */
  conflict: { text: string; version: string } | null
  loading: boolean
}

/** 编辑器里选中的一段代码：添加到对话（Ctrl+L）、快速修改（Ctrl+K）用 */
export interface CodeSelection {
  path: string
  language: string
  from: number
  to: number
  code: string
}

/** 选中代码放进对话里的样子：文件、行号、代码块 */
export function selectionMarkdown(sel: CodeSelection, header: string): string {
  const fence = sel.code.includes('```') ? '~~~~' : '```'
  return `${header}\n${fence}${sel.language === 'plaintext' ? '' : sel.language}\n${sel.code.replace(/\s+$/, '')}\n${fence}\n`
}

interface ConversationCode {
  tabs: CodeTab[]
  active: string | null
  /** 跟随 AI：AI 改了没打开的文件时自动打开 */
  follow: boolean
}

/** IDE 布局（记住员工上次的选择） */
interface IdeLayout {
  on: boolean
  explorerTab: 'project' | 'skills' | 'mcp' | 'knowledge'
  explorerOpen: boolean
  bottomOpen: boolean
  bottomTab: 'terminal' | 'logs' | 'trace'
  bottomHeight: number
  agentWidth: number
}

const LayoutKey = 'flyknit.ide'
const defaults: IdeLayout = { on: false, explorerTab: 'project', explorerOpen: true, bottomOpen: true, bottomTab: 'trace', bottomHeight: 220, agentWidth: 420 }

function readLayout(): IdeLayout {
  try {
    const raw = JSON.parse(localStorage.getItem(LayoutKey) ?? '{}')
    return { ...defaults, ...(raw && typeof raw === 'object' ? raw : {}) }
  } catch {
    return { ...defaults }
  }
}

export const ide = reactive<IdeLayout>(readLayout())

watch(
  () => ({ ...ide }),
  (v) => {
    try {
      localStorage.setItem(LayoutKey, JSON.stringify(v))
    } catch {
      // 存不了就只在这次打开期间记住
    }
  },
  { deep: true },
)

export function toggleIde(on = !ide.on) {
  ide.on = on
}

export const clampBottom = (px: number) => Math.round(Math.min(Math.max(px, 120), window.innerHeight * 0.7))
export const clampAgent = (px: number) => Math.round(Math.min(Math.max(px, 320), window.innerWidth * 0.5))

/** 最多同时开这么多个标签：再多就把最早的、没改过的关掉 */
const MaxTabs = 12

export const code = reactive<{ byConversation: Record<string, ConversationCode>; treeVersion: Record<string, number> }>({ byConversation: {}, treeVersion: {} })

/** AI 新建 / 删除了文件：文件树重新列（连着几步只列一次） */
const treeTimers = new Map<string, number>()
function bumpTree(conversationId: string) {
  clearTimeout(treeTimers.get(conversationId))
  treeTimers.set(
    conversationId,
    window.setTimeout(() => {
      code.treeVersion[conversationId] = (code.treeVersion[conversationId] ?? 0) + 1
    }, 400),
  )
}

function convOf(conversationId: string): ConversationCode {
  return (code.byConversation[conversationId] ??= { tabs: [], active: null, follow: true })
}

/** 当前对话能不能用编码模式：开发者工具开着、办事对话、选了工作区 */
export const codeAvailable = computed(
  () => !!state.app?.developerTools && current.value?.mode === 'agent' && !!current.value.workspace,
)

/** 编码模式开着（而且当前对话能用）：主界面换成 IDE 布局 */
export const ideActive = computed(() => ide.on && codeAvailable.value && state.view === 'chat')

export const currentCode = computed(() => (current.value ? convOf(current.value.id) : null))
export const activeTab = computed(() => {
  const c = currentCode.value
  return c?.tabs.find((x) => x.path === c.active) ?? null
})

export const isDirty = (tab: CodeTab) => !tab.readOnly && tab.draft !== tab.saved

const samePath = (a: string, b: string) => a.replace(/\//g, '\\').toLowerCase() === b.replace(/\//g, '\\').toLowerCase()

function baseName(path: string) {
  return path.split(/[\\/]/).pop() || path
}

function fromFile(file: CodeFile): CodeTab {
  return {
    path: file.path,
    fullPath: file.fullPath,
    name: baseName(file.path),
    saved: file.text,
    draft: file.text,
    version: file.version,
    exists: file.exists,
    readOnly: file.readOnly,
    reason: file.reason,
    before: null,
    aiChanged: false,
    conflict: null,
    loading: false,
  }
}

/** 打开工作区里的一个文件（已经开着就切过去） */
export async function openCodeFile(conversationId: string, path: string, opts: { focus?: boolean } = {}) {
  const c = convOf(conversationId)
  const existing = c.tabs.find((x) => samePath(x.path, path))
  if (existing) {
    if (opts.focus !== false) c.active = existing.path
    return existing
  }
  try {
    const file = await bridge.loadCode(conversationId, path)
    if (!file.fullPath) {
      toast(file.reason || path)
      return null
    }
    const tab = fromFile(file)
    c.tabs.push(tab)
    trimTabs(c)
    if (opts.focus !== false || !c.active) c.active = tab.path
    return tab
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
    return null
  }
}

function trimTabs(c: ConversationCode) {
  while (c.tabs.length > MaxTabs) {
    const i = c.tabs.findIndex((x) => x.path !== c.active && !isDirty(x) && !x.conflict)
    if (i < 0) break
    c.tabs.splice(i, 1)
  }
}

export function closeCodeTab(conversationId: string, path: string) {
  const c = convOf(conversationId)
  const i = c.tabs.findIndex((x) => x.path === path)
  if (i < 0) return
  c.tabs.splice(i, 1)
  if (c.active === path) c.active = c.tabs[Math.min(i, c.tabs.length - 1)]?.path ?? null
}

export type SaveOutcome = 'saved' | 'conflict' | 'failed' | 'skipped'

/** 保存（Ctrl+S）。期间被 AI 改过：不覆盖，标成冲突让员工选 */
export async function saveCodeTab(conversationId: string, tab: CodeTab): Promise<SaveOutcome> {
  if (!isDirty(tab)) return 'skipped'
  const text = tab.draft
  try {
    const r = await bridge.saveCode(conversationId, tab.path, text, tab.version)
    if (r.ok) {
      tab.saved = text
      tab.version = r.version
      tab.exists = true
      tab.conflict = null
      return 'saved'
    }
    if (r.conflict) {
      tab.conflict = { text: r.currentText, version: r.version }
      return 'conflict'
    }
    toast(r.message)
    return 'failed'
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
    return 'failed'
  }
}

/** 冲突时用 AI 的版本：丢掉自己没保存的修改 */
export function takeTheirs(tab: CodeTab) {
  if (!tab.conflict) return
  tab.before = tab.saved
  tab.saved = tab.draft = tab.conflict.text
  tab.version = tab.conflict.version
  tab.aiChanged = true
  tab.conflict = null
}

/** 冲突时用自己的：按 AI 那一版的版本号覆盖保存 */
export async function keepMine(conversationId: string, tab: CodeTab) {
  if (!tab.conflict) return
  tab.version = tab.conflict.version
  tab.saved = tab.conflict.text
  tab.conflict = null
  await saveCodeTab(conversationId, tab)
}

/** 看完对比：收起对比视图，回到普通编辑 */
export function acceptDiff(tab: CodeTab) {
  tab.before = null
  tab.aiChanged = false
}

/** 完整路径 → 工作区里的相对路径；不在工作区里返回 null */
function relativeTo(root: string, full: string): string | null {
  const r = root.replace(/[\\/]+$/, '').replace(/\//g, '\\')
  const f = full.replace(/\//g, '\\')
  if (f.toLowerCase().startsWith(r.toLowerCase() + '\\')) return f.slice(r.length + 1)
  return null
}

/** AI 改了文件：开着的刷新成对比；没开的「跟随 AI」时打开 */
async function onAiChanged(conversationId: string, paths: string[]) {
  const c = convOf(conversationId)
  const root = state.conversations.find((x) => x.id === conversationId)?.workspace ?? ''
  for (const full of paths) {
    const tab = c.tabs.find((x) => samePath(x.fullPath, full))
    if (tab) {
      tab.loading = true
      try {
        const file = await bridge.loadCode(conversationId, tab.path)
        if (file.version === tab.version) continue
        if (isDirty(tab)) {
          // 员工也在改：不动编辑器里的内容，等员工决定
          tab.conflict = { text: file.text, version: file.version }
        } else {
          tab.before = tab.before ?? tab.saved
          tab.saved = tab.draft = file.text
          tab.version = file.version
          tab.exists = file.exists
          tab.aiChanged = true
        }
      } catch {
        // 读不到（文件被删、被占用）：下次再说
      } finally {
        tab.loading = false
      }
      if (c.follow) c.active = tab.path
      continue
    }
    const rel = root ? relativeTo(root, full) : null
    if (!rel || !c.follow) continue
    // 没开过的文件不知道 AI 改之前是什么样，只标「AI 改过」，不做对比
    const opened = await openCodeFile(conversationId, rel)
    if (opened) opened.aiChanged = true
  }
}

// ---------- 右边 AI Agent 的任务标签（参照 Cursor）：编码模式里新开任务、切换任务都不退出 IDE ----------

/** 这个工作区里打开过的任务（标签），按打开顺序；关掉标签只是不显示，任务本身还在 */
export const agentTabs = reactive<{ ids: string[] }>({ ids: [] })

const sameDir = (a?: string | null, b?: string | null) => !!a && !!b && a.replace(/[\\/]+$/, '').toLowerCase() === b.replace(/[\\/]+$/, '').toLowerCase()

/** 同一个工作区里的办事任务，最近的在前（历史） */
export const workspaceTasks = computed(() => {
  const ws = current.value?.workspace
  return state.conversations
    .filter((c) => c.mode === 'agent' && sameDir(c.workspace, ws))
    .slice()
    .sort((a, b) => (b.updatedAt ?? '').localeCompare(a.updatedAt ?? ''))
})

export function noteTab(id: string | null | undefined) {
  if (id && !agentTabs.ids.includes(id)) agentTabs.ids.push(id)
  if (agentTabs.ids.length > 8) agentTabs.ids.splice(0, agentTabs.ids.length - 8)
}

export function closeAgentTab(id: string) {
  const i = agentTabs.ids.indexOf(id)
  if (i < 0) return
  agentTabs.ids.splice(i, 1)
  if (state.currentId === id) {
    const next = agentTabs.ids[Math.min(i, agentTabs.ids.length - 1)]
    if (next) void openConversation(next)
  }
}

/** 在当前工作区新开一个办事任务，留在编码模式里（不回首页） */
export async function newTaskInIde(workspace?: string | null) {
  const ws = workspace ?? current.value?.workspace
  if (!ws) return
  try {
    const c = await bridge.createConversation('agent', draftMode.modelId, ws, draftMode.permission, null, null)
    state.conversations.unshift(c)
    state.currentId = c.id
    convState(c.id).loaded = true
    noteTab(c.id)
    window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  }
}

/**
 * 编码模式开着时，侧栏的「新建任务」、Ctrl+N 也留在编码模式：它们会把当前对话清空（回首页），
 * 这里接着在刚才那个工作区新开一个办事任务。
 */
let lastIdeWorkspace: string | null = null
watch(
  () => [state.currentId, ideActive.value] as const,
  ([id, active], [prevId]) => {
    if (active && current.value?.workspace) lastIdeWorkspace = current.value.workspace
    if (!id && prevId && ide.on && state.view === 'chat' && lastIdeWorkspace && state.app?.developerTools) void newTaskInIde(lastIdeWorkspace)
  },
)

export function switchTask(id: string) {
  noteTab(id)
  if (state.currentId !== id) void openConversation(id)
}

// ---------- 恢复到这里（参照 Cursor 的 Restore Checkpoint） ----------

/**
 * 从这条用户消息开始（含）往后各轮改过、还没还原的文件。用现有的「每一轮改动可以还原」（changes.revert）实现：
 * 恢复到这里 = 从最新一轮往回、一轮一轮还原到这条消息之前的样子。
 */
export function turnsSince(conversationId: string, userMessageId: string): ChangeTurn[] {
  const messages = convState(conversationId).messages
  const at = messages.findIndex((m) => m.id === userMessageId)
  if (at < 0) return []
  const order = new Map(messages.slice(at).filter((m) => m.role === 'user').map((m, i) => [m.id, i]))
  return (state.changes[conversationId] ?? [])
    .filter((x) => order.has(x.turnId) && x.files.some((f) => !f.reverted && f.restorable))
    .sort((a, b) => order.get(b.turnId)! - order.get(a.turnId)!)
}

export async function restoreCheckpoint(conversationId: string, userMessageId: string, confirmText: (files: number) => string, done: (files: number) => string) {
  const turns = turnsSince(conversationId, userMessageId)
  const files = new Set(turns.flatMap((x) => x.files.filter((f) => !f.reverted && f.restorable).map((f) => f.path))).size
  if (!files || convState(conversationId).busy) return
  // 会改员工电脑上的文件：先问
  if (!window.confirm(confirmText(files))) return
  try {
    for (const turn of turns) await bridge.revertChanges(conversationId, turn.turnId)
    toast(done(files))
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  } finally {
    await loadChanges(conversationId)
    // 编辑器里开着的文件按磁盘重新读
    const c = code.byConversation[conversationId]
    for (const tab of c?.tabs ?? []) {
      if (isDirty(tab)) continue
      try {
        const file = await bridge.loadCode(conversationId, tab.path)
        tab.saved = tab.draft = file.text
        tab.version = file.version
        tab.before = null
        tab.aiChanged = false
      } catch {
        // 读不到就算了
      }
    }
  }
}

registerFeature({
  id: 'code',
  eventPrefix: 'code.',
  onEvent(e: HostEvent) {
    if (e.type === 'code.treeChanged') bumpTree(e.conversationId)
    if (e.type !== 'code.changed') return
    void onAiChanged(e.conversationId, e.paths)
  },
})
