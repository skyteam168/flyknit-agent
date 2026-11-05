<script setup lang="ts">
import { computed, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Eye, FilePen, FileText, Globe, Info, ListChecks, SquareTerminal, X } from '@lucide/vue'
import { closeRightPanel, current, currentState, state } from '../store'
import PlanPanel from './PlanPanel.vue'
import PreviewPane from './PreviewPane.vue'
import SessionPanel from './SessionPanel.vue'
import ChangesTab from './ChangesTab.vue'
import BrowserTab from './BrowserTab.vue'
import TerminalTab from './TerminalTab.vue'
import DocumentTab from './DocumentTab.vue'

/**
 * 右侧面板：计划、文件预览、改动的文件、AI 浏览器、终端、会话信息放在同一块地方，用标签切换。
 * 哪几个标签出现看当前有没有内容（没有计划就没有「计划」），显示哪个由 state.rightTab 决定。
 */
const { t } = useI18n()

type Tab = 'document' | 'plan' | 'preview' | 'changes' | 'browser' | 'terminal' | 'session'
const icons = { document: FileText, plan: ListChecks, preview: Eye, changes: FilePen, browser: Globe, terminal: SquareTerminal, session: Info }

const tabs = computed<Tab[]>(() => {
  const list: Tab[] = []
  const id = current.value?.id
  if (id && state.docs[id]) list.push('document')
  if (currentState.value?.plan.length) list.push('plan')
  if (state.preview) list.push('preview')
  if (id && state.changes[id]?.some((x) => x.files.length)) list.push('changes')
  if (state.browser.active) list.push('browser')
  if (id && state.terminal[id]?.length) list.push('terminal')
  list.push('session')
  return list
})

const active = computed<Tab>(() => (tabs.value.includes(state.rightTab as Tab) ? (state.rightTab as Tab) : tabs.value[0]))

// 新出现计划、打开了预览时自动切过去（只在面板本来没选中有效标签时）
watch(
  () => tabs.value.join(),
  () => {
    if (!tabs.value.includes(state.rightTab as Tab)) state.rightTab = tabs.value[0]
  },
)
</script>

<template>
  <div class="right" :aria-label="t('ui.right.title')">
    <nav class="tabs" role="tablist">
      <button
        v-for="tab in tabs"
        :key="tab"
        type="button"
        role="tab"
        class="tab"
        :class="{ on: tab === active }"
        :aria-selected="tab === active"
        :title="t(`ui.right.${tab}`)"
        @click="state.rightTab = tab"
      >
        <component :is="icons[tab]" :size="14" />
        <span>{{ t(`ui.right.${tab}`) }}</span>
      </button>
      <span class="spacer" />
      <button type="button" class="close" :title="t('ui.right.close')" :aria-label="t('ui.right.close')" @click="closeRightPanel">
        <X :size="14" />
      </button>
    </nav>
    <DocumentTab v-if="active === 'document'" class="body" />
    <PlanPanel v-else-if="active === 'plan'" :plan="currentState!.plan" class="body" />
    <PreviewPane v-else-if="active === 'preview'" class="body" />
    <ChangesTab v-else-if="active === 'changes'" class="body" />
    <BrowserTab v-else-if="active === 'browser'" class="body" />
    <TerminalTab v-else-if="active === 'terminal'" class="body" />
    <SessionPanel v-else class="body" />
  </div>
</template>

<style scoped>
.right {
  display: flex;
  flex: none;
  flex-direction: column;
  min-height: 0;
  max-width: 70vw;
}
.tabs {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 6px 6px 0;
  border-left: 1px solid var(--line);
  border-bottom: 1px solid var(--line);
  background: var(--loom);
  overflow-x: auto;
}
.tab {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  padding: 5px 9px 7px;
  border-bottom: 2px solid transparent;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.tab:hover {
  color: var(--ink-soft);
}
.tab.on {
  border-bottom-color: var(--indigo);
  color: var(--ink);
}
.spacer {
  flex: 1;
}
.close {
  display: grid;
  flex: none;
  place-items: center;
  width: 24px;
  height: 24px;
  margin-bottom: 4px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.close:hover {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.body {
  flex: 1;
  min-height: 0;
}
</style>
