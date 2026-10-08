<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Archive } from '@lucide/vue'
import { state } from '../store'

// 上下文压缩进度：对话太长时会把较早的内容提炼成摘要，这期间让用户知道在干什么
const { t } = useI18n()

const info = computed(() => state.compacting)
const label = computed(() => {
  const c = info.value
  if (!c) return ''
  if (c.phase === 'done') return t('ui.context.compactDone', { n: c.messages })
  if (c.phase === 'scanning') return t('ui.context.compactScanning')
  return t('ui.context.compacting')
})
</script>

<template>
  <div v-if="info" class="compacting" role="status" aria-live="polite">
    <div class="line">
      <Archive :size="13" />
      <span>{{ label }}</span>
      <span class="pct">{{ info.percent }}%</span>
    </div>
    <div class="track" :aria-valuenow="info.percent" aria-valuemin="0" aria-valuemax="100" role="progressbar">
      <div class="fill" :style="{ width: info.percent + '%' }" />
    </div>
  </div>
</template>

<style scoped>
.compacting {
  width: 220px;
  margin: 12px auto;
  padding: 8px 12px;
  border-radius: var(--r-md, 8px);
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.line {
  display: flex;
  gap: 6px;
  align-items: center;
  font-size: calc(12px * var(--font-scale, 1));
  color: var(--ink-soft);
}
.line span:first-of-type {
  flex: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.pct {
  font-size: calc(11px * var(--font-scale, 1));
  font-variant-numeric: tabular-nums;
  opacity: 0.7;
}
.track {
  margin-top: 6px;
  height: 3px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--ink) 12%, transparent);
  overflow: hidden;
}
.fill {
  height: 100%;
  border-radius: 999px;
  background: var(--indigo);
  transition: width 0.2s ease-out;
}
</style>
