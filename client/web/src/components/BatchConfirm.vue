<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, Check, Hand, X } from '@lucide/vue'
import { answerBatch, findWaitingBatch, mcpToolLabel } from '../store'

/**
 * 合并确认（docs/approval-p0.md 第 7 节）：同一步里 AI 要做的几个操作要确认时，合成一张卡问一次。
 * - 普通项默认勾上；高危、读到可疑内容之后、IT 规定必须确认的项默认不勾，要员工自己勾；
 * - 「允许勾选的」只放行勾上的；没勾的不算拒绝，执行到它时再单独问；
 * - 「全部拒绝」把所有项都拒绝。
 */
const { t, te } = useI18n()

const pending = computed(() => findWaitingBatch())
const items = computed(() => pending.value?.batch.items ?? [])
const checked = ref<string[]>([])
const session = ref(false)

// 换了一张卡：普通项默认勾上，必须自己勾的不勾
watch(
  () => pending.value?.batch.requestId,
  () => {
    checked.value = items.value.filter((i) => !i.mustTick).map((i) => i.callId)
    session.value = false
  },
  { immediate: true },
)

const anyDanger = computed(() => items.value.some((i) => i.mustTick))
const anySessionable = computed(() => items.value.some((i) => i.sessionable && checked.value.includes(i.callId)))

function label(name: string) {
  return mcpToolLabel(name) ?? (te(`tool.names.${name}`) ? t(`tool.names.${name}`) : name)
}

function allowChecked() {
  if (!pending.value) return
  void answerBatch(pending.value.conversationId, [...checked.value], [], session.value)
}

function rejectAll() {
  if (!pending.value) return
  void answerBatch(pending.value.conversationId, [], items.value.map((i) => i.callId), false)
}
</script>

<template>
  <div v-if="pending" class="batch" :class="{ danger: anyDanger }" role="alertdialog" :aria-label="t('tool.batch.title', { n: items.length })">
    <div class="head">
      <AlertTriangle v-if="anyDanger" :size="16" />
      <Hand v-else :size="16" />
      <strong>{{ t('tool.batch.title', { n: items.length }) }}</strong>
    </div>
    <p v-if="pending.batch.rationale" class="why">{{ pending.batch.rationale }}</p>
    <ul>
      <li v-for="i in items" :key="i.callId" :class="{ must: i.mustTick }">
        <label>
          <input v-model="checked" type="checkbox" :value="i.callId" />
          <span class="name">{{ label(i.toolName) }}</span>
          <span v-if="i.mustTick" class="tag">{{ t('tool.batch.mustTick') }}</span>
        </label>
        <code v-if="i.summary" class="what">{{ i.summary }}</code>
        <small v-if="i.reason" class="reason">{{ i.reason }}</small>
      </li>
    </ul>
    <label v-if="anySessionable" class="session">
      <input v-model="session" type="checkbox" />
      {{ t('tool.batch.session') }}
    </label>
    <div class="actions">
      <button type="button" class="btn primary" :disabled="checked.length === 0" @click="allowChecked">
        <Check :size="15" /> {{ t('tool.batch.allowChecked', { n: checked.length }) }}
      </button>
      <button type="button" class="btn" @click="rejectAll"><X :size="15" /> {{ t('tool.batch.rejectAll') }}</button>
    </div>
    <p class="note">{{ t('tool.batch.note') }}</p>
  </div>
</template>

<style scoped>
.batch {
  max-width: var(--column);
  margin: 0 auto 10px;
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--amber) 40%, var(--line));
  border-radius: var(--r-lg);
  background: var(--amber-wash);
}
.batch.danger {
  border-color: color-mix(in srgb, var(--rust, #c0392b) 45%, var(--line));
}
.head {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--amber);
  font-size: var(--t-sm);
}
.head strong {
  color: var(--ink);
}
.why {
  margin: 6px 0 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
ul {
  max-height: 240px;
  margin: 8px 0;
  padding: 0;
  overflow-y: auto;
  list-style: none;
}
li {
  padding: 6px 0;
  border-bottom: 1px solid var(--line);
}
li:last-child {
  border-bottom: 0;
}
li label {
  display: flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
}
.name {
  font-weight: 600;
}
.tag {
  padding: 0 6px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--rust, #c0392b) 14%, var(--cloth));
  color: var(--rust, #c0392b);
  font-size: var(--t-xs);
}
.what {
  display: block;
  max-height: 60px;
  margin: 4px 0 0 24px;
  overflow: auto;
  font-family: var(--font-code);
  font-size: var(--t-xs);
  white-space: pre-wrap;
  word-break: break-all;
}
.reason {
  display: block;
  margin-left: 24px;
  color: var(--ink-faint);
}
.session {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: var(--t-sm);
  cursor: pointer;
}
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 10px;
}
.note {
  margin: 8px 0 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
</style>
