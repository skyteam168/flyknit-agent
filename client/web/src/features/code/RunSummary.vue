<script setup lang="ts">
/**
 * 输入框上方的一行汇总（参照 Cursor）：后台有几个终端还在跑、最近一轮改了几个文件。
 * 点终端：编码模式下展开底部终端，否则打开右侧「终端」标签；点「查看」：打开这一轮的改动（对比、还原）。
 */
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, FilePen, SquareTerminal } from '@lucide/vue'
import { current, openChanges, state } from '../../store'
import { ide, ideActive } from './store'

const { t } = useI18n()

const running = computed(() => (current.value ? (state.terminal[current.value.id] ?? []).filter((r) => r.running && r.background).length : 0))
const lastTurn = computed(() => {
  const turns = current.value ? (state.changes[current.value.id] ?? []) : []
  for (let i = turns.length - 1; i >= 0; i--) if (turns[i].files.length) return turns[i]
  return null
})
const files = computed(() => lastTurn.value?.files.filter((f) => !f.reverted).length ?? 0)

function showTerminals() {
  if (ideActive.value) {
    ide.bottomOpen = true
    ide.bottomTab = 'terminal'
  } else {
    state.sessionPanel = true
    state.rightClosed = false
    state.rightTab = 'terminal'
  }
}
</script>

<template>
  <div v-if="current && (running || files)" class="run-summary">
    <button v-if="running" type="button" @click="showTerminals">
      <ChevronRight :size="13" class="chev" />
      <SquareTerminal :size="13" />
      {{ t('ui.code.backgroundTerminals', { n: running }) }}
    </button>
    <button v-if="files && lastTurn" type="button" @click="openChanges(current.id, lastTurn.turnId)">
      <ChevronRight :size="13" class="chev" />
      <FilePen :size="13" />
      {{ t('ui.code.filesChanged', { n: files }) }}
      <span class="review">{{ t('ui.code.review') }}</span>
    </button>
  </div>
</template>

<style scoped>
.run-summary {
  display: flex;
  flex-direction: column;
  max-width: var(--column);
  margin: 0 auto 6px;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--loom);
}
.run-summary button {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border: 0;
  background: transparent;
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
  text-align: left;
  cursor: pointer;
}
.run-summary button + button {
  border-top: 1px solid var(--line);
}
.run-summary button:hover {
  color: var(--ink);
  background: color-mix(in srgb, var(--ink) 4%, transparent);
}
.chev {
  color: var(--ink-faint);
}
.review {
  margin-left: auto;
  padding: 1px 10px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
}
</style>
