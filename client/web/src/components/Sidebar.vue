<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowLeft,
  Languages,
  MessageSquare,
  MoreHorizontal,
  Pencil,
  Pin,
  PinOff,
  RotateCcw,
  Search,
  Settings,
  SquarePen,
  Trash2,
  Wrench,
  X,
} from '@lucide/vue'
import KnitMark from './KnitMark.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import {
  deleteConversation,
  newConversation,
  openConversation,
  purgeConversation,
  refreshList,
  renameConversation,
  restoreConversation,
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

const modeIcon: Record<Mode, unknown> = { chat: MessageSquare, translate: Languages, agent: Wrench }

let searchTimer: number | undefined
watch(
  () => state.query,
  () => {
    clearTimeout(searchTimer)
    searchTimer = window.setTimeout(refreshList, 200)
  },
)

const groups = computed(() => {
  const startOfDay = new Date()
  startOfDay.setHours(0, 0, 0, 0)
  const day = 86400000
  const buckets: { key: string; label: string; items: Conversation[] }[] = [
    { key: 'pinned', label: t('sidebar.pinned'), items: [] },
    { key: 'today', label: t('sidebar.today'), items: [] },
    { key: 'yesterday', label: t('sidebar.yesterday'), items: [] },
    { key: 'week', label: t('sidebar.week'), items: [] },
    { key: 'earlier', label: t('sidebar.earlier'), items: [] },
  ]
  for (const c of state.conversations) {
    const ts = new Date(c.updatedAt).getTime()
    const b = c.pinned
      ? 0
      : ts >= startOfDay.getTime()
        ? 1
        : ts >= startOfDay.getTime() - day
          ? 2
          : ts >= startOfDay.getTime() - 6 * day
            ? 3
            : 4
    buckets[b].items.push(c)
  }
  return buckets.filter((b) => b.items.length > 0)
})

function select(c: Conversation) {
  if (editingId.value === c.id) return
  void openConversation(c.id)
  emit('navigate')
}

function startNew() {
  newConversation()
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
  menuPos.value = { x: Math.min(rect.left, window.innerWidth - 196), y: rect.bottom + 4 }
  menuFor.value = menuFor.value === c.id ? null : c.id
}

function onContext(c: Conversation, e: MouseEvent) {
  e.preventDefault()
  menuPos.value = { x: Math.min(e.clientX, window.innerWidth - 196), y: Math.min(e.clientY, window.innerHeight - 160) }
  menuFor.value = c.id
}

const menuConversation = computed(() =>
  [...state.conversations, ...state.trash].find((c) => c.id === menuFor.value) ?? null,
)

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
onMounted(() => document.addEventListener('mousedown', closeMenu))
onBeforeUnmount(() => document.removeEventListener('mousedown', closeMenu))
</script>

<template>
  <aside class="sidebar">
    <header class="brand">
      <KnitMark :size="30" />
      <div class="brand-text">
        <strong>{{ t('app.short') }}</strong>
        <span>{{ t('app.name') }}</span>
      </div>
    </header>

    <template v-if="!state.showTrash">
      <button class="new-chat" type="button" @click="startNew">
        <SquarePen :size="17" />
        <span class="label">{{ t('sidebar.newChat') }}</span>
        <kbd>Ctrl N</kbd>
      </button>

      <label class="search">
        <Search :size="15" />
        <input v-model="state.query" type="search" :placeholder="t('sidebar.search')" />
        <button v-if="state.query" type="button" class="clear" :aria-label="t('confirmDelete.cancel')" @click="state.query = ''">
          <X :size="14" />
        </button>
      </label>

      <nav class="list" :aria-label="t('topbar.menu')">
        <p v-if="state.conversations.length === 0" class="empty">
          {{ state.query ? t('sidebar.noResults') : t('sidebar.empty') }}
        </p>
        <section v-for="g in groups" :key="g.key">
          <h3>{{ g.label }}</h3>
          <div
            v-for="c in g.items"
            :key="c.id"
            class="item"
            :class="{ active: c.id === state.currentId, menuOpen: menuFor === c.id }"
            role="button"
            tabindex="0"
            @click="select(c)"
            @keydown.enter="select(c)"
            @keydown.f2.prevent="startRename(c)"
            @dblclick="startRename(c)"
            @contextmenu="onContext(c, $event)"
          >
            <component :is="modeIcon[c.mode]" :size="15" class="mode-icon" />
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
            <Pin v-if="c.pinned && editingId !== c.id" :size="13" class="pin-mark" />
            <button type="button" class="more" :aria-label="t('menu.rename')" @click.stop="openMenu(c, $event)">
              <MoreHorizontal :size="16" />
            </button>
          </div>
        </section>
      </nav>
    </template>

    <template v-else>
      <button class="back" type="button" @click="toggleTrash">
        <ArrowLeft :size="16" /> {{ t('sidebar.backToList') }}
      </button>
      <p class="trash-hint">{{ t('sidebar.trashHint') }}</p>
      <nav class="list">
        <p v-if="state.trash.length === 0" class="empty">{{ t('sidebar.empty') }}</p>
        <div v-for="c in state.trash" :key="c.id" class="item trash-item" @contextmenu="onContext(c, $event)">
          <component :is="modeIcon[c.mode]" :size="15" class="mode-icon" />
          <span class="title">{{ c.title || t('sidebar.untitled') }}</span>
          <button type="button" class="icon-btn small" :title="t('menu.restore')" @click="restoreConversation(c.id)">
            <RotateCcw :size="15" />
          </button>
          <button type="button" class="icon-btn small danger" :title="t('menu.purge')" @click="confirm = { id: c.id, purge: true }">
            <Trash2 :size="15" />
          </button>
        </div>
      </nav>
    </template>

    <footer class="foot">
      <button type="button" class="foot-btn" @click="state.settingsOpen = true">
        <Settings :size="16" /> {{ t('sidebar.settings') }}
      </button>
      <button v-if="!state.showTrash" type="button" class="icon-btn" :title="t('sidebar.trash')" @click="toggleTrash">
        <Trash2 :size="16" />
      </button>
    </footer>

    <Teleport to="body">
      <div v-if="menuConversation" class="menu" :style="{ left: `${menuPos.x}px`, top: `${menuPos.y}px` }" role="menu">
        <template v-if="!menuConversation.deletedAt">
          <button role="menuitem" type="button" @click="startRename(menuConversation)">
            <Pencil :size="15" /> {{ t('menu.rename') }}
          </button>
          <button role="menuitem" type="button" @click="(togglePin(menuConversation), (menuFor = null))">
            <component :is="menuConversation.pinned ? PinOff : Pin" :size="15" />
            {{ menuConversation.pinned ? t('menu.unpin') : t('menu.pin') }}
          </button>
          <button role="menuitem" type="button" class="danger" @click="((confirm = { id: menuConversation.id, purge: false }), (menuFor = null))">
            <Trash2 :size="15" /> {{ t('menu.delete') }}
          </button>
        </template>
        <template v-else>
          <button role="menuitem" type="button" @click="(restoreConversation(menuConversation.id), (menuFor = null))">
            <RotateCcw :size="15" /> {{ t('menu.restore') }}
          </button>
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
  background: var(--cloth-sunk);
  border-right: 1px solid var(--line);
  min-width: 0;
}
.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 16px 16px 12px;
}
.brand-text {
  display: flex;
  flex-direction: column;
  line-height: 1.2;
  min-width: 0;
}
.brand-text strong {
  font-size: var(--t-lg);
  font-weight: 600;
  letter-spacing: -0.01em;
}
.brand-text span {
  font-size: var(--t-xs);
  color: var(--ink-faint);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.new-chat {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 4px 12px 8px;
  height: 40px;
  padding: 0 12px;
  border-radius: var(--r-md);
  background: var(--cloth);
  border: 1px solid var(--line);
  font-weight: 500;
}
.new-chat:hover {
  border-color: var(--indigo);
  color: var(--indigo);
}
.new-chat .label {
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.new-chat kbd {
  white-space: nowrap;
  margin-left: auto;
  font-family: inherit;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.search {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 12px 6px;
  padding: 0 10px;
  height: 34px;
  border-radius: var(--r-sm);
  color: var(--ink-faint);
}
.search:focus-within {
  background: var(--cloth);
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
.list {
  flex: 1;
  overflow-y: auto;
  padding: 0 8px 12px;
}
.list h3 {
  margin: 14px 8px 4px;
  font-size: var(--t-xs);
  font-weight: 500;
  color: var(--ink-faint);
}
.empty {
  margin: 24px 12px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.item {
  position: relative;
  display: flex;
  align-items: center;
  gap: 9px;
  height: 36px;
  padding: 0 6px 0 10px;
  border-radius: var(--r-sm);
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
  box-shadow: inset 2px 0 0 var(--indigo);
}
.mode-icon {
  flex: none;
  color: var(--ink-faint);
}
.item.active .mode-icon {
  color: var(--indigo);
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
.more {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: var(--r-sm);
  color: var(--ink-faint);
  opacity: 0;
}
.item:hover .more,
.item.active .more,
.item.menuOpen .more,
.more:focus-visible {
  opacity: 1;
}
.more:hover {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.rename {
  flex: 1;
  min-width: 0;
  height: 26px;
  border: 1px solid var(--indigo);
  border-radius: 4px;
  padding: 0 6px;
  background: var(--cloth);
  outline: 0;
  font-size: var(--t-sm);
}
.back {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 4px 12px 0;
  height: 36px;
  padding: 0 8px;
  border-radius: var(--r-sm);
  font-weight: 500;
}
.back:hover {
  background: var(--cloth);
}
.trash-hint {
  margin: 4px 20px 8px;
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
.foot {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 10px 12px;
  border-top: 1px solid var(--line);
}
.foot-btn {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 8px;
  height: 34px;
  padding: 0 8px;
  border-radius: var(--r-sm);
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.foot-btn:hover {
  background: var(--cloth);
  color: var(--ink);
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
  background: var(--cloth-sunk);
}
.menu button.danger {
  color: var(--red);
}
</style>
