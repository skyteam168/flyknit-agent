<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Info, X } from '@lucide/vue'
import { dismissUsageWarning, showUsageWarning, state, usagePercent } from '../store'

// 今日额度用到 90% 时在输入框上方提醒，用完之前就让用户有时间找 IT 加额度
const { t } = useI18n()

const exceeded = computed(() => state.usage?.exceeded ?? false)
const contact = computed(() => {
  const u = state.usage
  if (!u?.contactEmail && !u?.contactPhone) return ''
  return [u?.contactName, u?.contactEmail, u?.contactPhone && t('ui.usage.phone', { n: u.contactPhone })]
    .filter(Boolean)
    .join(' · ')
})
</script>

<template>
  <div v-if="showUsageWarning" class="usage-warning" :class="{ exceeded }" role="status">
    <Info :size="15" />
    <span class="text">
      {{ exceeded ? t('ui.usage.bannerExceeded') : t('ui.usage.bannerNear', { p: usagePercent }) }}
      <small v-if="contact">{{ t('ui.usage.bannerContact', { who: contact }) }}</small>
    </span>
    <button type="button" class="more" @click="state.usageOpen = true">{{ t('ui.usage.bannerDetail') }}</button>
    <button type="button" class="close" :aria-label="t('ui.usage.bannerDismiss')" @click="dismissUsageWarning()">
      <X :size="14" />
    </button>
  </div>
</template>

<style scoped>
.usage-warning {
  display: flex;
  gap: 10px;
  align-items: center;
  max-width: var(--column);
  margin: 0 auto 8px;
  padding: 9px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth-soft, var(--cloth));
  font-size: calc(13px * var(--font-scale));
  color: var(--ink-soft);
}
.usage-warning.exceeded {
  border-color: color-mix(in srgb, var(--amber) 50%, var(--line));
  background: var(--amber-wash);
}
.text {
  flex: 1;
  min-width: 0;
}
.text small {
  display: block;
  margin-top: 2px;
  opacity: 0.75;
}
.more {
  flex: none;
  padding: 4px 10px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth);
  color: inherit;
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.more:hover {
  border-color: var(--accent);
  color: var(--accent);
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
