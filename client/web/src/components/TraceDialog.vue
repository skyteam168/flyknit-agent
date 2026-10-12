<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, Archive, ArrowLeft, Brain, BriefcaseBusiness, Check, ChevronRight, Copy, FileCheck, ShieldAlert, Wrench, X } from '@lucide/vue'
import { backTrace, closeTrace, openSubtaskTrace, state, toast } from '../store'
import type { TraceStep } from '../types'

// 一次任务的执行链路：每一步做了什么、花了多久、用了多少 token。
// 用户说「结果不对」时，顺着这里看是模型判断错了，还是某个工具返回了不对的东西。
const { t } = useI18n()

const trace = computed(() => state.trace)
const items = computed(() => trace.value?.items ?? [])

/** 最慢那一步高亮出来，一眼看到时间花在哪 */
const slowestMs = computed(() => Math.max(1, ...items.value.map((i) => i.durationMs)))

function ms(v: number) {
  return v >= 1000 ? `${(v / 1000).toFixed(1)} s` : `${v} ms`
}

function icon(step: TraceStep) {
  if (step.kind === 'subtask') return BriefcaseBusiness
  if (step.kind === 'guard') return ShieldAlert
  if (step.status === 'error' || step.status === 'blocked') return AlertTriangle
  if (step.kind === 'model') return Brain
  if (step.kind === 'compact') return Archive
  if (step.kind === 'tool') return Wrench
  if (step.kind === 'verify') return step.status === 'warning' ? AlertTriangle : FileCheck
  return Check
}

/** 正在看专家的链路（从主助手的链路点进来的） */
const nested = computed(() => state.traceStack.length > 0)

/** 派遣专家那一步：detail 是子任务 ID，专家的完整链路向宿主要 */
const opening = ref('')
async function openExpert(step: TraceStep) {
  if (!step.detail || opening.value) return
  opening.value = step.detail
  try {
    await openSubtaskTrace(step.detail, true)
  } finally {
    opening.value = ''
  }
}

const stopLabel = computed(() => {
  const r = trace.value?.stopReason
  return r ? t(`ui.trace.stop.${r}`, r) : ''
})

/** 复制成纯文本：员工发给 IT、IT 发给开发排查（只有步骤、耗时、token，没有对话内容） */
async function copy() {
  const tr = trace.value
  if (!tr) return
  const lines = [
    `${t('ui.trace.title')} ${tr.id} · ${stopLabel.value} · ${ms(tr.durationMs)} · ${tr.modelCalls} ${t('ui.trace.modelCalls')} · ${tr.toolCalls} ${t('ui.trace.toolCalls')} · ${(tr.promptTokens + tr.completionTokens).toLocaleString()} tokens`,
    ...tr.items.map((s) =>
      [
        `${s.index}. [${s.kind}${s.status !== 'ok' ? '/' + s.status : ''}] ${s.name}`,
        s.summary,
        s.detail,
        ms(s.durationMs),
        s.promptTokens + s.completionTokens > 0 ? `${s.promptTokens}+${s.completionTokens} tokens` : '',
      ].filter(Boolean).join(' | '),
    ),
  ]
  try {
    await navigator.clipboard.writeText(lines.join('\n'))
    toast(t('ui.trace.copied'))
  } catch {
    /* 剪贴板不可用 */
  }
}
</script>

<template>
  <div v-if="trace" class="scrim" @mousedown.self="closeTrace">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.trace.title')">
      <header>
        <button v-if="nested" type="button" class="ico back" :aria-label="t('ui.trace.back')" :title="t('ui.trace.back')" @click="backTrace">
          <ArrowLeft :size="16" />
        </button>
        <h2>{{ state.traceTitle ? `${state.traceTitle} · ${t('ui.trace.title')}` : t('ui.trace.title') }}</h2>
        <button type="button" class="ico" :aria-label="t('settings.close')" @click="closeTrace">
          <X :size="16" />
        </button>
      </header>

      <div class="stats">
        <span><strong>{{ ms(trace.durationMs) }}</strong>{{ t('ui.trace.total') }}</span>
        <span><strong>{{ trace.steps }}</strong>{{ t('ui.trace.steps') }}</span>
        <span><strong>{{ trace.modelCalls }}</strong>{{ t('ui.trace.modelCalls') }}</span>
        <span><strong>{{ trace.toolCalls }}</strong>{{ t('ui.trace.toolCalls') }}</span>
        <span :class="{ bad: trace.errors > 0 }"><strong>{{ trace.errors }}</strong>{{ t('ui.trace.errors') }}</span>
        <span><strong>{{ (trace.promptTokens + trace.completionTokens).toLocaleString() }}</strong>{{ t('ui.trace.tokens') }}</span>
        <span v-if="stopLabel" class="stop" :class="trace.stopReason">{{ stopLabel }}</span>
      </div>

      <ol class="steps">
        <li v-for="s in items" :key="s.index" :class="[s.kind, s.status]">
          <span class="n">{{ s.index }}</span>
          <component :is="icon(s)" :size="15" class="ico-step" />
          <span class="body">
            <span class="name">
              {{ s.name || t(`ui.trace.kinds.${s.kind}`) }}
              <em v-if="s.status !== 'ok'" class="tag">{{ t(`ui.trace.status.${s.status}`, s.status) }}</em>
            </span>
            <span v-if="s.summary" class="sum">{{ s.summary }}</span>
            <button
              v-if="s.kind === 'subtask' && s.detail"
              type="button"
              class="expand"
              :disabled="opening === s.detail"
              @click="openExpert(s)"
            >
              {{ t('ui.trace.openExpert') }} <ChevronRight :size="13" />
            </button>
            <span v-else-if="s.detail" class="detail">{{ s.detail }}</span>
            <span class="bar"><i :style="{ width: Math.round((s.durationMs / slowestMs) * 100) + '%' }" /></span>
          </span>
          <span class="right">
            <span class="ms">{{ ms(s.durationMs) }}</span>
            <small v-if="s.promptTokens + s.completionTokens > 0">
              {{ (s.promptTokens + s.completionTokens).toLocaleString() }}
            </small>
          </span>
        </li>
      </ol>

      <footer>
        <span class="hint">{{ t('ui.trace.hint') }}</span>
        <button type="button" class="btn" @click="copy"><Copy :size="14" />{{ t('ui.trace.copy') }}</button>
        <button type="button" class="btn primary" @click="closeTrace">{{ t('settings.close') }}</button>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
}
.dialog {
  display: flex;
  flex-direction: column;
  width: min(760px, 100%);
  max-height: calc(100vh - 32px);
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
header {
  display: flex;
  align-items: center;
  padding: 18px 22px 10px;
}
header h2 {
  flex: 1;
  margin: 0;
  font-size: calc(16px * var(--font-scale));
}
.ico {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.ico:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.stats {
  display: flex;
  flex-wrap: wrap;
  gap: 18px;
  padding: 0 22px 14px;
  border-bottom: 1px solid var(--line);
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
}
.stats strong {
  margin-right: 5px;
  color: var(--ink);
  font-size: calc(14px * var(--font-scale));
  font-variant-numeric: tabular-nums;
}
.stats .bad strong {
  color: var(--rust, #c0392b);
}
.steps {
  flex: 1;
  margin: 0;
  padding: 10px 14px;
  overflow-y: auto;
  list-style: none;
}
.steps li {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 9px 10px;
  border-radius: 9px;
}
.steps li:hover {
  background: color-mix(in srgb, var(--ink) 4%, transparent);
}
.steps li.error,
.steps li.blocked {
  background: color-mix(in srgb, var(--amber) 10%, transparent);
}
.n {
  flex: none;
  width: 22px;
  color: var(--ink-soft);
  font-size: calc(11.5px * var(--font-scale));
  font-variant-numeric: tabular-nums;
  text-align: right;
}
.ico-step {
  flex: none;
  margin-top: 1px;
  color: var(--ink-soft);
}
li.model .ico-step,
li.subtask .ico-step {
  color: var(--indigo);
}
/* 派遣专家那一步：点开看专家自己的链路 */
.steps li.subtask {
  background: color-mix(in srgb, var(--indigo) 6%, transparent);
}
.expand {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  margin-top: 4px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--indigo);
  font: inherit;
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.expand:hover,
.expand:focus-visible {
  text-decoration: underline;
  outline: none;
}
.expand:disabled {
  opacity: 0.6;
  cursor: progress;
}
.ico.back {
  margin: 0 6px 0 -6px;
}
li.guard .ico-step,
li.warning .ico-step {
  color: var(--amber);
}
.steps li.guard {
  background: color-mix(in srgb, var(--amber) 8%, transparent);
}
.detail {
  display: block;
  margin-top: 2px;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
  font-variant-numeric: tabular-nums;
}
.stats .stop {
  margin-left: auto;
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--thread-wash);
  color: var(--thread);
  font-weight: 600;
}
.stats .stop:not(.Completed) {
  background: var(--amber-wash);
  color: var(--amber);
}
li.error .ico-step,
li.blocked .ico-step {
  color: var(--rust, #c0392b);
}
.body {
  flex: 1;
  min-width: 0;
}
.name {
  display: block;
  font-size: calc(13px * var(--font-scale));
  font-weight: 600;
}
.tag {
  margin-left: 6px;
  padding: 1px 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--rust, #c0392b) 14%, transparent);
  color: var(--rust, #c0392b);
  font-size: calc(11px * var(--font-scale));
  font-style: normal;
}
.sum {
  display: block;
  margin-top: 2px;
  overflow: hidden;
  color: var(--ink-soft);
  font-size: calc(12px * var(--font-scale));
  text-overflow: ellipsis;
  white-space: nowrap;
}
.bar {
  display: block;
  margin-top: 6px;
  height: 3px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.bar i {
  display: block;
  height: 100%;
  min-width: 2px;
  border-radius: 999px;
  background: var(--indigo);
}
.right {
  flex: none;
  text-align: right;
}
.ms {
  display: block;
  font-size: calc(12.5px * var(--font-scale));
  font-variant-numeric: tabular-nums;
}
.right small {
  color: var(--ink-soft);
  font-size: calc(11px * var(--font-scale));
}
footer .btn {
  display: inline-flex;
  gap: 6px;
  align-items: center;
}
footer {
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 12px 22px 18px;
  border-top: 1px solid var(--line);
}
.hint {
  flex: 1;
  color: var(--ink-soft);
  font-size: calc(12px * var(--font-scale));
}
</style>
