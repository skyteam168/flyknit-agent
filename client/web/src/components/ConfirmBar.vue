<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Hand, X } from '@lucide/vue'
import { answerConfirm, current, currentState } from '../store'

// 停靠在输入框上方的确认条：等待确认的操作始终可见，不会被输入框挡住
const { t, te } = useI18n()

const waiting = computed(() => Object.values(currentState.value?.tools ?? {}).filter((x) => x.state === 'waiting' && x.confirm))
const tool = computed(() => waiting.value[0] ?? null)
const label = computed(() => (tool.value && te(`tool.names.${tool.value.name}`) ? t(`tool.names.${tool.value.name}`) : tool.value?.name ?? ''))

function answer(choice: 'allowOnce' | 'allowAlways' | 'reject') {
  if (tool.value && current.value) void answerConfirm(current.value.id, tool.value.callId, choice)
}
</script>

<template>
  <div v-if="tool && tool.confirm" class="confirm-bar" role="alertdialog" :aria-label="t('tool.confirmTitle')">
    <div class="head">
      <Hand :size="16" />
      <strong>{{ t('tool.confirmTitle') }}</strong>
      <span class="what-name">{{ label }}</span>
      <span v-if="waiting.length > 1" class="more">+{{ waiting.length - 1 }}</span>
    </div>
    <code class="what">{{ tool.summary }}</code>
    <p v-if="tool.confirm.rationale" class="why">{{ tool.confirm.rationale }}</p>
    <p v-if="tool.confirm.reason" class="reason">{{ tool.confirm.reason }}</p>
    <div class="actions">
      <button type="button" class="btn primary" @click="answer(tool.confirm.rememberable ? 'allowAlways' : 'allowOnce')">
        <Check :size="15" /> {{ tool.confirm.rememberable ? t('tool.allowAlways') : t('tool.allowOnce') }}
      </button>
      <button v-if="tool.confirm.rememberable" type="button" class="btn" @click="answer('allowOnce')">{{ t('tool.allowOnlyOnce') }}</button>
      <button type="button" class="btn" @click="answer('reject')"><X :size="15" /> {{ t('tool.reject') }}</button>
      <span v-if="tool.confirm.rememberable" class="note">{{ t('tool.rememberNote') }}</span>
    </div>
  </div>
</template>

<style scoped>
.confirm-bar {
  max-width: var(--column);
  margin: 0 auto 10px;
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--amber) 40%, var(--line));
  border-radius: var(--r-lg);
  background: var(--amber-wash);
  box-shadow: var(--shadow-card);
  animation: rise 160ms ease-out;
}
.head {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--amber);
  font-size: var(--t-sm);
}
.what-name {
  color: var(--ink-soft);
}
.more {
  margin-left: auto;
  padding: 0 7px;
  border-radius: 9px;
  background: var(--cloth);
  font-size: var(--t-xs);
}
.what {
  display: block;
  max-height: 120px;
  overflow: auto;
  margin: 8px 0;
  padding: 8px 10px;
  border-radius: var(--r-sm);
  background: var(--cloth);
  font-family: var(--font-code);
  font-size: var(--t-xs);
  white-space: pre-wrap;
  word-break: break-all;
}
.why,
.reason {
  margin: 0 0 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.reason {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
.note {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
@keyframes rise {
  from {
    opacity: 0;
    transform: translateY(6px);
  }
}
</style>
