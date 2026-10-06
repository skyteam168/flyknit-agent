<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, Archive, Brain, Check, Wrench, X } from '@lucide/vue'
import { state } from '../store'
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
  if (step.status === 'error' || step.status === 'blocked') return AlertTriangle
  if (step.kind === 'model') return Brain
  if (step.kind === 'compact') return Archive
  if (step.kind === 'tool') return Wrench
  return Check
}
</script>

<template>
  <div v-if="trace" class="scrim" @mousedown.self="state.trace = null">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.trace.title')">
      <header>
        <h2>{{ t('ui.trace.title') }}</h2>
        <button type="button" class="ico" :aria-label="t('settings.close')" @click="state.trace = null">
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
        <button type="button" class="btn primary" @click="state.trace = null">{{ t('settings.close') }}</button>
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
li.model .ico-step {
  color: var(--accent);
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
  background: var(--accent);
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
