<script setup lang="ts">
import { currentState } from '../store'
import PlanPanel from './PlanPanel.vue'
import PreviewPane from './PreviewPane.vue'
import SessionPanel from './SessionPanel.vue'
import ChangesTab from './ChangesTab.vue'
import BrowserTab from './BrowserTab.vue'
import TerminalTab from './TerminalTab.vue'
import DocumentTab from './DocumentTab.vue'

/** 右侧面板里的一格：按标签显示对应的内容（分屏时上下两格各用一个） */
defineProps<{ tab: string }>()
</script>

<template>
  <DocumentTab v-if="tab === 'document'" class="body" />
  <PlanPanel v-else-if="tab === 'plan' && currentState" :plan="currentState.plan" class="body" />
  <PreviewPane v-else-if="tab === 'preview'" class="body" />
  <ChangesTab v-else-if="tab === 'changes'" class="body" />
  <BrowserTab v-else-if="tab === 'browser'" class="body" />
  <TerminalTab v-else-if="tab === 'terminal'" class="body" />
  <SessionPanel v-else class="body" />
</template>

<style scoped>
.body {
  flex: 1;
  min-height: 0;
}
</style>
