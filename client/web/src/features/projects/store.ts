/**
 * 左侧「项目」（参照 Claude 的 Projects）：项目 = 一个工作区文件夹 + 名字 + 目标 + 说明。
 * 项目里的任务就是工作区在这个文件夹里（或子文件夹里）的那些对话；进项目可以接着做、开新任务、在编码模式里打开。
 * 清单存在宿主（Core/Projects，projects.json）；目标和说明由宿主在项目里的任务第一次发消息时交给 AI。
 */
import { computed, reactive } from 'vue'
import { bridge } from '../../bridge'
import { loadWorkspaces, newConversationIn, openConversation, state, toast } from '../../store'
import type { Conversation } from '../../types'
import { enterIde } from '../code/store'
import type { ProjectInfo } from './types'

export const projects = reactive<{
  list: ProjectInfo[]
  loaded: boolean
  /** 正在看的项目（null = 列表） */
  selected: string | null
  /** 新建项目的对话框 */
  creating: boolean
}>({ list: [], loaded: false, selected: null, creating: false })

export async function loadProjects() {
  try {
    projects.list = await bridge.projectList()
  } catch {
    // 旧版宿主没有项目：列表为空
    projects.list = []
  }
  projects.loaded = true
}

/** 侧栏「项目」：打开项目页（列表） */
export function openProjects(opts: { create?: boolean } = {}) {
  state.view = 'projects'
  projects.selected = null
  projects.creating = !!opts.create
  void loadProjects()
}

export const selectedProject = computed(() => projects.list.find((p) => p.id === projects.selected) ?? null)

const norm = (p: string) => p.replace(/[\\/]+$/, '').toLowerCase()
/** 文件夹在项目里（就是项目目录，或者在它下面） */
export function inProject(project: ProjectInfo, folder?: string | null): boolean {
  if (!folder) return false
  const root = norm(project.path)
  const f = norm(folder)
  return f === root || f.startsWith(root + '\\') || f.startsWith(root + '/')
}

/** 项目里的任务（最近的在前） */
export function tasksOf(project: ProjectInfo): Conversation[] {
  return state.conversations
    .filter((c) => inProject(project, c.workspace) && !c.deletedAt)
    .slice()
    .sort((a, b) => (b.updatedAt ?? '').localeCompare(a.updatedAt ?? ''))
}

export function selectProject(id: string | null) {
  projects.selected = id
  if (id) void bridge.projectOpen(id).catch(() => {})
}

/**
 * 新建项目：在某个工作区里新建文件夹（parent），或者用已有的文件夹（existing = true 时弹出选文件夹）。
 * 建好后加进工作区列表，打开这个项目。返回失败原因（成功返回空字符串）。
 */
export async function createProject(p: { name: string; goal: string; notes: string; parent?: string; existing?: boolean }): Promise<string> {
  let path = ''
  if (p.existing) {
    // 直接用宿主的选文件夹（store 的 addWorkspace 会把正打开的对话挪过去，这里不要）
    const picked = await bridge.addWorkspace().catch(() => null)
    if (!picked) return 'cancelled'
    path = picked
  }
  const r = await bridge.projectCreate({ name: p.name, goal: p.goal, notes: p.notes, path, parent: p.existing ? '' : (p.parent ?? '') })
  if (!r.ok) return r.message
  if (!p.existing && r.path) await bridge.addWorkspace(r.path).catch(() => null)
  await loadWorkspaces()
  await loadProjects()
  projects.creating = false
  if (r.id) selectProject(r.id)
  return ''
}

export async function saveProject(id: string, patch: { name?: string; goal?: string; notes?: string }): Promise<boolean> {
  const r = await bridge.projectUpdate(id, patch)
  if (!r.ok) {
    toast(r.message)
    return false
  }
  await loadProjects()
  return true
}

/** 从项目列表移除（不删文件夹、不删任务） */
export async function removeProject(id: string) {
  await bridge.projectRemove(id)
  projects.selected = null
  await loadProjects()
}

/** 在项目里开一个新任务（普通对话界面，工作区就是项目文件夹） */
export async function newTaskInProject(project: ProjectInfo) {
  state.view = 'chat'
  await newConversationIn(project.path)
}

/** 打开项目里的某个任务 */
export async function openProjectTask(id: string) {
  state.view = 'chat'
  await openConversation(id)
}

/** 在编码模式里打开项目：接着最近的办事任务，没有就新开一个 */
export async function openProjectInIde(project: ProjectInfo, newTask = false) {
  await enterIde(project.path, { newTask })
}
