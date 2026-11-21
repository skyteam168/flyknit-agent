<script setup lang="ts">
/**
 * 编码模式右边 AI Agent 顶上的一行（参照 Cursor）：打开过的任务标签、新建任务（+）、历史（同一个工作区的任务）。
 * 新建、切换都留在编码模式里；关掉标签只是不显示，任务还在左侧列表和历史里。
 */
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { History, MessageSquare, Plus, X } from '@lucide/vue'
import { state } from '../../store'
import { agentTabs, closeAgentTab, newTaskInIde, noteTab, switchTask, workspaceTasks } from './store'

const { t } = useI18n()
watch(() => state.currentId, (id) => noteTab(id), { immediate: true })

const tabs = computed(() => agentTabs.ids.map((id) => state.conversations.find((c) => c.id === id)).filter((c) => !!c))
const historyOpen = ref(false)
const query = ref('')
const history = computed(() => {
  const q = query.value.trim().toLowerCase()
  return workspaceTasks.value.filter((c) => !q || (c.title || '').toLowerCase().includes(q)).slice(0, 50)
})

function when(at: string) {
  const d = new Date(at)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

function pick(id: string) {
  historyOpen.value = false
  query.value = ''
  switchTask(id)
}
</script>

<template>
  <div class="agent-tabs">
    <div class="tabs" role="tablist">
      <div
        v-for="c in tabs"
        :key="c!.id"
        class="tab"
        :class="{ on: c!.id === state.currentId }"
        role="tab"
        :aria-selected="c!.id === state.currentId"
        :title="c!.title || t('ui.code.agent.untitled')"
        @click="switchTask(c!.id)"
      >
        <MessageSquare :size="12" />
        <span class="name">{{ c!.title || t('ui.code.agent.untitled') }}</span>
        <button type="button" class="x" :aria-label="t('ui.code.close')" @click.stop="closeAgentTab(c!.id)"><X :size="11" /></button>
      </div>
    </div>
    <button type="button" class="icon" :title="t('ui.code.agent.newTask')" @click="newTaskInIde()"><Plus :size="15" /></button>
    <div class="history-wrap">
      <button type="button" class="icon" :class="{ on: historyOpen }" :title="t('ui.code.agent.history')" @click="historyOpen = !historyOpen"><History :size="15" /></button>
      <div v-if="historyOpen" class="history" @keydown.esc="historyOpen = false">
        <input v-model="query" :placeholder="t('ui.code.agent.search')" />
        <button v-for="c in history" :key="c.id" type="button" class="row" :class="{ on: c.id === state.currentId }" @click="pick(c.id)">
          <span class="title">{{ c.title || t('ui.code.agent.untitled') }}</span>
          <span class="time">{{ when(c.updatedAt) }}</span>
        </button>
        <p v-if="!history.length" class="none">{{ t('ui.code.empty') }}</p>
      </div>
    </div>
  </div>
  <div v-if="historyOpen" class="scrim" @click="historyOpen = false" />
</template>

<style scoped>
.agent-tabs {
  position: relative;
  display: flex;
  flex: none;
  align-items: center;
  gap: 2px;
  height: 34px;
  padding: 0 6px 0 0;
  border-bottom: 1px solid var(--line);
  background: var(--loom);
  z-index: 20;
}
.tabs {
  display: flex;
  flex: 1;
  min-width: 0;
  height: 100%;
  overflow-x: auto;
  scrollbar-width: none;
}
.tab {
  display: flex;
  flex: 0 1 auto;
  align-items: center;
  gap: 5px;
  min-width: 70px;
  max-width: 180px;
  padding: 0 4px 0 10px;
  border-right: 1px solid var(--line);
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.tab.on {
  background: var(--cloth);
  color: var(--ink);
  box-shadow: inset 0 2px 0 var(--indigo);
}
.tab .name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.x,
.icon {
  display: grid;
  flex: none;
  place-items: center;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: inherit;
  cursor: pointer;
}
.x {
  width: 18px;
  height: 18px;
  opacity: 0;
}
.tab:hover .x,
.tab.on .x {
  opacity: 0.6;
}
.x:hover {
  opacity: 1 !important;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
}
.icon {
  width: 26px;
  height: 26px;
  color: var(--ink-soft);
}
.icon:hover,
.icon.on {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
  color: var(--ink);
}
.history-wrap {
  position: relative;
}
.history {
  position: absolute;
  top: 30px;
  right: 0;
  width: 300px;
  max-height: 420px;
  overflow-y: auto;
  padding: 6px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  z-index: 30;
}
.history input {
  width: 100%;
  margin-bottom: 4px;
  padding: 5px 8px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--loom);
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
}
.row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
  text-align: left;
  cursor: pointer;
}
.row:hover,
.row.on {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.time {
  flex: none;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
}
.none {
  margin: 6px 8px;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
}
.scrim {
  position: fixed;
  inset: 0;
  z-index: 19;
}
</style>
