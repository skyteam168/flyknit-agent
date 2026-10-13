<script setup lang="ts">
import UpdateBar from './UpdateBar.vue'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowLeft,
  Brain,
  CalendarClock,
  ChartColumn,
  Check,
  ChevronRight,
  ExternalLink,
  Folder,
  FolderOpen,
  FolderPlus,
  Hand,
  LayoutGrid,
  LibraryBig,
  UsersRound,
  Languages,
  LoaderCircle,
  MessageSquare,
  MessageSquarePlus,
  MoreHorizontal,
  Pencil,
  Pin,
  PinOff,
  Plus,
  Plug,
  Puzzle,
  RotateCcw,
  Search,
  SlidersHorizontal,
  Trash2,
  Wrench,
  X,
} from '@lucide/vue'
import KnitMark from './KnitMark.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import ExpertAvatar from './ExpertAvatar.vue'
import Popover from './Popover.vue'
import { openLibrary } from '../library'
import {
  addWorkspace,
  deleteConversation,
  draftMode,
  moveToWorkspace,
  newConversation,
  newConversationIn,
  openConversation,
  openExperts,
  purgeConversation,
  refreshList,
  removeWorkspace,
  renameConversation,
  reorderConversations,
  reorderWorkspaces,
  restoreConversation,
  openPath,
  runStatus,
  selectedWorkspace,
  state,
  toast,
  togglePin,
} from '../store'
import type { Conversation, Mode } from '../types'

const emit = defineEmits<{ navigate: [] }>()
const { t } = useI18n()

/** 专家对话在列表里用专家头像（专家下线了就回到模式图标） */
const expertOf = (c: Conversation) => (c.expertId ? state.experts.find((e) => e.id === c.expertId) : undefined)

const editingId = ref<string | null>(null)
const editText = ref('')
const menuFor = ref<string | null>(null)
const menuPos = ref({ x: 0, y: 0 })
const confirm = ref<{ id: string; purge: boolean } | null>(null)
const editInput = ref<HTMLInputElement[]>([])
const now = ref(Date.now())
let clock: number | undefined

const modeIcon: Record<Mode, unknown> = { chat: MessageSquare, translate: Languages, agent: Wrench }
const filters: ('all' | Mode)[] = ['all', 'agent', 'chat', 'translate']

let searchTimer: number | undefined
watch(
  () => state.query,
  () => {
    clearTimeout(searchTimer)
    searchTimer = window.setTimeout(refreshList, 200)
  },
)

// ---------- 视图选项：分组、排序、折叠（记在本机） ----------
const VIEW_KEY = 'flyknit-sidebar-view'
function readView() {
  try {
    const v = JSON.parse(localStorage.getItem(VIEW_KEY) || '{}')
    return {
      group: v.group === 'flat' ? 'flat' : 'workspace',
      sort: v.sort === 'manual' ? 'manual' : 'recent',
      collapsed: Array.isArray(v.collapsed) ? (v.collapsed as string[]) : [],
    }
  } catch {
    return { group: 'workspace', sort: 'recent', collapsed: [] as string[] }
  }
}
const view = reactive(readView() as { group: 'workspace' | 'flat'; sort: 'manual' | 'recent'; collapsed: string[] })
watch(view, () => {
  try {
    localStorage.setItem(VIEW_KEY, JSON.stringify(view))
  } catch {
    /* 存不了就只在这次有效 */
  }
})

const keyOf = (path: string) => path.toLowerCase()
const isCollapsed = (path: string) => view.collapsed.includes(keyOf(path))
function toggleGroup(path: string) {
  const k = keyOf(path)
  const i = view.collapsed.indexOf(k)
  if (i >= 0) view.collapsed.splice(i, 1)
  else view.collapsed.push(k)
}

const defaultPath = computed(() => state.workspaces.find((w) => w.isDefault)?.path ?? state.app?.defaultWorkspace ?? '')
/** 没记工作区的老对话算在默认工作区里 */
const workspaceOf = (c: Conversation) => c.workspace || defaultPath.value
const baseName = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() || path

const visible = computed(() => state.conversations.filter((c) => state.filter === 'all' || c.mode === state.filter))

function sortItems(list: Conversation[]) {
  const time = (c: Conversation) => new Date(c.updatedAt).getTime()
  return [...list].sort((a, b) => {
    if (a.pinned !== b.pinned) return a.pinned ? -1 : 1
    if (view.sort === 'manual') {
      // 没拖过的（新对话）排在拖过的前面
      const ao = a.sortOrder ?? -1
      const bo = b.sortOrder ?? -1
      if (ao !== bo) return ao - bo
    }
    return time(b) - time(a)
  })
}

interface Group {
  path: string
  name: string
  exists: boolean
  isDefault: boolean
  items: Conversation[]
  latest: number
}

const groups = computed<Group[]>(() => {
  const map = new Map<string, Group>()
  for (const w of state.workspaces) map.set(keyOf(w.path), { ...w, items: [], latest: 0 })
  for (const c of visible.value) {
    const path = workspaceOf(c)
    let g = map.get(keyOf(path))
    if (!g) {
      // 工作区已从列表里移除，但还有对话在那里：照样分一组
      g = { path, name: baseName(path), exists: true, isDefault: false, items: [], latest: 0 }
      map.set(keyOf(path), g)
    }
    g.items.push(c)
    g.latest = Math.max(g.latest, new Date(c.updatedAt).getTime())
  }
  let list = [...map.values()].map((g) => ({ ...g, items: sortItems(g.items) }))
  // 搜索时只留有结果的组
  if (state.query) list = list.filter((g) => g.items.length > 0)
  if (view.sort === 'recent') list.sort((a, b) => b.latest - a.latest)
  return list
})

const flatItems = computed(() => sortItems(visible.value))
const isSelected = (path: string) => keyOf(path) === keyOf(selectedWorkspace.value || '')

// 打开某个对话时，它所在的组自动展开
watch(
  () => state.currentId,
  (id) => {
    const c = state.conversations.find((x) => x.id === id)
    if (!c) return
    const i = view.collapsed.indexOf(keyOf(workspaceOf(c)))
    if (i >= 0) view.collapsed.splice(i, 1)
  },
)

// ---------- 搜索（点放大镜才出来） ----------
const searching = ref(!!state.query)
const searchInput = ref<HTMLInputElement>()
async function openSearch() {
  searching.value = true
  await nextTick()
  searchInput.value?.focus()
}
function closeSearch() {
  state.query = ''
  searching.value = false
}

function ago(iso: string) {
  const diff = Math.max(0, now.value - new Date(iso).getTime())
  const m = Math.floor(diff / 60000)
  if (m < 1) return t('ui.time.now')
  if (m < 60) return t('ui.time.minutes', { n: m })
  const h = Math.floor(m / 60)
  if (h < 24) return t('ui.time.hours', { n: h })
  return t('ui.time.days', { n: Math.floor(h / 24) })
}

const initial = computed(() => (state.app?.userName ?? '?').trim().charAt(0).toUpperCase() || '?')

function select(c: Conversation) {
  if (editingId.value === c.id) return
  void openConversation(c.id)
  emit('navigate')
}

function startNew() {
  newConversation()
  state.showTrash = false
  emit('navigate')
  window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
}

async function startNewIn(path: string) {
  state.showTrash = false
  const i = view.collapsed.indexOf(keyOf(path))
  if (i >= 0) view.collapsed.splice(i, 1)
  await newConversationIn(path)
  emit('navigate')
}

/** 添加工作区：选好文件夹后直接在里面开一个新对话 */
async function onAddWorkspace() {
  // 先开新对话再切工作区：不能把正打开的那个对话挪到新文件夹里
  if (await addWorkspace({ newConversation: true })) {
    emit('navigate')
    window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
  }
}

async function startRename(c: Conversation) {
  menuFor.value = null
  editingId.value = c.id
  editText.value = c.title || t('sidebar.untitled')
  await nextTick()
  editInput.value[0]?.focus()
  editInput.value[0]?.select()
}

async function commitRename(c: Conversation) {
  if (editingId.value !== c.id) return
  editingId.value = null
  await renameConversation(c.id, editText.value)
}

function openMenu(c: Conversation, e: MouseEvent) {
  const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
  wsMenu.value = null
  menuPos.value = { x: Math.min(rect.left, window.innerWidth - 196), y: Math.min(rect.bottom + 4, window.innerHeight - 140) }
  menuFor.value = menuFor.value === c.id ? null : c.id
}

function onContext(c: Conversation, e: MouseEvent) {
  e.preventDefault()
  wsMenu.value = null
  menuPos.value = { x: Math.min(e.clientX, window.innerWidth - 196), y: Math.min(e.clientY, window.innerHeight - 140) }
  menuFor.value = c.id
}

const menuConversation = computed(() => [...state.conversations, ...state.trash].find((c) => c.id === menuFor.value) ?? null)

// ---------- 工作区的「…」菜单 ----------
const wsMenu = ref<{ group: Group; x: number; y: number } | null>(null)
function openWsMenu(g: Group, e: MouseEvent) {
  const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
  menuFor.value = null
  wsMenu.value = wsMenu.value?.group.path === g.path ? null : { group: g, x: Math.min(rect.left, window.innerWidth - 216), y: Math.min(rect.bottom + 4, window.innerHeight - 120) }
}
async function removeGroup(g: Group) {
  wsMenu.value = null
  await removeWorkspace(g.path)
}

async function toggleTrash() {
  state.showTrash = !state.showTrash
  await refreshList()
}

async function onConfirm() {
  if (!confirm.value) return
  const { id, purge } = confirm.value
  confirm.value = null
  if (purge) await purgeConversation(id)
  else await deleteConversation(id)
}

// ---------- 「更多」：鼠标移上去弹出卡片 ----------
const moreOpen = ref(false)
const moreBtn = ref<HTMLElement>()
const morePos = ref({ x: 0, y: 0 })
let moreTimer: number | undefined
const moreActive = computed(() => state.showTrash)

function showMore() {
  clearTimeout(moreTimer)
  const rect = moreBtn.value?.getBoundingClientRect()
  if (rect) {
    const height = 3 * 40 + 16
    morePos.value = {
      x: Math.min(rect.right + 6, window.innerWidth - 230),
      y: Math.max(8, Math.min(rect.top - 8, window.innerHeight - height - 8)),
    }
  }
  moreOpen.value = true
}
function hideMoreSoon() {
  clearTimeout(moreTimer)
  moreTimer = window.setTimeout(() => (moreOpen.value = false), 180)
}
function toggleMore() {
  if (moreOpen.value) moreOpen.value = false
  else showMore()
}
function moreAction(fn: () => void) {
  moreOpen.value = false
  fn()
  emit('navigate')
}

// ---------- 拖动：手动排序、拖到别的工作区 ----------
const drag = ref<{ kind: 'conv' | 'ws'; id: string } | null>(null)
const dropAt = ref<{ kind: 'item' | 'group'; id: string; after: boolean } | null>(null)

function onDragStart(kind: 'conv' | 'ws', id: string, e: DragEvent) {
  drag.value = { kind, id }
  if (e.dataTransfer) {
    e.dataTransfer.effectAllowed = 'move'
    e.dataTransfer.setData('text/plain', id)
  }
}
function onDragEnd() {
  drag.value = null
  dropAt.value = null
}
function onItemDragOver(c: Conversation, e: DragEvent) {
  if (drag.value?.kind !== 'conv' || drag.value.id === c.id) return
  // 只有手动排序时才能在列表里挪位置；拖到别的组的对话上 = 挪到那个工作区
  const sameGroup = view.group === 'flat' || keyOf(workspaceOf(c)) === keyOf(workspaceOf(byId(drag.value.id)!))
  if (sameGroup && view.sort !== 'manual') return
  e.preventDefault()
  const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
  dropAt.value = { kind: 'item', id: c.id, after: e.clientY > rect.top + rect.height / 2 }
}
function onGroupDragOver(g: Group, e: DragEvent) {
  if (!drag.value) return
  if (drag.value.kind === 'ws') {
    if (view.sort !== 'manual' || keyOf(drag.value.id) === keyOf(g.path)) return
    e.preventDefault()
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
    dropAt.value = { kind: 'group', id: g.path, after: e.clientY > rect.top + rect.height / 2 }
    return
  }
  const c = byId(drag.value.id)
  if (!c || keyOf(workspaceOf(c)) === keyOf(g.path) || !g.exists) return
  e.preventDefault()
  dropAt.value = { kind: 'group', id: g.path, after: false }
}
const byId = (id: string) => state.conversations.find((c) => c.id === id)

async function onDrop() {
  const d = drag.value
  const at = dropAt.value
  onDragEnd()
  if (!d || !at) return
  if (d.kind === 'ws' && at.kind === 'group') {
    const paths = groups.value.map((g) => g.path).filter((p) => keyOf(p) !== keyOf(d.id))
    const i = paths.findIndex((p) => keyOf(p) === keyOf(at.id))
    paths.splice(i + (at.after ? 1 : 0), 0, d.id)
    await reorderWorkspaces(paths)
    return
  }
  const c = byId(d.id)
  if (!c) return
  if (at.kind === 'group') {
    await moveToWorkspace(c.id, at.id)
    toast(t('ui.workspace.moved', { name: baseName(at.id) }))
    return
  }
  const target = byId(at.id)
  if (!target) return
  const targetPath = workspaceOf(target)
  if (view.group === 'workspace' && keyOf(targetPath) !== keyOf(workspaceOf(c))) {
    await moveToWorkspace(c.id, targetPath)
    toast(t('ui.workspace.moved', { name: baseName(targetPath) }))
    if (view.sort !== 'manual') return
  }
  const list = (view.group === 'flat' ? flatItems.value : groups.value.find((g) => keyOf(g.path) === keyOf(targetPath))?.items ?? [])
    .map((x) => x.id)
    .filter((id) => id !== c.id)
  const i = list.indexOf(target.id)
  list.splice(i + (at.after ? 1 : 0), 0, c.id)
  await reorderConversations(list)
}

const closeMenu = (e: MouseEvent) => {
  if (!(e.target as HTMLElement).closest('.menu, .more, .ws-more')) {
    menuFor.value = null
    wsMenu.value = null
  }
}
onMounted(() => {
  document.addEventListener('mousedown', closeMenu)
  clock = window.setInterval(() => (now.value = Date.now()), 60000)
})
onBeforeUnmount(() => {
  document.removeEventListener('mousedown', closeMenu)
  clearInterval(clock)
  clearTimeout(moreTimer)
})
</script>

<template>
  <aside class="sidebar">
    <header class="brand">
      <KnitMark :size="28" />
      <strong>{{ t('app.short') }}</strong>
      <span class="ver">v{{ state.app?.version }}</span>
      <span
        class="conn"
        :class="{ off: !state.app?.connected }"
        :title="state.app?.connected ? t('status.connected', { model: state.app?.modelName }) : state.app?.serverMessage || t('status.offline')"
      />
    </header>

    <template v-if="!state.showTrash">
      <button class="new-task" type="button" :class="{ on: state.currentId === null && state.view === 'chat' && !draftMode.expertId }" @click="startNew">
        <Plus :size="18" />
        <span>{{ t('sidebar.newChat') }}</span>
        <kbd>Ctrl N</kbd>
      </button>

      <nav class="nav">
        <button
          type="button"
          :class="{ on: state.view === 'experts' || (state.view === 'chat' && state.currentId === null && !!draftMode.expertId) }"
          @click="(openExperts(), emit('navigate'))"
        >
          <UsersRound :size="17" /> {{ t('ui.nav.experts') }}
        </button>
        <button type="button" :class="{ on: state.view === 'library' }" @click="(openLibrary(), emit('navigate'))">
          <LibraryBig :size="17" /> {{ t('ui.nav.library') }}
        </button>
        <button type="button" @click="state.skillsOpen = true"><Puzzle :size="17" /> {{ t('ui.nav.skills') }}</button>
        <button type="button" @click="state.mcpOpen = true"><Plug :size="17" /> {{ t('ui.nav.connectors') }}</button>
        <button type="button" @click="state.schedulesOpen = true"><CalendarClock :size="17" /> {{ t('ui.nav.schedules') }}</button>
        <button
          ref="moreBtn"
          type="button"
          class="more-nav"
          :class="{ on: moreActive, open: moreOpen }"
          aria-haspopup="menu"
          :aria-expanded="moreOpen"
          @mouseenter="showMore"
          @mouseleave="hideMoreSoon"
          @click="toggleMore"
        >
          <LayoutGrid :size="17" /> {{ t('ui.nav.more') }}
          <ChevronRight :size="15" class="end" />
        </button>
      </nav>

      <div class="ws-head">
        <h3>{{ view.group === 'workspace' ? t('ui.workspace.title') : state.filter === 'all' ? t('ui.nav.tasks') : t(`ui.filter.${state.filter}`) }}</h3>
        <button type="button" class="head-btn" :class="{ on: searching }" :title="t('ui.workspace.search')" @click="searching ? closeSearch() : openSearch()">
          <Search :size="15" />
        </button>
        <Popover placement="down" align="end" :width="200">
          <template #trigger="{ toggle, open }">
            <button
              type="button"
              class="head-btn"
              :class="{ on: open || state.filter !== 'all' }"
              :title="t('ui.workspace.view')"
              @click="toggle"
            >
              <SlidersHorizontal :size="15" />
            </button>
          </template>
          <template #default>
            <p class="opt-head">{{ t('ui.workspace.groupBy') }}</p>
            <button type="button" class="filter-opt" @click="view.group = 'workspace'">
              <span>{{ t('ui.workspace.byWorkspace') }}</span><Check v-if="view.group === 'workspace'" :size="14" />
            </button>
            <button type="button" class="filter-opt" @click="view.group = 'flat'">
              <span>{{ t('ui.workspace.flat') }}</span><Check v-if="view.group === 'flat'" :size="14" />
            </button>
            <div class="opt-sep" />
            <p class="opt-head">{{ t('ui.workspace.sortBy') }}</p>
            <button type="button" class="filter-opt" :title="t('ui.workspace.dragHint')" @click="view.sort = 'manual'">
              <span>{{ t('ui.workspace.manual') }}</span><Check v-if="view.sort === 'manual'" :size="14" />
            </button>
            <button type="button" class="filter-opt" @click="view.sort = 'recent'">
              <span>{{ t('ui.workspace.recent') }}</span><Check v-if="view.sort === 'recent'" :size="14" />
            </button>
            <div class="opt-sep" />
            <p class="opt-head">{{ t('ui.workspace.show') }}</p>
            <button v-for="f in filters" :key="f" type="button" class="filter-opt" @click="state.filter = f">
              <span>{{ t(`ui.filter.${f}`) }}</span><Check v-if="state.filter === f" :size="14" />
            </button>
          </template>
        </Popover>
        <button type="button" class="head-btn" :title="t('ui.workspace.addShort')" @click="onAddWorkspace">
          <FolderPlus :size="15" />
        </button>
      </div>

      <label v-if="searching" class="search">
        <Search :size="14" />
        <input
          ref="searchInput"
          v-model="state.query"
          type="search"
          :placeholder="t('ui.workspace.search')"
          @keydown.esc.prevent="closeSearch"
        />
        <button type="button" class="clear" :aria-label="t('menu.delete')" @click="closeSearch"><X :size="14" /></button>
      </label>

      <div class="list" role="list" @dragover.prevent @drop.prevent="onDrop">
        <!-- 按工作区分组 -->
        <template v-if="view.group === 'workspace'">
          <p v-if="state.query && groups.length === 0" class="empty">{{ t('sidebar.noResults') }}</p>
          <div v-for="g in groups" :key="g.path" class="group">
            <div
              class="ws-row"
              :class="{
                selected: isSelected(g.path),
                missing: !g.exists,
                'drop-in': dropAt?.kind === 'group' && dropAt.id === g.path && drag?.kind === 'conv',
                'drop-before': dropAt?.kind === 'group' && dropAt.id === g.path && drag?.kind === 'ws' && !dropAt.after,
                'drop-after': dropAt?.kind === 'group' && dropAt.id === g.path && drag?.kind === 'ws' && dropAt.after,
                menuOpen: wsMenu?.group.path === g.path,
              }"
              :title="g.exists ? g.path : t('ui.workspace.missing')"
              :draggable="view.sort === 'manual' && !state.query"
              @click="toggleGroup(g.path)"
              @dragstart="onDragStart('ws', g.path, $event)"
              @dragend="onDragEnd"
              @dragover="onGroupDragOver(g, $event)"
            >
              <ChevronRight :size="13" class="caret" :class="{ open: !isCollapsed(g.path) }" />
              <component :is="isCollapsed(g.path) ? Folder : FolderOpen" :size="16" class="folder" />
              <span class="ws-name">{{ g.name }}</span>
              <span class="ws-actions">
                <button
                  v-if="g.exists"
                  type="button"
                  class="ws-btn"
                  :title="t('ui.workspace.newHere')"
                  @click.stop="startNewIn(g.path)"
                >
                  <MessageSquarePlus :size="15" />
                </button>
                <button type="button" class="ws-btn ws-more" :aria-label="t('ui.workspace.title')" @click.stop="openWsMenu(g, $event)">
                  <MoreHorizontal :size="15" />
                </button>
              </span>
            </div>
            <template v-if="!isCollapsed(g.path)">
              <p v-if="g.items.length === 0" class="empty in-group">{{ t('ui.workspace.empty') }}</p>
              <div
                v-for="c in g.items"
                :key="c.id"
                class="item nested"
                role="listitem"
                tabindex="0"
                :draggable="editingId !== c.id"
                :class="{
                  active: c.id === state.currentId && state.view === 'chat',
                  menuOpen: menuFor === c.id,
                  dragging: drag?.kind === 'conv' && drag.id === c.id,
                  'drop-before': dropAt?.kind === 'item' && dropAt.id === c.id && !dropAt.after,
                  'drop-after': dropAt?.kind === 'item' && dropAt.id === c.id && dropAt.after,
                }"
                @click="select(c)"
                @keydown.enter="select(c)"
                @keydown.f2.prevent="startRename(c)"
                @dblclick="startRename(c)"
                @contextmenu="onContext(c, $event)"
                @dragstart="onDragStart('conv', c.id, $event)"
                @dragend="onDragEnd"
                @dragover="onItemDragOver(c, $event)"
              >
                <span class="status" :class="runStatus(c.id)">
                  <LoaderCircle v-if="runStatus(c.id) === 'running'" :size="15" class="spin" />
                  <Hand v-else-if="runStatus(c.id) === 'waiting'" :size="15" />
                  <ExpertAvatar v-else-if="expertOf(c)" :avatar="expertOf(c)!.avatar" :name="expertOf(c)!.name" :id="expertOf(c)!.id" :size="17" />
                  <component :is="modeIcon[c.mode]" v-else :size="15" />
                </span>
                <input
                  v-if="editingId === c.id"
                  ref="editInput"
                  v-model="editText"
                  class="rename"
                  maxlength="100"
                  @click.stop
                  @keydown.enter.prevent="commitRename(c)"
                  @keydown.esc.prevent="editingId = null"
                  @blur="commitRename(c)"
                />
                <span v-else class="title">{{ c.title || t('sidebar.untitled') }}</span>
                <Pin v-if="c.pinned && editingId !== c.id" :size="12" class="pin-mark" />
                <span v-if="editingId !== c.id" class="time">
                  {{ runStatus(c.id) === 'waiting' ? t('ui.status.waiting') : ago(c.updatedAt) }}
                </span>
                <button type="button" class="more" :aria-label="t('menu.rename')" @click.stop="openMenu(c, $event)">
                  <MoreHorizontal :size="16" />
                </button>
              </div>
            </template>
          </div>
        </template>

        <!-- 单列表 -->
        <template v-else>
          <p v-if="flatItems.length === 0" class="empty">{{ state.query ? t('sidebar.noResults') : t('sidebar.empty') }}</p>
          <div
            v-for="c in flatItems"
            :key="c.id"
            class="item"
            role="listitem"
            tabindex="0"
            :draggable="editingId !== c.id && view.sort === 'manual'"
            :class="{
              active: c.id === state.currentId && state.view === 'chat',
              menuOpen: menuFor === c.id,
              dragging: drag?.kind === 'conv' && drag.id === c.id,
              'drop-before': dropAt?.kind === 'item' && dropAt.id === c.id && !dropAt.after,
              'drop-after': dropAt?.kind === 'item' && dropAt.id === c.id && dropAt.after,
            }"
            @click="select(c)"
            @keydown.enter="select(c)"
            @keydown.f2.prevent="startRename(c)"
            @dblclick="startRename(c)"
            @contextmenu="onContext(c, $event)"
            @dragstart="onDragStart('conv', c.id, $event)"
            @dragend="onDragEnd"
            @dragover="onItemDragOver(c, $event)"
          >
            <span class="status" :class="runStatus(c.id)">
              <LoaderCircle v-if="runStatus(c.id) === 'running'" :size="15" class="spin" />
              <Hand v-else-if="runStatus(c.id) === 'waiting'" :size="15" />
              <ExpertAvatar v-else-if="expertOf(c)" :avatar="expertOf(c)!.avatar" :name="expertOf(c)!.name" :id="expertOf(c)!.id" :size="17" />
              <component :is="modeIcon[c.mode]" v-else :size="15" />
            </span>
            <input
              v-if="editingId === c.id"
              ref="editInput"
              v-model="editText"
              class="rename"
              maxlength="100"
              @click.stop
              @keydown.enter.prevent="commitRename(c)"
              @keydown.esc.prevent="editingId = null"
              @blur="commitRename(c)"
            />
            <span v-else class="title">{{ c.title || t('sidebar.untitled') }}</span>
            <Pin v-if="c.pinned && editingId !== c.id" :size="12" class="pin-mark" />
            <span v-if="editingId !== c.id" class="time">
              {{ runStatus(c.id) === 'waiting' ? t('ui.status.waiting') : ago(c.updatedAt) }}
            </span>
            <button type="button" class="more" :aria-label="t('menu.rename')" @click.stop="openMenu(c, $event)">
              <MoreHorizontal :size="16" />
            </button>
          </div>
        </template>
      </div>
    </template>

    <template v-else>
      <button class="back" type="button" @click="toggleTrash"><ArrowLeft :size="16" /> {{ t('sidebar.backToList') }}</button>
      <p class="trash-hint">{{ t('sidebar.trashHint') }}</p>
      <div class="list">
        <p v-if="state.trash.length === 0" class="empty">{{ t('sidebar.empty') }}</p>
        <div v-for="c in state.trash" :key="c.id" class="item trash-item" @contextmenu="onContext(c, $event)">
          <span class="status"><component :is="modeIcon[c.mode]" :size="15" /></span>
          <span class="title">{{ c.title || t('sidebar.untitled') }}</span>
          <button type="button" class="icon-btn small" :title="t('menu.restore')" @click="restoreConversation(c.id)"><RotateCcw :size="15" /></button>
          <button type="button" class="icon-btn small danger" :title="t('menu.purge')" @click="confirm = { id: c.id, purge: true }"><Trash2 :size="15" /></button>
        </div>
      </div>
    </template>

    <UpdateBar />
    <button type="button" class="user" @click="state.settingsOpen = true">
      <span class="avatar">{{ initial }}</span>
      <span class="who">
        <strong>{{ state.app?.userName }}</strong>
        <small>{{ state.app?.department || state.app?.machineName }}</small>
      </span>
      <ChevronRight :size="16" class="go" />
    </button>

    <Teleport to="body">
      <div
        v-if="moreOpen"
        class="more-card"
        role="menu"
        :style="{ left: `${morePos.x}px`, top: `${morePos.y}px` }"
        @mouseenter="showMore"
        @mouseleave="hideMoreSoon"
      >
        <button role="menuitem" type="button" @click="moreAction(() => (state.memoryOpen = true))">
          <Brain :size="17" /> {{ t('ui.nav.memory') }}
        </button>
        <button role="menuitem" type="button" @click="moreAction(() => (state.usageOpen = true))">
          <ChartColumn :size="17" /> {{ t('ui.nav.usage') }}
        </button>
        <button role="menuitem" type="button" :class="{ on: state.showTrash }" @click="moreAction(() => void toggleTrash())">
          <Trash2 :size="17" /> {{ t('ui.nav.trash') }}
        </button>
      </div>
      <div v-if="wsMenu" class="menu" :style="{ left: `${wsMenu.x}px`, top: `${wsMenu.y}px` }" role="menu">
        <button v-if="wsMenu.group.exists" role="menuitem" type="button" @click="(startNewIn(wsMenu.group.path), (wsMenu = null))">
          <MessageSquarePlus :size="15" /> {{ t('ui.workspace.newHere') }}
        </button>
        <button v-if="wsMenu.group.exists" role="menuitem" type="button" @click="(openPath(wsMenu.group.path), (wsMenu = null))">
          <ExternalLink :size="15" /> {{ t('ui.workspace.open') }}
        </button>
        <button v-if="!wsMenu.group.isDefault" role="menuitem" type="button" class="danger" @click="removeGroup(wsMenu.group)">
          <X :size="15" /> {{ t('ui.workspace.remove') }}
        </button>
      </div>
      <div v-if="menuConversation" class="menu" :style="{ left: `${menuPos.x}px`, top: `${menuPos.y}px` }" role="menu">
        <template v-if="!menuConversation.deletedAt">
          <button role="menuitem" type="button" @click="startRename(menuConversation)"><Pencil :size="15" /> {{ t('menu.rename') }}</button>
          <button role="menuitem" type="button" @click="(togglePin(menuConversation), (menuFor = null))">
            <component :is="menuConversation.pinned ? PinOff : Pin" :size="15" />
            {{ menuConversation.pinned ? t('menu.unpin') : t('menu.pin') }}
          </button>
          <button role="menuitem" type="button" class="danger" @click="((confirm = { id: menuConversation.id, purge: false }), (menuFor = null))">
            <Trash2 :size="15" /> {{ t('menu.delete') }}
          </button>
        </template>
        <template v-else>
          <button role="menuitem" type="button" @click="(restoreConversation(menuConversation.id), (menuFor = null))"><RotateCcw :size="15" /> {{ t('menu.restore') }}</button>
          <button role="menuitem" type="button" class="danger" @click="((confirm = { id: menuConversation.id, purge: true }), (menuFor = null))">
            <Trash2 :size="15" /> {{ t('menu.purge') }}
          </button>
        </template>
      </div>
      <ConfirmDialog
        v-if="confirm"
        :title="confirm.purge ? t('confirmPurge.title') : t('confirmDelete.title')"
        :body="confirm.purge ? t('confirmPurge.body') : t('confirmDelete.body')"
        :ok="confirm.purge ? t('confirmPurge.ok') : t('confirmDelete.ok')"
        danger
        @cancel="confirm = null"
        @ok="onConfirm"
      />
    </Teleport>
  </aside>
</template>

<style scoped>
.sidebar {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-width: 0;
  background: var(--cloth-sunk);
  border-right: 1px solid var(--line);
}
.brand {
  display: flex;
  align-items: center;
  gap: 9px;
  height: 56px;
  padding: 0 16px;
  app-region: drag;
  -webkit-app-region: drag;
}
.brand strong {
  font-size: var(--t-lg);
  font-weight: 600;
  letter-spacing: -0.01em;
}
.ver {
  font-size: calc(11px * var(--font-scale));
  color: var(--ink-faint);
}
.conn {
  width: 8px;
  height: 8px;
  margin-left: auto;
  border-radius: 50%;
  background: var(--thread);
  box-shadow: 0 0 0 3px var(--thread-wash);
}
.conn.off {
  background: var(--red);
  box-shadow: 0 0 0 3px var(--red-wash);
}
.ws-head {
  display: flex;
  align-items: center;
  gap: 2px;
  margin: 16px 12px 4px 24px;
}
.ws-head h3 {
  flex: 1;
  margin: 0;
  font-size: var(--t-sm);
  font-weight: 500;
  color: var(--ink-faint);
}
.head-btn {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 7px;
  color: var(--ink-faint);
}
.head-btn:hover,
.head-btn.on {
  background: color-mix(in srgb, var(--ink) 7%, transparent);
  color: var(--ink);
}
.search {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 32px;
  margin: 2px 12px 6px;
  padding: 0 10px;
  border-radius: 8px;
  border: 1px solid var(--line);
  background: var(--cloth);
  color: var(--ink-faint);
}
.search:focus-within {
  border-color: var(--line-strong);
}
.search input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: 0;
  background: transparent;
  font-size: var(--t-sm);
}
.search input::-webkit-search-cancel-button {
  display: none;
}
.clear {
  display: grid;
  place-items: center;
  color: var(--ink-faint);
}
.opt-head {
  margin: 4px 10px 2px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.opt-sep {
  height: 1px;
  margin: 6px 4px;
  background: var(--line);
}
.filter-opt {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  height: 30px;
  padding: 0 10px;
  border-radius: 7px;
  font-size: var(--t-sm);
}
.filter-opt:hover {
  background: var(--chip);
}
.new-task {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 0 12px 4px;
  height: 42px;
  padding: 0 12px;
  border-radius: 10px;
  font-weight: 500;
  color: var(--ink);
}
.new-task:hover,
.new-task.on {
  background: color-mix(in srgb, var(--ink) 7%, transparent);
}
.new-task kbd {
  margin-left: auto;
  font-family: inherit;
  font-size: calc(11px * var(--font-scale));
  color: var(--ink-faint);
}
.nav {
  display: flex;
  flex-direction: column;
  padding: 0 12px;
}
.nav button {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 38px;
  padding: 0 12px;
  border-radius: 10px;
  color: var(--ink-soft);
}
.more-nav .end {
  margin-left: auto;
  color: var(--ink-faint);
}
.nav button.more-nav.open {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
  color: var(--ink);
}
.nav button.on {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
  color: var(--ink);
  font-weight: 500;
}
.nav button:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
  color: var(--ink);
}
.list {
  flex: 1;
  overflow-y: auto;
  padding: 0 12px 12px;
}
.empty {
  margin: 12px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.empty.in-group {
  margin: 2px 0 6px 40px;
  font-size: var(--t-xs);
}
.group + .group {
  margin-top: 2px;
}
.ws-row {
  position: relative;
  display: flex;
  align-items: center;
  gap: 6px;
  height: 36px;
  padding: 0 6px 0 6px;
  border-radius: 9px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  cursor: pointer;
  user-select: none;
}
.ws-row:hover,
.ws-row.menuOpen {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
  color: var(--ink);
}
.caret {
  flex: none;
  color: var(--ink-faint);
  transition: transform 0.15s;
}
.caret.open {
  transform: rotate(90deg);
}
.folder {
  flex: none;
  color: var(--ink-faint);
}
.ws-row.selected .folder {
  color: var(--indigo);
}
.ws-row.selected .ws-name {
  color: var(--ink);
  font-weight: 500;
}
.ws-row.missing {
  opacity: 0.6;
}
.ws-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ws-actions {
  display: none;
  gap: 2px;
}
.ws-row:hover .ws-actions,
.ws-row.menuOpen .ws-actions {
  display: flex;
}
.ws-btn {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.ws-btn:hover {
  background: var(--chip);
  color: var(--ink);
}
.ws-row.drop-in {
  background: var(--indigo-wash);
  color: var(--indigo);
  box-shadow: inset 0 0 0 1px var(--indigo);
}
.drop-before::before,
.drop-after::after {
  content: '';
  position: absolute;
  left: 8px;
  right: 8px;
  height: 2px;
  border-radius: 1px;
  background: var(--indigo);
}
.drop-before::before {
  top: -1px;
}
.drop-after::after {
  bottom: -1px;
}
.item.nested {
  padding-left: 30px;
}
.item.dragging {
  opacity: 0.45;
}
.item {
  position: relative;
  display: flex;
  align-items: center;
  gap: 10px;
  height: 40px;
  padding: 0 8px 0 12px;
  border-radius: 10px;
  cursor: pointer;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.item:hover,
.item.menuOpen {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
  color: var(--ink);
}
.item.active {
  background: var(--cloth);
  color: var(--ink);
  box-shadow: 0 1px 2px rgba(24, 24, 27, 0.06);
}
.status {
  flex: none;
  display: grid;
  place-items: center;
  color: var(--ink-faint);
}
.status.running {
  color: var(--indigo);
}
.status.waiting {
  color: var(--amber);
}
.spin {
  animation: spin 900ms linear infinite;
}
.title {
  flex: 1;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.pin-mark {
  flex: none;
  color: var(--ink-faint);
}
.time {
  flex: none;
  font-size: calc(11.5px * var(--font-scale));
  color: var(--ink-faint);
}
.more {
  display: none;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.item:hover .time,
.item.menuOpen .time {
  display: none;
}
.item:hover .more,
.item.menuOpen .more,
.more:focus-visible {
  display: grid;
}
.more:hover {
  background: var(--chip);
  color: var(--ink);
}
.rename {
  flex: 1;
  min-width: 0;
  height: 28px;
  border: 1px solid var(--indigo);
  border-radius: 6px;
  padding: 0 6px;
  background: var(--cloth);
  outline: 0;
  font-size: var(--t-sm);
}
.back {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 12px;
  height: 38px;
  padding: 0 10px;
  border-radius: 10px;
  font-weight: 500;
}
.back:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.trash-hint {
  margin: 4px 24px 8px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.trash-item {
  cursor: default;
}
.icon-btn.small {
  width: 28px;
  height: 28px;
}
.icon-btn.danger:hover {
  color: var(--red);
}
.user {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 8px 12px 12px;
  padding: 8px 10px;
  border-radius: 12px;
  border: 1px solid var(--line);
  background: var(--cloth);
  text-align: left;
}
.user:hover {
  border-color: var(--line-strong);
}
.avatar {
  flex: none;
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 50%;
  background: var(--indigo);
  color: #fff;
  font-weight: 600;
}
.who {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.3;
}
.who strong {
  font-weight: 600;
  font-size: var(--t-sm);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.who small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.go {
  color: var(--ink-faint);
}
.more-card {
  position: fixed;
  z-index: 50;
  width: 216px;
  padding: 8px;
  border-radius: 14px;
  background: var(--cloth);
  border: 1px solid var(--line);
  box-shadow: var(--shadow-pop);
  animation: pop 120ms ease-out;
}
.more-card button {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 100%;
  height: 40px;
  padding: 0 12px;
  border-radius: 9px;
  color: var(--ink);
  font-size: var(--t-sm);
}
.more-card button:hover {
  background: var(--chip);
}
.more-card button.on {
  background: var(--indigo-wash);
  color: var(--indigo);
}
@keyframes pop {
  from {
    opacity: 0;
    transform: translateX(-4px);
  }
}
.menu {
  position: fixed;
  z-index: 50;
  min-width: 180px;
  padding: 6px;
  border-radius: var(--r-md);
  background: var(--cloth);
  border: 1px solid var(--line);
  box-shadow: var(--shadow-pop);
}
.menu button {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  height: 34px;
  padding: 0 10px;
  border-radius: var(--r-sm);
  font-size: var(--t-sm);
}
.menu button:hover {
  background: var(--chip);
}
.menu button.danger {
  color: var(--red);
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
