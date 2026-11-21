<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ShieldCheck, X } from '@lucide/vue'
import { state } from '../../store'
import { MASKED_KINDS, dismissPrivacy, visibleFor } from './store'

// 数据保护：这次对话里有敏感信息，服务端已换成占位符再发给模型。不打断任务，只让员工心里有数
const { t } = useI18n()
const info = computed(() => visibleFor(state.currentId))
const items = computed(() => {
  const p = info.value
  if (!p) return ''
  return MASKED_KINDS.filter((k) => (p.counts[k] ?? 0) > 0)
    .map((k) => t('ui.privacy.item', { label: t(`ui.privacy.kind.${k}`), n: p.counts[k] }))
    .join(t('ui.privacy.sep'))
})
const hasPersonal = computed(() => MASKED_KINDS.some((k) => k !== 'secret' && (info.value?.counts[k] ?? 0) > 0))
</script>

<template>
  <div v-if="info" class="privacy-notice" role="status" aria-live="polite">
    <ShieldCheck :size="18" class="icon" />
    <div class="text">
      <b>{{ t('ui.privacy.title') }}</b>
      <span>{{ t('ui.privacy.body', { items }) }}</span>
      <small>{{ hasPersonal ? t('ui.privacy.hint') : t('ui.privacy.hintSecret') }}</small>
    </div>
    <button type="button" class="ok" @click="dismissPrivacy(state.currentId!)">{{ t('ui.privacy.dismiss') }}</button>
    <button type="button" class="close" :aria-label="t('ui.privacy.dismiss')" @click="dismissPrivacy(state.currentId!)">
      <X :size="14" />
    </button>
  </div>
</template>

<style scoped>
.privacy-notice {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  max-width: var(--column);
  margin: 0 auto 8px;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--amber) 45%, var(--line));
  border-left: 4px solid var(--amber);
  border-radius: var(--r-lg);
  background: var(--amber-wash);
  font-size: calc(13px * var(--font-scale));
  color: var(--ink);
}
.icon {
  flex: none;
  margin-top: 1px;
  color: var(--amber);
}
.text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  line-height: 1.5;
}
.text b {
  font-weight: 600;
  color: var(--amber);
}
.text small {
  color: var(--ink-soft);
  font-size: calc(12px * var(--font-scale));
}
.ok {
  flex: none;
  align-self: center;
  padding: 4px 12px;
  border: 1px solid color-mix(in srgb, var(--amber) 45%, var(--line));
  border-radius: 999px;
  background: var(--cloth);
  color: inherit;
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.ok:hover {
  border-color: var(--amber);
}
.close {
  flex: none;
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border: 0;
  border-radius: 50%;
  background: transparent;
  color: inherit;
  cursor: pointer;
  opacity: 0.6;
}
.close:hover {
  opacity: 1;
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
</style>
