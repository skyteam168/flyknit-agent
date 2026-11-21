<script setup lang="ts">
/**
 * 编码模式输入框上方：这个任务的上下文用了多少，以及「该新开任务了」的提示。
 * - 用量：上一次请求的上下文 / 模型窗口（宿主每轮结束时记下，context.usage）；
 * - 对话太长（≥ 60%）或者换了项目：建议新开任务，可以「带上摘要新建」（旧任务整理成交接摘要，第一句话发出时交给 AI）；
 * - 带了摘要的新任务：显示「正在整理 / 已带上」，可以查看、不带了。
 */
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Gauge, Sparkles, X } from '@lucide/vue'
import { compactConversation, current, currentState, loadContextUsage, state, toast } from '../../store'
import { dropHandoff, handoffs, hintDismissed, ideWelcome, LongContextRatio, newTaskInIde, projectSwitch } from './store'

const { t } = useI18n()

watch(
  () => current.value?.id,
  (id) => {
    if (id && state.contextUsage[id] === undefined) void loadContextUsage(id)
  },
  { immediate: true },
)

const usage = computed(() => (current.value ? state.contextUsage[current.value.id] : null))
const ratio = computed(() => (usage.value && usage.value.contextLength > 0 ? usage.value.used / usage.value.contextLength : 0))
const percent = computed(() => Math.min(100, Math.round(ratio.value * 100)))
const k = (n: number) => (n >= 1000 ? Math.round(n / 1000) + 'k' : String(n))
const busy = computed(() => !!currentState.value?.busy)

/** 还没说话时交接摘要显示在欢迎页里（IdeWelcome），这里不重复 */
const handoff = computed(() => (current.value && !ideWelcome.value ? handoffs[current.value.id] : undefined))
const showSummary = ref(false)
watch(() => current.value?.id, () => (showSummary.value = false))

const dismissed = computed(() => (current.value ? hintDismissed[current.value.id] : undefined))
const switched = computed(() => (current.value ? projectSwitch(current.value.id) : null))
/** 要提示哪一种：换项目优先（更明确），其次对话太长 */
const suggest = computed<'project' | 'long' | null>(() => {
  if (!current.value || busy.value || handoffs[current.value.id]) return null
  if (switched.value && dismissed.value?.project !== switched.value[0]) return 'project'
  if (ratio.value >= LongContextRatio && !dismissed.value?.long) return 'long'
  return null
})

function dismiss() {
  if (!current.value) return
  // 整个换掉（不在 ??= 返回的原对象上改：那不是响应式的，界面不会变）
  const d = hintDismissed[current.value.id] ?? {}
  hintDismissed[current.value.id] = suggest.value === 'project' && switched.value ? { ...d, project: switched.value[0] } : { ...d, long: true }
}

function newTask(withSummary: boolean) {
  const from = current.value?.id
  if (!from) return
  void newTaskInIde(current.value?.workspace, withSummary ? { handoffFrom: from } : {})
}

const compacting = ref(false)
async function compact() {
  if (!current.value || compacting.value) return
  compacting.value = true
  try {
    toast(await compactConversation(current.value.id))
  } finally {
    compacting.value = false
  }
}
</script>

<template>
  <div v-if="current && (handoff || suggest || usage)" class="ctx-hint">
    <div v-if="handoff" class="card handoff" :class="handoff.status">
      <div class="row">
        <Sparkles :size="13" class="icon" />
        <span class="text">
          {{ t(handoff.status === 'working' ? 'ui.code.ctx.handoffWorking' : 'ui.code.ctx.handoffReady', { title: handoff.fromTitle || t('ui.code.agent.untitled') }) }}
        </span>
        <button v-if="handoff.status === 'ready'" type="button" class="act" @click="showSummary = !showSummary">
          {{ showSummary ? t('ui.code.ctx.hide') : t('ui.code.ctx.view') }}
        </button>
        <button type="button" class="act" @click="dropHandoff(current.id)">{{ t('ui.code.ctx.drop') }}</button>
      </div>
      <pre v-if="showSummary && handoff.status === 'ready'" class="summary">{{ handoff.summary }}</pre>
    </div>
    <div v-else-if="suggest" class="card suggest">
      <div class="row">
        <Gauge :size="13" class="icon warn" />
        <span class="text">
          {{ suggest === 'project' && switched ? t('ui.code.ctx.project', { now: switched[0], before: switched[1] }) : t('ui.code.ctx.long', { p: percent }) }}
        </span>
        <button type="button" class="x" :title="t('ui.code.ctx.dismiss')" :aria-label="t('ui.code.ctx.dismiss')" @click="dismiss"><X :size="12" /></button>
      </div>
      <div class="actions">
        <button type="button" class="act primary" :title="t('ui.code.ctx.newWithSummaryHint')" @click="newTask(true)">{{ t('ui.code.ctx.newWithSummary') }}</button>
        <button type="button" class="act" @click="newTask(false)">{{ t('ui.code.ctx.newTask') }}</button>
        <button v-if="suggest === 'long'" type="button" class="act" :disabled="compacting" :title="t('ui.code.ctx.compactHint')" @click="compact">{{ t('ui.code.ctx.compact') }}</button>
      </div>
    </div>
    <div
      v-if="usage"
      class="meter"
      :class="{ warn: ratio >= LongContextRatio }"
      :title="t('ui.code.ctx.meterHint', { used: k(usage.used), total: k(usage.contextLength), compact: k(usage.compactAt) })"
    >
      <span class="bar"><span class="fill" :style="{ width: percent + '%' }" /></span>
      {{ t('ui.code.ctx.meter', { p: percent }) }}
    </div>
  </div>
</template>

<style scoped>
.ctx-hint {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-width: var(--column);
  margin: 0 auto 6px;
}
.card {
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--loom);
  font-size: calc(12.5px * var(--font-scale));
}
.card.suggest {
  border-color: color-mix(in srgb, var(--amber, #d4a017) 55%, var(--line));
}
.row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px 6px 12px;
}
.text {
  flex: 1;
  min-width: 0;
  color: var(--ink-soft);
}
.icon {
  flex: none;
  color: var(--indigo);
}
.icon.warn {
  color: var(--amber, #d4a017);
}
.handoff.working .icon {
  animation: pulse 1.2s ease-in-out infinite;
}
@keyframes pulse {
  50% {
    opacity: 0.35;
  }
}
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 0 12px 8px 31px;
}
.act {
  flex: none;
  padding: 2px 10px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.act:hover {
  border-color: var(--indigo);
}
.act:disabled {
  opacity: 0.55;
  cursor: default;
}
.act.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
.x {
  display: grid;
  flex: none;
  place-items: center;
  width: 20px;
  height: 20px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--ink-faint);
  cursor: pointer;
}
.x:hover {
  color: var(--ink);
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.summary {
  max-height: 200px;
  margin: 0;
  padding: 8px 12px;
  overflow: auto;
  border-top: 1px solid var(--line);
  color: var(--ink-soft);
  font-family: inherit;
  font-size: calc(12px * var(--font-scale));
  white-space: pre-wrap;
}
.meter {
  display: flex;
  align-items: center;
  align-self: flex-end;
  gap: 6px;
  padding: 0 4px;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  cursor: default;
}
.bar {
  position: relative;
  width: 48px;
  height: 4px;
  overflow: hidden;
  border-radius: 2px;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
}
.fill {
  position: absolute;
  inset: 0 auto 0 0;
  border-radius: 2px;
  background: var(--indigo);
}
.meter.warn {
  color: var(--amber, #d4a017);
}
.meter.warn .fill {
  background: var(--amber, #d4a017);
}
</style>
