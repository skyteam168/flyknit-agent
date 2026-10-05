<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowLeft,
  Brain,
  Check,
  ChevronRight,
  Hand,
  Languages,
  LoaderCircle,
  MessageSquare,
  MoreHorizontal,
  Pencil,
  Pin,
  PinOff,
  Plus,
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
import Popover from './Popover.vue'
import { bridge } from '../bridge'
import {
  deleteConversation,
  newConversation,
  openConversation,
  purgeConversation,
  refreshList,
  renameConversation,
  restoreConversation,
  runStatus,
  state,
  togglePin,
} from '../store'
import type { Conversation, Mode } from '../types'

const emit = defineEmits<{ navigate: [] }>()
const { t } = useI18n()

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

const items = computed(() =>
  state.conversations.filter((c) => state.filter === 'all' || c.mode === state.filter),
)

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
  menuPos.value = { x: Math.min(rect.left, window.innerWidth - 196), y: Math.min(rect.bottom + 4, window.innerHeight - 140) }
  menuFor.value = menuFor.value === c.id ? null : c.id
}

function onContext(c: Conversation, e: MouseEvent) {
  e.preventDefault()
  menuPos.value = { x: Math.min(e.clientX, window.innerWidth - 196), y: Math.min(e.clientY, window.innerHeight - 140) }
  menuFor.value = c.id
}

const menuConversation = computed(() => [...state.conversations, ...state.trash].find((c) => c.id === menuFor.value) ?? null)

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

const closeMenu = (e: MouseEvent) => {
  if (!(e.target as HTMLElement).closest('.menu, .more')) menuFor.value = null
}
onMounted(() => {
  document.addEventListener('mousedown', closeMenu)
  clock = window.setInterval(() => (now.value = Date.now()), 60000)
})
onBeforeUnmount(() => {
  document.removeEventListener('mousedown', closeMenu)
  clearInterval(clock)
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

    <div class="search-row">
      <label class="search">
        <Search :size="15" />
        <input v-model="state.query" type="search" :placeholder="t('sidebar.search')" />
        <button v-if="state.query" type="button" class="clear" @click="state.query = ''"><X :size="14" /></button>
      </label>
      <Popover placement="down" align="end" :width="168">
        <template #trigger="{ toggle, open }">
          <button type="button" class="filter-btn" :class="{ open, active: state.filter !== 'all' }" :title="t('ui.filter.all')" @click="toggle">
            <SlidersHorizontal :size="16" />
          </button>
        </template>
        <template #default="{ close }">
          <button
            v-for="f in filters"
            :key="f"
            type="button"
            class="filter-opt"
            @click="((state.filter = f), close())"
          >
            <span>{{ t(`ui.filter.${f}`) }}</span>
            <Check v-if="state.filter === f" :size="14" />
          </button>
        </template>
      </Popover>
    </div>

    <template v-if="!state.showTrash">
      <button class="new-task" type="button" :class="{ on: state.currentId === null }" @click="startNew">
        <Plus :size="18" />
        <span>{{ t('sidebar.newChat') }}</span>
        <kbd>Ctrl N</kbd>
      </button>

      <nav class="nav">
        <button type="button" @click="state.skillsOpen = true"><Puzzle :size="17" /> {{ t('ui.nav.skills') }}</button>
        <button type="button" @click="bridge.openMemoryFolder()"><Brain :size="17" /> {{ t('ui.nav.memory') }}</button>
        <button type="button" @click="toggleTrash"><Trash2 :size="17" /> {{ t('ui.nav.trash') }}</button>
      </nav>

      <h3 class="section">{{ state.filter === 'all' ? t('ui.nav.tasks') : t(`ui.filter.${state.filter}`) }}</h3>
      <div class="list" role="list">
        <p v-if="items.length === 0" class="empty">{{ state.query ? t('sidebar.noResults') : t('sidebar.empty') }}</p>
        <div
          v-for="c in items"
          :key="c.id"
          class="item"
          role="listitem"
          tabindex="0"
          :class="{ active: c.id === state.currentId, menuOpen: menuFor === c.id }"
          @click="select(c)"
          @keydown.enter="select(c)"
          @keydown.f2.prevent="startRename(c)"
          @dblclick="startRename(c)"
          @contextmenu="onContext(c, $event)"
        >
          <span class="status" :class="runStatus(c.id)">
            <LoaderCircle v-if="runStatus(c.id) === 'running'" :size="15" class="spin" />
            <Hand v-else-if="runStatus(c.id) === 'waiting'" :size="15" />
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

    <button type="button" class="user" @click="state.settingsOpen = true">
      <span class="avatar">{{ initial }}</span>
      <span class="who">
        <strong>{{ state.app?.userName }}</strong>
        <small>{{ state.app?.machineName }}</small>
      </span>
      <ChevronRight :size="16" class="go" />
    </button>

    <Teleport to="body">
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
  font-size: 11px;
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
.search-row {
  display: flex;
  gap: 6px;
  padding: 4px 12px 10px;
}
.search {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 8px;
  height: 36px;
  padding: 0 10px;
  border-radius: 9px;
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
.filter-btn {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  border-radius: 9px;
  border: 1px solid var(--line);
  background: var(--cloth);
  color: var(--ink-soft);
}
.filter-btn.active {
  color: var(--indigo);
  border-color: var(--indigo);
}
.filter-opt {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  height: 34px;
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
  font-size: 11px;
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
.nav button:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
  color: var(--ink);
}
.section {
  margin: 18px 24px 6px;
  font-size: var(--t-sm);
  font-weight: 500;
  color: var(--ink-faint);
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
  font-size: 11.5px;
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
