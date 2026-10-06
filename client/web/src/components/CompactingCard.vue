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
      <Archive :size="15" />
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
  max-width: var(--column);
  margin: 10px auto;
  padding: 10px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth-soft, var(--cloth));
}
.line {
  display: flex;
  gap: 8px;
  align-items: center;
  font-size: calc(13px * var(--font-scale));
  color: var(--ink-soft);
}
.line span:first-of-type {
  flex: 1;
}
.pct {
  font-variant-numeric: tabular-nums;
}
.track {
  margin-top: 8px;
  height: 4px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
  overflow: hidden;
}
.fill {
  height: 100%;
  border-radius: 999px;
  background: var(--indigo);
  transition: width 0.25s ease-out;
}
</style>
