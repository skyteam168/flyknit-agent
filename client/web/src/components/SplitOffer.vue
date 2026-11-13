<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { Split, X } from '@lucide/vue'
import { acceptSplit, declineSplit, state } from '../store'

/**
 * 拆分建议（docs/c2-router.md 3.3）：员工一句话里像有好几件互相独立的事时，问一句要不要分开做。
 * 不自动拆：员工点「分开做」才拆，点「就当一件做」照常发送。
 */
const { t } = useI18n()
</script>

<template>
  <div v-if="state.splitOffer" class="split" role="dialog" :aria-label="t('ui.router.offerTitle', { n: state.splitOffer.items.length })">
    <div class="head">
      <Split :size="15" />
      <strong>{{ t('ui.router.offerTitle', { n: state.splitOffer.items.length }) }}</strong>
      <button type="button" class="x" :title="t('ui.router.keepOne')" @click="declineSplit"><X :size="14" /></button>
    </div>
    <p class="hint">{{ t('ui.router.offerHint') }}</p>
    <p v-if="state.splitOffer.isolation" class="iso">{{ t(`ui.router.isolation.${state.splitOffer.isolation}`) }}</p>
    <ol>
      <li v-for="(it, i) in state.splitOffer.items" :key="i">
        <strong>{{ it.title }}</strong>
        <span class="muted">{{ it.instruction }}</span>
      </li>
    </ol>
    <p v-if="state.splitOffer.final" class="final">
      <strong>{{ t('ui.router.finalStep') }}</strong>{{ state.splitOffer.final.title }}
      <span class="muted">{{ t('ui.router.finalHint') }}</span>
    </p>
    <div class="actions">
      <button type="button" class="btn" :disabled="state.splitOffer.busy" @click="declineSplit">{{ t('ui.router.keepOne') }}</button>
      <button type="button" class="btn primary" :disabled="state.splitOffer.busy" @click="acceptSplit">{{ t('ui.router.split') }}</button>
    </div>
  </div>
</template>

<style scoped>
.split {
  margin: 0 0 8px;
  padding: 10px 12px;
  border: 1px solid var(--indigo);
  border-radius: var(--r-md);
  background: var(--cloth);
  font-size: var(--t-sm);
}
.head {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--indigo);
}
.head strong {
  flex: 1;
  color: var(--ink);
}
.x {
  border: 0;
  background: none;
  color: var(--ink-faint);
  cursor: pointer;
}
.hint {
  margin: 4px 0 6px;
  color: var(--ink-faint);
}
ol {
  margin: 0;
  padding-left: 20px;
}
li {
  margin: 3px 0;
}
li .muted {
  display: block;
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.final {
  margin: 6px 0 0;
  padding-left: 4px;
}
.final .muted {
  display: block;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.iso {
  margin: -2px 0 6px;
  color: var(--indigo);
  font-size: var(--t-xs);
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 8px;
}
.btn {
  padding: 4px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  cursor: pointer;
}
.btn.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
.btn:disabled {
  opacity: 0.6;
  cursor: default;
}
</style>
