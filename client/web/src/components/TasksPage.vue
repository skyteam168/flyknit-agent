<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { CircleAlert, CircleCheck, CircleStop, History, ListChecks, LoaderCircle, Pause, Play, RotateCcw, Smartphone, Square, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { dismissInterrupted, fail, loadRuns, openConversation, resumeInterrupted, state } from '../store'
import type { RunningRun } from '../types'

const { t, locale } = useI18n()

// 每秒走一下「用时」
const now = ref(Date.now())
let tick: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  void loadRuns()
  tick = setInterval(() => (now.value = Date.now()), 1000)
})
onBeforeUnmount(() => clearInterval(tick))

const runs = computed(() => state.runs)
const empty = computed(() => runs.value.running.length + runs.value.interrupted.length + runs.value.recent.length === 0)

function elapsed(startedAt: string) {
  const s = Math.max(0, Math.floor((now.value - new Date(startedAt).getTime()) / 1000))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = String(s % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

function when(at: string) {
  const date = new Date(at)
  return Number.isNaN(date.getTime()) ? at : date.toLocaleString(locale.value, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

/** 一轮任务的结局 → 文案和样式 */
function outcome(reason: string) {
  const known = ['Completed', 'Cancelled', 'MaxSteps', 'Budget', 'Stuck', 'TooManyFailures', 'Failed']
  const key = known.includes(reason) ? reason : 'Failed'
  return { text: t(`ui.tasks.outcome.${key}`), ok: key === 'Completed', stopped: key === 'Cancelled' }
}

async function act(fn: () => Promise<unknown>) {
  try {
    await fn()
    await loadRuns()
  } catch (e) {
    fail(e)
  }
}

const togglePause = (r: RunningRun) => act(() => (r.paused ? bridge.resume(r.conversationId) : bridge.pause(r.conversationId)))
const stopRun = (r: RunningRun) => act(() => bridge.stop(r.conversationId))
</script>

<template>
  <div class="tasks">
    <div class="head">
      <h1><ListChecks :size="22" /> {{ t('ui.tasks.title') }}</h1>
      <p class="sub">{{ t('ui.tasks.subtitle') }}</p>
    </div>

    <div class="content">
      <p v-if="empty" class="empty">{{ t('ui.tasks.empty') }}</p>

      <!-- 正在进行 -->
      <section v-if="runs.running.length">
        <h2><LoaderCircle :size="15" class="spin" /> {{ t('ui.tasks.running') }} <span class="count">{{ runs.running.length }}</span></h2>
        <div v-for="r in runs.running" :key="r.conversationId" class="card" :class="{ paused: r.paused }">
          <div class="row">
            <button type="button" class="title" :title="t('ui.tasks.open')" @click="openConversation(r.conversationId)">
              <Smartphone v-if="r.remote" :size="14" class="origin" />
              {{ r.title }}
            </button>
            <span v-if="r.paused" class="pill warn"><Pause :size="12" /> {{ t('ui.pause.paused') }}</span>
            <span class="meta">{{ elapsed(r.startedAt) }}</span>
          </div>
          <p class="step">{{ r.step || t('ui.tasks.thinking') }}</p>
          <div v-if="r.total" class="bar" :title="`${r.done}/${r.total}`">
            <span :style="{ width: `${Math.round((r.done / r.total) * 100)}%` }" />
          </div>
          <div class="row actions">
            <span v-if="r.total" class="meta">{{ t('ui.tasks.planProgress', { done: r.done, total: r.total }) }}</span>
            <span class="spacer" />
            <button type="button" class="btn" @click="togglePause(r)">
              <component :is="r.paused ? Play : Pause" :size="14" /> {{ t(r.paused ? 'ui.pause.resume' : 'ui.tasks.pause') }}
            </button>
            <button type="button" class="btn" @click="stopRun(r)"><Square :size="13" /> {{ t('ui.tasks.stop') }}</button>
            <button type="button" class="btn primary" @click="openConversation(r.conversationId)">{{ t('ui.tasks.open') }}</button>
          </div>
        </div>
      </section>

      <!-- 上次没做完 -->
      <section v-if="runs.interrupted.length">
        <h2><CircleAlert :size="15" /> {{ t('ui.tasks.interrupted') }} <span class="count">{{ runs.interrupted.length }}</span></h2>
        <p class="hint">{{ t('ui.tasks.interruptedHint') }}</p>
        <div v-for="r in runs.interrupted" :key="r.conversationId" class="card">
          <div class="row">
            <button type="button" class="title" @click="openConversation(r.conversationId)">{{ r.title || t('ui.tasks.untitled') }}</button>
            <span class="meta">{{ t('ui.tasks.startedAt', { at: when(r.startedAt) }) }}</span>
          </div>
          <p v-if="r.origin !== 'ui'" class="step">{{ t(`ui.tasks.origin.${r.origin === 'remote' ? 'remote' : 'headless'}`) }}</p>
          <p v-if="r.stage" class="step">{{ t('ui.tasks.stoppedAt', { stage: r.stage }) }}</p>
          <div class="row actions">
            <span class="spacer" />
            <button type="button" class="btn" @click="dismissInterrupted(r.conversationId)"><X :size="14" /> {{ t('ui.tasks.dismiss') }}</button>
            <button type="button" class="btn primary" @click="resumeInterrupted(r.conversationId)"><RotateCcw :size="14" /> {{ t('ui.tasks.resume') }}</button>
          </div>
        </div>
      </section>

      <!-- 最近完成 -->
      <section v-if="runs.recent.length">
        <h2><History :size="15" /> {{ t('ui.tasks.recent') }}</h2>
        <div class="list">
          <button v-for="(r, i) in runs.recent" :key="r.conversationId + i" type="button" class="item" @click="openConversation(r.conversationId)">
            <component
              :is="outcome(r.stopReason).ok ? CircleCheck : outcome(r.stopReason).stopped ? CircleStop : CircleAlert"
              :size="15"
              class="state"
              :class="{ ok: outcome(r.stopReason).ok, bad: !outcome(r.stopReason).ok && !outcome(r.stopReason).stopped }"
            />
            <span class="name">{{ r.title || t('ui.tasks.untitled') }}</span>
            <span class="meta">{{ outcome(r.stopReason).text }} · {{ t('ui.tasks.steps', { n: r.steps }) }}</span>
            <span class="meta when">{{ when(r.finishedAt) }}</span>
          </button>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.tasks {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  background: var(--loom);
}
.head {
  padding: 16px 28px 4px;
}
h1 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: 22px;
  font-weight: 600;
}
.sub {
  margin: 6px 0 0;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 8px 28px 32px;
}
.empty {
  margin: 60px 0;
  color: var(--ink-faint);
  text-align: center;
}
section {
  max-width: 820px;
  margin-top: 18px;
}
h2 {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0 0 8px;
  font-size: var(--t-md);
  font-weight: 600;
  color: var(--ink-soft);
}
.count {
  padding: 0 7px;
  border-radius: 999px;
  background: var(--chip);
  font-size: var(--t-xs);
  font-weight: 500;
}
.hint {
  margin: -2px 0 8px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.card {
  margin-bottom: 8px;
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
}
.card.paused {
  border-color: var(--amber);
}
.row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  overflow: hidden;
  font-weight: 600;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--ink);
}
.title:hover {
  color: var(--indigo);
}
.origin {
  flex: none;
  color: var(--ink-faint);
}
.meta {
  flex: none;
  margin-left: auto;
  color: var(--ink-faint);
  font-size: var(--t-sm);
  font-variant-numeric: tabular-nums;
}
.actions .meta {
  margin-left: 0;
}
.spacer {
  flex: 1;
}
.step {
  margin: 4px 0 0;
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  white-space: nowrap;
  text-overflow: ellipsis;
}
.bar {
  height: 4px;
  margin-top: 10px;
  border-radius: 2px;
  background: var(--chip);
  overflow: hidden;
}
.bar span {
  display: block;
  height: 100%;
  border-radius: 2px;
  background: var(--indigo);
  transition: width 300ms;
}
.paused .bar span {
  background: var(--ink-faint);
}
.actions {
  margin-top: 10px;
}
.pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 8px;
  border-radius: 999px;
  font-size: var(--t-xs);
}
.pill.warn {
  background: var(--amber-wash);
  color: var(--amber);
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-sm);
}
.btn:hover {
  background: var(--cloth-sunk);
}
.btn.primary {
  border-color: var(--action);
  background: var(--action);
  color: var(--action-ink);
}
.btn.primary:hover {
  background: var(--action-hover);
}
.list {
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  overflow: hidden;
}
.item {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 9px 14px;
  border-bottom: 1px solid var(--line);
  text-align: left;
}
.item:last-child {
  border-bottom: none;
}
.item:hover {
  background: var(--cloth-sunk);
}
.state {
  flex: none;
  color: var(--ink-faint);
}
.state.ok {
  color: var(--thread);
}
.state.bad {
  color: var(--amber);
}
.name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.when {
  width: 120px;
  margin-left: 0;
  text-align: right;
}
.spin {
  animation: spin 1.2s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
@media (max-width: 640px) {
  .head,
  .content {
    padding-left: 16px;
    padding-right: 16px;
  }
  .when {
    display: none;
  }
}
</style>
