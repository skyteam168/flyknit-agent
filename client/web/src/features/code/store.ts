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
import { loadLspStatus } from './lsp'
import { convState, current, draftMode, loadChanges, moveToWorkspace, openConversation, registerFeature, send, setPlanMode, state, toast } from '../../store'
import type { ChangeTurn, CodeFile, HostEvent, UiMessage } from '../../types'

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
  /** 光标所在行、选中的行（告诉 AI「这里」是哪里） */
  cursor?: { line: number; column?: number; from: number; to: number }
  /** 不是磁盘上的文件（计划模式的「方案.md」）：不读写磁盘、不告诉宿主；saved 是 AI 给的那一版，draft 是员工改过的 */
  virtual?: boolean
  /** 要跳到的位置（全局搜索点了某一处）；at 每次不同，编辑器看到新的就跳 */
  reveal?: { line: number; column: number; length: number; at: number }
  /** 对比视图是「源代码管理」打开的（和上次提交比），不是 AI 改的：横幅说法不同，没有「撤销此文件」 */
  diffSource?: 'git'
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
  explorerTab: 'project' | 'search' | 'git' | 'skills' | 'mcp' | 'knowledge'
  explorerOpen: boolean
  /** 左栏（图标栏 + 内容）的宽度，右边缘拖动调 */
  explorerWidth: number
  bottomOpen: boolean
  bottomTab: 'terminal' | 'logs' | 'trace'
  bottomHeight: number
  agentWidth: number
  /** 底部终端右边的终端列表宽度，左边缘拖动调 */
  termListWidth: number
}

const LayoutKey = 'flyknit.ide'
const defaults: IdeLayout = { on: false, explorerTab: 'project', explorerOpen: true, explorerWidth: 260, bottomOpen: true, bottomTab: 'terminal', bottomHeight: 220, agentWidth: 420, termListWidth: 260 }

function readLayout(): IdeLayout {
  try {
    const raw = JSON.parse(localStorage.getItem(LayoutKey) ?? '{}')
    const layout = { ...defaults, ...(raw && typeof raw === 'object' ? raw : {}) }
    if (typeof layout.explorerWidth !== 'number' || !Number.isFinite(layout.explorerWidth)) layout.explorerWidth = defaults.explorerWidth
    if (typeof layout.termListWidth !== 'number' || !Number.isFinite(layout.termListWidth)) layout.termListWidth = defaults.termListWidth
    return layout
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
export const clampExplorer = (px: number) => Math.round(Math.min(Math.max(px, 180), Math.max(180, window.innerWidth * 0.45)))
export const clampTermList = (px: number) => Math.round(Math.min(Math.max(px, 160), Math.max(160, window.innerWidth * 0.4)))
export const clampAgent = (px: number) => Math.round(Math.min(Math.max(px, 320), window.innerWidth * 0.5))

/** 最多同时开这么多个标签：再多就把最早的、没改过的关掉 */
// 标签栏能横向滚动、看不见的收进「…」，关掉的标签会释放编辑器内存，所以可以多开一些
const MaxTabs = 20

/**
 * 编辑器状态（打开的文件、当前标签）按工作区存，不按任务存（参照 Cursor：编辑器属于项目，右边的 AI 任务可以随便换）：
 * 同一个工作区里新开任务、切换任务，打开的文件都还在。没有工作区的对话按对话 id 存。
 */
export const code = reactive<{ byEditor: Record<string, ConversationCode>; treeVersion: Record<string, number> }>({ byEditor: {}, treeVersion: {} })

const workspaceKey = (ws: string) => 'ws:' + ws.replace(/[\\/]+$/, '').toLowerCase()

/** 这个对话的编辑器状态存在哪个格子里 */
export function editorKey(conversationId: string): string {
  const ws = state.conversations.find((c) => c.id === conversationId)?.workspace
  return ws ? workspaceKey(ws) : conversationId
}

/** 只看不建 */
export function editorOf(conversationId: string): ConversationCode | undefined {
  return code.byEditor[editorKey(conversationId)]
}

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

// 注意：不能写成 `return (code.byEditor[k] ??= {...})`——第一次创建时那样返回的是原对象、不是响应式的，
// 在它上面改（重新打开客户端时恢复标签、设当前文件），界面不会跟着变：标签出来了、编辑器却空着
function convOf(conversationId: string): ConversationCode {
  const key = editorKey(conversationId)
  if (!code.byEditor[key]) code.byEditor[key] = { tabs: [], active: null, follow: true }
  return code.byEditor[key]
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

export const isDirty = (tab: CodeTab) => !tab.readOnly && !tab.virtual && tab.draft !== tab.saved

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

/** 打开文件并跳到某一处（全局搜索的结果） */
export async function openCodeAt(conversationId: string, path: string, line: number, column = 1, length = 0) {
  const tab = await openCodeFile(conversationId, path)
  if (tab) tab.reveal = { line, column, length, at: Date.now() }
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
  tab.diffSource = undefined
}

/** 源代码管理里点了一个改动：打开文件，和上次提交（HEAD）对比；新文件对比空白 */
export async function openGitDiff(conversationId: string, workspacePath: string, headText: string) {
  const tab = await openCodeFile(conversationId, workspacePath)
  if (!tab) return
  tab.before = headText
  tab.diffSource = 'git'
}

/** 完整路径 → 工作区里的相对路径；不在工作区里返回 null */
function relativeTo(root: string, full: string): string | null {
  const r = root.replace(/[\\/]+$/, '').replace(/\//g, '\\')
  const f = full.replace(/\//g, '\\')
  if (f.toLowerCase().startsWith(r.toLowerCase() + '\\')) return f.slice(r.length + 1)
  return null
}

/** 预览里点「在编辑器里打开」：文件在这个任务的工作区里就开成标签（能改），不在返回 false */
export async function openInEditor(conversationId: string, fullPath: string): Promise<boolean> {
  const root = state.conversations.find((x) => x.id === conversationId)?.workspace ?? ''
  const rel = root ? relativeTo(root, fullPath) : null
  if (!rel) return false
  return (await openCodeFile(conversationId, rel)) !== null
}

export function inWorkspace(conversationId: string, fullPath: string): boolean {
  const root = state.conversations.find((x) => x.id === conversationId)?.workspace ?? ''
  return !!root && relativeTo(root, fullPath) !== null
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
          // 正在看「和上次提交的对比」时 AI 改了：换成 AI 改之前的样子做对比
          tab.before = tab.diffSource === 'git' ? tab.saved : (tab.before ?? tab.saved)
          tab.diffSource = undefined
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

// ---------- 项目文件树：选中的文件夹、新建文件 / 文件夹、全部收起（像 VS Code 的资源管理器） ----------

export const tree = reactive<{
  /** 每个对话选中的文件夹（新建放在这里；空 = 工作区根） */
  selectedDir: Record<string, string>
  /** 正在新建：在哪个文件夹下、建文件还是文件夹 */
  creating: { conversationId: string; parent: string; directory: boolean } | null
  /** 每加一次，所有展开的文件夹收起 */
  collapseVersion: number
  /** 工作区根节点是否展开 */
  rootOpen: boolean
}>({ selectedDir: {}, creating: null, collapseVersion: 0, rootOpen: true })

export function startCreate(conversationId: string, directory: boolean) {
  tree.rootOpen = true
  tree.creating = { conversationId, parent: tree.selectedDir[conversationId] ?? '', directory }
}

/** 新建：成功后那一层重新列；文件接着在编辑器里打开 */
export async function finishCreate(name: string) {
  const c = tree.creating
  tree.creating = null
  if (!c || !name.trim()) return
  try {
    const r = await bridge.createCode(c.conversationId, c.parent, name.trim(), c.directory)
    if (!r.ok) {
      toast(r.message)
      return
    }
    code.treeVersion[c.conversationId] = (code.treeVersion[c.conversationId] ?? 0) + 1
    if (c.directory) tree.selectedDir[c.conversationId] = r.path
    else await openCodeFile(c.conversationId, r.path)
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  }
}

export function refreshTree(conversationId: string) {
  code.treeVersion[conversationId] = (code.treeVersion[conversationId] ?? 0) + 1
}

export function collapseTree() {
  tree.collapseVersion++
  if (current.value) expanded[editorKey(current.value.id)] = {}
}

// ---------- 重新打开客户端后原样恢复（参照 VS Code）：打开的文件、当前标签、展开的文件夹 ----------
//
// 按工作区记在本机（和编辑器状态同一个格子）。员工手动关掉的标签、收起的文件夹不会回来——记的就是关之前的样子。
// 虚拟标签（计划模式的「方案.md」）不记；文件已经不在了的，恢复时跳过、不报错。

const SessionKey = 'flyknit.ide.session'
interface SavedEditor {
  tabs: string[]
  active: string | null
  /** 展开着的文件夹（相对路径） */
  expanded: string[]
}

function readSession(): Record<string, SavedEditor> {
  try {
    const raw = JSON.parse(localStorage.getItem(SessionKey) ?? '{}')
    return raw && typeof raw === 'object' ? raw : {}
  } catch {
    return {}
  }
}

const saved: Record<string, SavedEditor> = readSession()

/** 每个工作区展开着的文件夹：路径 → true。文件树（ProjectTree）直接读写这里 */
export const expanded = reactive<Record<string, Record<string, boolean>>>(
  Object.fromEntries(Object.entries(saved).map(([k, v]) => [k, Object.fromEntries((v.expanded ?? []).map((p) => [p, true]))])),
)

export function expandedOf(conversationId: string): Record<string, boolean> {
  const key = editorKey(conversationId)
  if (!expanded[key]) expanded[key] = {}
  return expanded[key]
}

/** 这次打开客户端后已经恢复过（或者本来就是新开）的格子：只有它们的状态会写回去，免得还没恢复就被空状态覆盖 */
const restored = new Set<string>()

function saveSession() {
  for (const key of new Set([...Object.keys(code.byEditor), ...Object.keys(expanded)])) {
    if (!restored.has(key)) continue
    const c = code.byEditor[key]
    saved[key] = {
      tabs: (c?.tabs ?? []).filter((t) => !t.virtual).map((t) => t.path).slice(-MaxTabs),
      active: c?.active && c.tabs.some((t) => t.path === c.active && !t.virtual) ? c.active : null,
      expanded: Object.entries(expanded[key] ?? {}).filter(([, on]) => on).map(([p]) => p).slice(0, 500),
    }
  }
  try {
    // 只留最近用过的 50 个工作区
    const entries = Object.entries(saved).slice(-50)
    localStorage.setItem(SessionKey, JSON.stringify(Object.fromEntries(entries)))
  } catch {
    // 存不了就只在这次打开期间记住
  }
}

let saveTimer = 0
watch(
  () => [
    Object.entries(code.byEditor).map(([k, c]) => k + ':' + c.tabs.map((t) => t.path).join('|') + '>' + (c.active ?? '')).join(';'),
    JSON.stringify(expanded),
  ],
  () => {
    clearTimeout(saveTimer)
    saveTimer = window.setTimeout(saveSession, 300)
  },
)

/** 进入编码模式的这个对话：把这个工作区上次开着的文件重新打开（每次启动只做一次） */
async function restoreEditor(conversationId: string) {
  const key = editorKey(conversationId)
  if (restored.has(key)) return
  restored.add(key)
  const last = saved[key]
  if (!last?.tabs?.length) return
  const c = convOf(conversationId)
  for (const path of last.tabs) {
    if (c.tabs.some((x) => samePath(x.path, path))) continue
    try {
      const file = await bridge.loadCode(conversationId, path)
      if (!file.fullPath || !file.exists) continue // 文件已经删了 / 不能打开：跳过，不提示
      if (c.tabs.some((x) => samePath(x.path, path))) continue
      c.tabs.push(fromFile(file))
    } catch {
      // 读不了就跳过
    }
  }
  const again = last.active ? c.tabs.find((x) => samePath(x.path, last.active!)) : undefined
  if (again) c.active = again.path
  else if (!c.tabs.some((x) => x.path === c.active) && c.tabs.length) c.active = c.tabs[c.tabs.length - 1].path
  saveSession()
}

// ---------- 告诉 AI 员工在看什么（Core/Code/EditorContext）：当前文件、光标、打开的文件、推断出的当前项目 ----------

export interface ProjectInfo {
  projectRoot: string | null
  projectName: string
  workspace: string
  narrower: boolean
}

/** 每个对话推断出的当前项目（编辑器顶上的「当前项目」条用） */
export const projects = reactive<Record<string, ProjectInfo>>({})

let focusTimer = 0
let lastFocus = ''
function reportFocus() {
  clearTimeout(focusTimer)
  focusTimer = window.setTimeout(async () => {
    const id = current.value?.id
    if (!id) return
    const c = editorOf(id)
    const found = ideActive.value ? (c?.tabs.find((x) => x.path === c.active) ?? null) : null
    const tab = found?.virtual ? null : found
    const focus = {
      activeFile: tab?.path ?? '',
      line: tab?.cursor?.line ?? 0,
      selectionFrom: tab?.cursor && tab.cursor.to > tab.cursor.from ? tab.cursor.from : 0,
      selectionTo: tab?.cursor && tab.cursor.to > tab.cursor.from ? tab.cursor.to : 0,
      // 退出编码模式时告诉宿主「什么都没开」，AI 不再拿旧的编辑器状态当参考
      openFiles: ideActive.value ? (c?.tabs.filter((x) => !x.virtual).map((x) => x.path) ?? []) : [],
    }
    const key = id + JSON.stringify(focus)
    if (key === lastFocus) return
    lastFocus = key
    try {
      projects[id] = await bridge.codeFocus(id, focus)
    } catch {
      // 旧版宿主没有这个调用：没有编辑器上下文，照旧
    }
  }, 400)
}

watch(
  () => {
    const id = current.value?.id
    const c = id ? editorOf(id) : undefined
    const tab = c?.tabs.find((x) => x.path === c.active)
    return [id, ideActive.value, c?.active, c?.tabs.map((x) => x.path).join('|'), tab?.cursor?.line, tab?.cursor?.from, tab?.cursor?.to, current.value?.workspace]
  },
  reportFocus,
)

/**
 * 把这个任务的工作区收窄到当前项目（只缩小范围，比原来更安全）：命令、文件操作默认都在这个项目里，文件树只显示它。
 * 开着的文件改成相对新工作区的路径；不在这个项目里的标签关掉。
 */
export async function narrowToProject(conversationId: string) {
  const info = projects[conversationId]
  if (!info?.projectRoot || !info.narrower) return
  const root = info.projectRoot
  // 编辑器按工作区存：收窄后这个任务换到项目的格子；原来大工作区的格子不动（别的任务可能还开着）
  const old = convOf(conversationId)
  const activeFull = old.tabs.find((t) => t.path === old.active)?.fullPath ?? ''
  const moved = old.tabs.flatMap((t) => {
    const rel = relativeTo(root, t.fullPath)
    return rel ? [{ ...t, path: rel }] : []
  })
  await moveToWorkspace(conversationId, root)
  const c = convOf(conversationId)
  for (const t of moved) if (!c.tabs.some((x) => samePath(x.fullPath, t.fullPath))) c.tabs.push(t)
  trimTabs(c)
  c.active = c.tabs.find((t) => samePath(t.fullPath, activeFull))?.path ?? c.active ?? c.tabs[0]?.path ?? null
  code.treeVersion[conversationId] = (code.treeVersion[conversationId] ?? 0) + 1
  lastFocus = ''
  reportFocus()
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

/**
 * 在当前工作区新开一个办事任务，留在编码模式里（不回首页）。打开的文件不关（编辑器按工作区存）。
 * handoffFrom：带上那个任务的交接摘要（对话太长、做下一件事时用），第一句话发出时交给 AI。
 */
export async function newTaskInIde(workspace?: string | null, opts: { handoffFrom?: string } = {}) {
  const ws = workspace ?? current.value?.workspace
  if (!ws) return
  try {
    const c = await bridge.createConversation('agent', draftMode.modelId, ws, draftMode.permission, null, null)
    state.conversations.unshift(c)
    state.currentId = c.id
    convState(c.id).loaded = true
    noteTab(c.id)
    if (opts.handoffFrom) void startHandoff(c.id, opts.handoffFrom)
    window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  }
}

// ---------- 带上摘要新建任务 ----------

export interface Handoff {
  from: string
  fromTitle: string
  status: 'working' | 'ready'
  summary: string
}

/** 新任务 id → 带过来的交接摘要（第一轮做完就收起：摘要已经跟着第一句话存进历史） */
export const handoffs = reactive<Record<string, Handoff>>({})

async function startHandoff(to: string, from: string) {
  const fromTitle = state.conversations.find((x) => x.id === from)?.title ?? ''
  handoffs[to] = { from, fromTitle, status: 'working', summary: '' }
  try {
    const r = await bridge.codeHandoff(to, from)
    const h = handoffs[to]
    if (!h || h.from !== from) return // 员工点了「不带了」
    if (r.ok && r.summary) {
      h.status = 'ready'
      h.summary = r.summary
    } else {
      delete handoffs[to]
      if (r.message) toast(r.message)
    }
  } catch (e) {
    delete handoffs[to]
    toast(String((e as Error)?.message ?? e))
  }
}

export function dropHandoff(to: string) {
  delete handoffs[to]
  void bridge.codeHandoffClear(to).catch(() => {})
}

/**
 * 编码模式里还没说话的新任务：右边对话区不留一大片空白，显示欢迎页（标志、「新对话」、带过来的交接摘要、可以怎么开始）。
 * 已经在跑（第一句话刚发出）的不算。
 */
export const ideWelcome = computed(() => {
  const c = current.value
  if (!ideActive.value || !c || c.mode !== 'agent') return false
  const s = state.byId[c.id]
  if (!s || s.busy || s.draft) return false
  return s.messages.length === 0 && (c.messageCount ?? 0) === 0
})

// ---------- 该新开任务了：对话太长、换了项目 ----------

/** 上下文用到这么多（占模型窗口）就建议新开任务；自动整理在 75% 左右 */
export const LongContextRatio = 0.6

/** 每个任务在做的项目（第一句话发出时员工看的那个项目） */
const taskProject: Record<string, string> = {}
/** 员工点了「不再提示」的：任务 id → 哪种提示 */
export const hintDismissed = reactive<Record<string, { long?: boolean; project?: string }>>({})

/** 这个任务现在打开的项目和它之前在做的项目不一样：返回 [现在, 之前] 的项目名 */
export function projectSwitch(conversationId: string): [string, string] | null {
  const now = projects[conversationId]?.projectRoot
  const before = taskProject[conversationId]
  if (!now || !before || sameDir(now, before)) return null
  return [baseName(now), baseName(before)]
}

watch(
  () => [current.value?.id, Math.max(current.value?.messageCount ?? 0, current.value ? (state.byId[current.value.id]?.messages.length ?? 0) : 0), current.value ? projects[current.value.id]?.projectRoot : null] as const,
  ([id, count, root]) => {
    if (!id || !root) return
    // 还没说话：跟着员工看的项目走；说过话之后记住第一次的
    if (count === 0 || !taskProject[id]) taskProject[id] = root
  },
)

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

/**
 * 进入编码模式（首页的「编码」、项目里的「在编码模式中打开」）：在这个工作区里接着最近的办事任务，没有就新开一个。
 * newTask：总是新开一个任务。
 */
export async function enterIde(workspace: string, opts: { newTask?: boolean } = {}) {
  if (!workspace || !state.app?.developerTools) return
  state.view = 'chat'
  toggleIde(true)
  const latest = opts.newTask
    ? undefined
    : state.conversations
        .filter((c) => c.mode === 'agent' && sameDir(c.workspace, workspace))
        .sort((a, b) => (b.updatedAt ?? '').localeCompare(a.updatedAt ?? ''))[0]
  if (latest) {
    noteTab(latest.id)
    await openConversation(latest.id)
  } else {
    await newTaskInIde(workspace)
  }
}

export function switchTask(id: string) {
  noteTab(id)
  if (state.currentId !== id) void openConversation(id)
}

// ---------- AI 改完后：保留 / 撤销 / 查看（参照 Cursor 的 Keep / Undo / Review） ----------
//
// AI 的改动一执行就已经写进磁盘了（和 Cursor 一样）。「保留」= 认可这一轮的改动（只是不再提示）；
// 「撤销」= 用现有的按轮还原把这一轮改过的文件恢复成这一轮之前的样子；「查看」= 打开这一轮的改动对比。

const KeptKey = 'flyknit.keptTurns'

function readKept(): Record<string, string[]> {
  try {
    const raw = JSON.parse(localStorage.getItem(KeptKey) ?? '{}')
    return raw && typeof raw === 'object' ? raw : {}
  } catch {
    return {}
  }
}

/** 每个对话里员工点过「保留」的轮次（记在本机，重启后不再提示） */
export const kept = reactive<Record<string, string[]>>(readKept())

function saveKept() {
  try {
    // 只留最近的一些，免得越攒越多
    const entries = Object.entries(kept).slice(-200)
    localStorage.setItem(KeptKey, JSON.stringify(Object.fromEntries(entries.map(([k, v]) => [k, v.slice(-50)]))))
  } catch {
    // 存不了就只在这次打开期间记住
  }
}

const isKept = (conversationId: string, turnId: string) => (kept[conversationId] ?? []).includes(turnId)
const openFiles = (t: ChangeTurn) => t.files.filter((f) => !f.reverted)

/** 最近一轮还没处理（没保留、没全撤销）的改动 */
export function pendingTurn(conversationId: string): ChangeTurn | null {
  const turns = state.changes[conversationId] ?? []
  for (let i = turns.length - 1; i >= 0; i--) {
    const t = turns[i]
    if (!t.files.length) continue
    return openFiles(t).length && !isKept(conversationId, t.turnId) ? t : null
  }
  return null
}

/** 这个文件最近一次被 AI 改、还没处理的那一轮 */
export function pendingTurnFor(conversationId: string, fullPath: string): ChangeTurn | null {
  const turns = state.changes[conversationId] ?? []
  for (let i = turns.length - 1; i >= 0; i--) {
    const t = turns[i]
    if (isKept(conversationId, t.turnId)) continue
    if (openFiles(t).some((f) => samePath(f.path, fullPath))) return t
  }
  return null
}

/** 保留这一轮的改动：不再提示，编辑器里这些文件的对比收起 */
export function keepTurn(conversationId: string, turnId: string) {
  if (!kept[conversationId]) kept[conversationId] = []
  kept[conversationId].push(turnId)
  saveKept()
  const turn = (state.changes[conversationId] ?? []).find((t) => t.turnId === turnId)
  for (const tab of editorOf(conversationId)?.tabs ?? []) {
    if (turn?.files.some((f) => samePath(f.path, tab.fullPath))) acceptDiff(tab)
  }
}

/**
 * 撤销这一轮（或其中几个文件）的改动：恢复成这一轮之前的样子。会改员工电脑上的文件，先问；
 * 员工在编辑器里还有没保存的修改时一并提醒（撤销后编辑器按磁盘重新读）。
 */
export async function undoTurn(conversationId: string, turnId: string, confirmText: (n: number, dirty: number) => string, done: (n: number) => string, paths?: string[]) {
  const turn = (state.changes[conversationId] ?? []).find((t) => t.turnId === turnId)
  if (!turn) return
  const files = openFiles(turn).filter((f) => !paths || paths.some((p) => samePath(p, f.path)))
  if (!files.length) return
  const tabs = (editorOf(conversationId)?.tabs ?? []).filter((tab) => files.some((f) => samePath(f.path, tab.fullPath)))
  if (!window.confirm(confirmText(files.length, tabs.filter(isDirty).length))) return
  try {
    const r = await bridge.revertChanges(conversationId, turnId, paths ? files.map((f) => f.path) : undefined)
    toast(done(r.reverted.length))
    if (r.skipped.length) toast(r.skipped.map((x) => `${x.path.split(/[\\/]/).pop()}：${x.reason}`).join('；'))
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  } finally {
    await loadChanges(conversationId)
    for (const tab of tabs) {
      try {
        const file = await bridge.loadCode(conversationId, tab.path)
        tab.saved = tab.draft = file.text
        tab.version = file.version
        tab.exists = file.exists
        tab.before = null
        tab.aiChanged = false
        tab.conflict = null
      } catch {
        // 文件被撤销成「不存在」（AI 新建的）：读不到就关掉
        closeCodeTab(conversationId, tab.path)
      }
    }
  }
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
    const c = editorOf(conversationId)
    for (const tab of c?.tabs ?? []) {
      if (isDirty(tab) || tab.virtual) continue
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

// ---------- 计划模式（参照 Cursor 的 Plan）：方案卡片、编辑器里可以改的「方案.md」、执行 ----------
//
// 计划模式本身在 Core（PlanMode：只读、拿掉会改东西的工具）；这里只管编码模式里怎么看、怎么改、怎么执行。
// 方案文档不写进项目目录（不弄脏代码库）：只是编辑器里的一个虚拟标签，跟着工作区的编辑器存在内存里。

const planPath = (conversationId: string) => `flyknit-plan/${conversationId}.md`

/** 编码模式下，计划模式的这一轮跑完了、最后是 AI 的回答：显示方案卡片（代替输入框里那一行「方案好了」） */
export const planCardVisible = computed(() => {
  const c = current.value
  if (!ideActive.value || !c || !state.planMode[c.id]) return false
  const s = state.byId[c.id]
  if (!s || s.busy) return false
  return s.messages.length > 0 && s.messages[s.messages.length - 1].role === 'assistant'
})

/** 这一版方案：计划模式下 AI 最后的那段回答 */
export function planAnswer(conversationId: string): UiMessage | null {
  const msgs = state.byId[conversationId]?.messages ?? []
  for (let i = msgs.length - 1; i >= 0; i--) if (msgs[i].role === 'assistant' && msgs[i].content.trim()) return msgs[i]
  return null
}

/** 方案标题：回答开头的「# 标题」，没有就用任务标题 */
export function planTitle(conversationId: string, fallback: string): string {
  const head = /^\s*#{1,3}\s+(.+)$/m.exec(planAnswer(conversationId)?.content ?? '')
  return (head?.[1] ?? state.conversations.find((c) => c.id === conversationId)?.title ?? '').trim() || fallback
}

/** 方案文档：标题 + AI 的说明 + 待办（员工在编辑器里改的就是这个） */
export function planMarkdown(conversationId: string, labels: { fallback: string; todos: string }): string {
  const answer = (planAnswer(conversationId)?.content ?? '').trim()
  const title = planTitle(conversationId, labels.fallback)
  const body = /^\s*#{1,3}\s+/.test(answer) ? answer : `# ${title}\n\n${answer}`
  const plan = state.byId[conversationId]?.plan ?? []
  const todos = plan.length ? `\n\n## ${labels.todos}\n\n` + plan.map((p) => `- [${p.status === 'completed' ? 'x' : ' '}] ${p.step}`).join('\n') : ''
  return body + todos + '\n'
}

/** 方案标签（没开过就是 undefined） */
export function planTab(conversationId: string): CodeTab | undefined {
  return editorOf(conversationId)?.tabs.find((t) => t.fullPath === planPath(conversationId))
}

/** 员工在编辑器里改过方案 */
export function planEdited(conversationId: string): boolean {
  const tab = planTab(conversationId)
  return !!tab && tab.draft.trim() !== tab.saved.trim()
}

/** 在编辑器里打开方案（已经开着就切过去；AI 出了新一版、员工又没改过时换成新的） */
export function openPlanDoc(conversationId: string, labels: { fallback: string; todos: string; name: string }) {
  const text = planMarkdown(conversationId, labels)
  const c = convOf(conversationId)
  let tab = planTab(conversationId)
  if (!tab) {
    tab = {
      path: planPath(conversationId), fullPath: planPath(conversationId), name: labels.name, saved: text, draft: text, version: '', exists: true,
      readOnly: false, reason: '', before: null, aiChanged: false, conflict: null, loading: false, virtual: true,
    }
    c.tabs.push(tab)
    trimTabs(c)
  } else if (!planEdited(conversationId)) {
    tab.saved = tab.draft = text
  }
  c.active = tab.path
}

/**
 * 按方案执行：退出计划模式，让 AI 动手。员工在编辑器里改过方案的，把改后的方案一起发过去（以这一版为准）。
 * 发出去以后方案标签的「AI 那一版」换成这一版，免得下次又当成改过。
 */
export async function executePlanInIde(conversationId: string, text: { go: string; goEdited: string }) {
  const tab = planTab(conversationId)
  const edited = planEdited(conversationId)
  await setPlanMode(false)
  const ok = await send(edited && tab ? `${text.goEdited}\n\n${tab.draft.trim()}` : text.go, { noSplit: true })
  if (ok && tab) tab.saved = tab.draft
}

// AI 出了新一版方案：开着的方案标签（员工没改过的）跟着换
watch(
  () => {
    const id = current.value?.id
    return id && planTab(id) ? [id, planAnswer(id)?.id, (state.byId[id]?.plan ?? []).map((p) => p.step + p.status).join('|')].join('#') : ''
  },
  () => {
    const id = current.value?.id
    const tab = id ? planTab(id) : undefined
    if (id && tab && !planEdited(id)) tab.saved = tab.draft = planMarkdown(id, planLabels)
  },
)
/** 方案文档里的「方案」「待办」这些字（界面语言在 PlanCard 里取，记下来给上面的 watch 用） */
let planLabels = { fallback: 'Plan', todos: 'To-dos' }
export function rememberPlanLabels(labels: { fallback: string; todos: string }) {
  planLabels = labels
}

// 语言服务（./lsp.ts）：跨文件跳到定义时打开那个文件；重命名改了没打开的文件时刷新文件树和开着的标签
window.addEventListener('flyknit:open-code', (e) => {
  const d = (e as CustomEvent<{ path: string; line: number; column: number }>).detail
  const id = current.value?.id
  if (id && d?.path) void openCodeAt(id, d.path, d.line, d.column, 0)
})
window.addEventListener('flyknit:files-changed', () => {
  const id = current.value?.id
  if (id) code.treeVersion[id] = (code.treeVersion[id] ?? 0) + 1
})
// 进编码模式、换任务、换工作区：查一下这个任务能用哪些语言服务
watch(
  () => [ideActive.value, current.value?.id, current.value?.workspace] as const,
  ([active, id]) => {
    if (active && id) void loadLspStatus(id)
  },
  { immediate: true },
)

/** Ctrl+B（快捷键表里的「切换左侧栏」）在编码模式里：收起 / 打开左边的文件目录，编辑器就宽了（和 VS Code 一样） */
export function toggleExplorer() {
  ide.explorerOpen = !ide.explorerOpen
}

// 进入编码模式、切到另一个工作区的任务：恢复那个工作区上次开着的文件
watch(
  () => [ideActive.value, current.value?.id, current.value?.workspace] as const,
  ([active, id]) => {
    if (active && id) void restoreEditor(id)
  },
  { immediate: true },
)

// Ctrl+Shift+F：编码模式里打开左边的「搜索」（参照 VS Code）。编辑器里也能按（Monaco 自己没占这个键）
window.addEventListener(
  'keydown',
  (e) => {
    if (!ideActive.value || !(e.ctrlKey || e.metaKey) || !e.shiftKey || e.altKey || e.key.toLowerCase() !== 'f') return
    e.preventDefault()
    e.stopPropagation()
    ide.explorerOpen = true
    ide.explorerTab = 'search'
    window.dispatchEvent(new CustomEvent('flyknit:code-search'))
  },
  true,
)

registerFeature({
  id: 'code',
  eventPrefix: 'code.',
  onRunFinished(conversationId: string) {
    // 交接摘要已经跟着第一句话交给 AI 了
    if (handoffs[conversationId]?.status === 'ready') delete handoffs[conversationId]
  },
  onEvent(e: HostEvent) {
    if (e.type === 'code.treeChanged') bumpTree(e.conversationId)
    if (e.type !== 'code.changed') return
    void onAiChanged(e.conversationId, e.paths)
  },
})
