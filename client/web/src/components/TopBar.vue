<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Languages, Menu, MessageSquare, Minus, Pin, Wrench, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { current, draftMode, renameConversation, setMode, state } from '../store'
import type { Mode } from '../types'

defineProps<{ narrow: boolean }>()
const emit = defineEmits<{ toggleSidebar: [] }>()
const { t } = useI18n()

const editing = ref(false)
const text = ref('')
const input = ref<HTMLInputElement>()
const topmost = ref(false)

const mode = computed<Mode>(() => current.value?.mode ?? draftMode.mode)
const modes: { key: Mode; icon: unknown }[] = [
  { key: 'agent', icon: Wrench },
  { key: 'chat', icon: MessageSquare },
  { key: 'translate', icon: Languages },
]

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
</script>

<template>
  <header class="topbar">
    <button v-if="narrow" type="button" class="icon-btn no-drag" :aria-label="t('topbar.menu')" @click="emit('toggleSidebar')">
      <Menu :size="18" />
    </button>

    <div class="title-wrap">
      <input
        v-if="editing"
        ref="input"
        v-model="text"
        class="title-input no-drag"
        maxlength="100"
        @keydown.enter.prevent="commit"
        @keydown.esc.prevent="editing = false"
        @blur="commit"
      />
      <h1 v-else class="no-drag" :title="current ? t('topbar.rename') : undefined" @dblclick="startEdit">
        {{ current ? current.title || t('sidebar.untitled') : t('sidebar.newChat') }}
      </h1>
    </div>

    <div class="modes no-drag" role="radiogroup">
      <button
        v-for="m in modes"
        :key="m.key"
        type="button"
        role="radio"
        :aria-checked="mode === m.key"
        :class="{ on: mode === m.key }"
        :title="t(`mode.${m.key}Hint`)"
        @click="setMode(m.key)"
      >
        <component :is="m.icon" :size="15" />
        <span>{{ t(`mode.${m.key}`) }}</span>
      </button>
    </div>

    <div class="window no-drag">
      <span
        class="status"
        :class="{ off: !state.app?.connected }"
        :title="state.app?.connected ? t('status.connected', { model: state.app?.modelName }) : state.app?.serverMessage || t('status.offline')"
      />
      <button type="button" class="icon-btn" :class="{ active: topmost }" :title="t('topbar.pinWindow')" @click="pin">
        <Pin :size="16" />
      </button>
      <button type="button" class="icon-btn" :title="t('topbar.minimize')" @click="bridge.minimizeWindow()">
        <Minus :size="17" />
      </button>
      <button type="button" class="icon-btn" :title="t('topbar.close')" @click="bridge.hideWindow()">
        <X :size="17" />
      </button>
    </div>
  </header>
</template>

<style scoped>
.topbar {
  display: flex;
  align-items: center;
  gap: 12px;
  height: 56px;
  padding: 0 10px 0 20px;
  border-bottom: 1px solid var(--line);
  background: var(--loom);
  app-region: drag;
  -webkit-app-region: drag;
}
.no-drag,
.topbar button {
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
.title-wrap {
  flex: 1;
  min-width: 0;
}
h1 {
  display: inline-block;
  max-width: 100%;
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
.modes {
  display: flex;
  padding: 3px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  border: 1px solid var(--line);
}
.modes button {
  display: flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 12px;
  border-radius: 7px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 500;
}
.modes button:hover {
  color: var(--ink);
}
.modes button.on {
  background: var(--cloth);
  color: var(--indigo);
  box-shadow: 0 1px 2px rgba(26, 36, 51, 0.12);
}
.window {
  display: flex;
  align-items: center;
  gap: 2px;
}
.status {
  width: 8px;
  height: 8px;
  margin: 0 8px;
  border-radius: 50%;
  background: var(--thread);
}
.status.off {
  background: var(--red);
}
@media (max-width: 720px) {
  .modes span {
    display: none;
  }
  .modes button {
    padding: 0 9px;
  }
  .topbar {
    padding-left: 8px;
  }
}
</style>
