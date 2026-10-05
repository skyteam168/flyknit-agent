<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Copy, Languages, Menu, MessageSquare, Minus, PanelLeft, Pin, Plus, Square, Wrench, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { current, newConversation, renameConversation, state, toggleMaximize } from '../store'
import type { Mode } from '../types'

defineProps<{ narrow: boolean; sidebarHidden: boolean }>()
const emit = defineEmits<{ toggleSidebar: [] }>()
const { t } = useI18n()

const editing = ref(false)
const text = ref('')
const input = ref<HTMLInputElement>()
const topmost = ref(false)

const modeIcon: Record<Mode, unknown> = { agent: Wrench, chat: MessageSquare, translate: Languages }
const icon = computed(() => (current.value ? modeIcon[current.value.mode] : null))

async function startEdit() {
  if (!current.value) return
  editing.value = true
  text.value = current.value.title || t('sidebar.untitled')
  await nextTick()
  input.value?.select()
}

async function commit() {
  if (!editing.value || !current.value) return
  editing.value = false
  await renameConversation(current.value.id, text.value)
}

async function pin() {
  topmost.value = await bridge.toggleTopmost()
}

function startNew() {
  newConversation()
  window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
}
</script>

<template>
  <header class="topbar" @dblclick.self="toggleMaximize">
    <button type="button" class="icon-btn" :aria-label="t('topbar.menu')" @click="emit('toggleSidebar')">
      <component :is="narrow ? Menu : PanelLeft" :size="18" />
    </button>
    <button v-if="sidebarHidden && current" type="button" class="icon-btn" :title="t('sidebar.newChat')" @click="startNew">
      <Plus :size="18" />
    </button>

    <div class="title-wrap">
      <template v-if="current">
        <span class="mode-chip"><component :is="icon" :size="14" />{{ t(`mode.${current.mode}`) }}</span>
        <input
          v-if="editing"
          ref="input"
          v-model="text"
          class="title-input"
          maxlength="100"
          @keydown.enter.prevent="commit"
          @keydown.esc.prevent="editing = false"
          @blur="commit"
        />
        <h1 v-else :title="t('topbar.rename')" @dblclick="startEdit">{{ current.title || t('sidebar.untitled') }}</h1>
      </template>
    </div>

    <div class="window">
      <button type="button" class="icon-btn" :class="{ active: topmost }" :title="t('topbar.pinWindow')" @click="pin"><Pin :size="16" /></button>
      <button type="button" class="icon-btn" :title="t('topbar.minimize')" @click="bridge.minimizeWindow()"><Minus :size="17" /></button>
      <button
        type="button"
        class="icon-btn"
        :title="state.maximized ? t('topbar.restore') : t('topbar.maximize')"
        @click="toggleMaximize"
      >
        <component :is="state.maximized ? Copy : Square" :size="state.maximized ? 14 : 13" :class="{ flip: state.maximized }" />
      </button>
      <button type="button" class="icon-btn" :title="t('topbar.close')" @click="bridge.hideWindow()"><X :size="17" /></button>
    </div>
  </header>
</template>

<style scoped>
.flip {
  transform: scaleX(-1);
}
.topbar {
  display: flex;
  align-items: center;
  gap: 6px;
  height: 52px;
  padding: 0 10px;
  background: var(--loom);
  app-region: drag;
  -webkit-app-region: drag;
}
.topbar button,
.title-input,
h1 {
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
.title-wrap {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 10px;
  padding-left: 6px;
}
.mode-chip {
  flex: none;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 24px;
  padding: 0 9px;
  border-radius: 12px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-xs);
  font-weight: 500;
}
h1 {
  min-width: 0;
  margin: 0;
  font-size: var(--t-md);
  font-weight: 600;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: text;
}
.title-input {
  width: min(360px, 100%);
  height: 30px;
  padding: 0 8px;
  border: 1px solid var(--indigo);
  border-radius: var(--r-sm);
  background: var(--cloth);
  font-weight: 600;
  outline: 0;
}
.window {
  display: flex;
  align-items: center;
  gap: 2px;
}
</style>
