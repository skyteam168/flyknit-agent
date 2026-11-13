<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  CalendarClock, CircleAlert, CircleCheck, CircleStop, FolderSearch, GitPullRequest, Hand, ListChecks, LoaderCircle, MessageSquare,
  Pause, Play, RotateCcw, ScrollText, ShieldAlert, Smartphone, Square, X, Zap,
} from '@lucide/vue'
import { bridge } from '../bridge'
import { dismissInterrupted, fail, loadRuns, openConversation, resumeInterrupted, state, toast } from '../store'
import type { RunningRun, TaskState, TaskView } from '../types'

/**
 * 任务中心：按任务列（docs/c1-unified-tasks.md 第 6 节）。每件事从哪来、谁让做的、按什么授权、跑了几轮、用了多少 token；
 * 正在跑的合上实时进度（runs.list），可以暂停 / 停止；中断的可以接着做 / 不用了；都能打开所在的对话。
 */
const { t, te, locale } = useI18n()

const tasks = ref<TaskView[]>([])
const loaded = ref(false)
async function loadTasks() {
  try {
    tasks.value = (await bridge.listTasks()) ?? []
  } catch (e) {
    console.warn('读取任务失败', e)
  } finally {
    loaded.value = true
  }
}

// 每秒走一下「用时」；任务开始 / 结束 / 换步时 runs 会刷新，顺带重读任务
const now = ref(Date.now())
let tick: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  void loadRuns()
  void loadTasks()
  tick = setInterval(() => (now.value = Date.now()), 1000)
})
onBeforeUnmount(() => clearInterval(tick))
watch(() => state.runs, () => void loadTasks(), { deep: true })

type Filter = 'all' | 'active' | 'waiting' | 'interrupted' | 'finished'
const filter = ref<Filter>('all')
const origin = ref('')
const groupOf = (s: TaskState): Exclude<Filter, 'all'> =>
  s === 'waiting_confirm' ? 'waiting' : s === 'interrupted' ? 'interrupted' : s === 'pending' || s === 'running' ? 'active' : 'finished'
const counts = computed(() => {
  const c: Record<Filter, number> = { all: tasks.value.length, active: 0, waiting: 0, interrupted: 0, finished: 0 }
  for (const x of tasks.value) c[groupOf(x.state)]++
  return c
})
const filters: Filter[] = ['all', 'active', 'waiting', 'interrupted', 'finished']
const origins = computed(() => [...new Set(tasks.value.map((x) => x.origin))])
const order: Record<Exclude<Filter, 'all'>, number> = { waiting: 0, active: 1, interrupted: 2, finished: 3 }
const shown = computed(() =>
  tasks.value
    .filter((x) => (filter.value === 'all' || groupOf(x.state) === filter.value) && (!origin.value || x.origin === origin.value))
    .sort((a, b) => order[groupOf(a.state)] - order[groupOf(b.state)] || (b.finishedAt ?? b.createdAt).localeCompare(a.finishedAt ?? a.createdAt)),
)

const icons: Record<string, Component> = {
  ui: Hand, mobile: Smartphone, dingtalk: MessageSquare, wecom: MessageSquare, instruction: ScrollText,
  schedule: CalendarClock, folder: FolderSearch, event: Zap, pr: GitPullRequest,
}
const originName = (o: string) => (te(`ui.tasks.from.${o}`) ? t(`ui.tasks.from.${o}`) : o)

/** 正在跑的那一轮的实时进度（同一个对话） */
const liveRun = (x: TaskView): RunningRun | undefined => (x.live ? state.runs.running.find((r) => r.conversationId === x.conversationId) : undefined)

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

function fmt(n: number) {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1000) return `${(n / 1000).toFixed(n >= 100_000 ? 0 : 1)}k`
  return String(n)
}
const tokens = (x: TaskView) => (x.tokenBudget > 0 ? t('ui.tasks.tokensOf', { used: fmt(x.tokensUsed), budget: fmt(x.tokenBudget) }) : t('ui.tasks.tokens', { used: fmt(x.tokensUsed) }))
function grantText(x: TaskView) {
  if (!x.grant) return ''
  const perm = te(`ui.permission.${x.grant.permission}`) ? t(`ui.permission.${x.grant.permission}`) : x.grant.permission
  const dir = x.grant.workspace.split(/[\\/]/).filter(Boolean).pop()
  return dir ? t('ui.tasks.grantIn', { perm, dir }) : t('ui.tasks.grant', { perm })
}
const finished = (x: TaskView) => groupOf(x.state) === 'finished'
const stateIcon = (x: TaskView) =>
  x.state === 'done' ? CircleCheck : x.state === 'stopped' ? CircleStop : x.state === 'failed' || x.state === 'interrupted' ? CircleAlert : x.state === 'waiting_confirm' ? Hand : LoaderCircle

async function act(fn: () => Promise<unknown>) {
  try {
    await fn()
    await loadRuns()
    await loadTasks()
  } catch (e) {
    fail(e)
  }
}

const togglePause = (r: RunningRun) => act(() => (r.paused ? bridge.resume(r.conversationId) : bridge.pause(r.conversationId)))
async function stopTask(x: TaskView) {
  if (!window.confirm(t('ui.tasks.stopConfirm'))) return
  await act(async () => {
    if (await bridge.stopTask(x.id)) toast(t('ui.tasks.stopped'))
  })
}
const resume = (x: TaskView) => act(() => resumeInterrupted(x.conversationId))
/** 看门狗：提醒过还在跑的显示「可能卡住了 / 跑偏了」和「不用管」；停下的显示原因 */
const watchdogWarn = (x: TaskView) => !!x.watchdog && (x.watchdog.stage === 'reminded' || x.watchdog.stage === 'notified') && !finished(x)
const watchdogTitle = (x: TaskView) => t(x.watchdog?.verdict === 'drifting' ? 'ui.tasks.watchdog.drifting' : 'ui.tasks.watchdog.stalled')
const dismissWatchdog = (x: TaskView) => act(() => bridge.dismissWatchdog(x.id))
const dismiss = (x: TaskView) => act(() => dismissInterrupted(x.conversationId))
</script>

<template>
  <div class="tasks">
    <div class="head">
      <h1><ListChecks :size="22" /> {{ t('ui.tasks.title') }}</h1>
      <p class="sub">{{ t('ui.tasks.byTask') }}</p>
      <div v-if="tasks.length" class="filters">
        <button v-for="f in filters" :key="f" type="button" class="chip" :class="{ on: filter === f }" @click="filter = f">
          {{ t(`ui.tasks.filter.${f}`) }} <span class="n">{{ counts[f] }}</span>
        </button>
        <select v-if="origins.length > 1" v-model="origin" class="origin-select" :aria-label="t('ui.tasks.allOrigins')">
          <option value="">{{ t('ui.tasks.allOrigins') }}</option>
          <option v-for="o in origins" :key="o" :value="o">{{ originName(o) }}</option>
        </select>
      </div>
    </div>

    <div class="content">
      <p v-if="loaded && tasks.length === 0" class="empty">{{ t('ui.tasks.empty') }}</p>
      <p v-else-if="loaded && shown.length === 0" class="empty">{{ t('ui.tasks.noMatch') }}</p>
      <p v-if="counts.interrupted && (filter === 'all' || filter === 'interrupted')" class="hint">{{ t('ui.tasks.interruptedHint') }}</p>

      <section class="list-tasks">
        <div v-for="x in shown" :key="x.id" class="card" :class="[`st-${x.state}`, { paused: liveRun(x)?.paused }]">
          <div class="row">
            <span class="from" :title="originName(x.origin)"><component :is="icons[x.origin] ?? Zap" :size="13" /> {{ originName(x.origin) }}</span>
            <span class="pill" :class="`st-${x.state}`">
              <component :is="stateIcon(x)" :size="12" :class="{ spin: x.state === 'running' && !liveRun(x)?.paused }" />
              {{ liveRun(x)?.paused ? t('ui.pause.paused') : t(`ui.tasks.state.${x.state}`) }}
            </span>
            <span class="spacer" />
            <span class="meta">{{ liveRun(x) ? elapsed(liveRun(x)!.startedAt) : when(x.finishedAt ?? x.startedAt ?? x.createdAt) }}</span>
          </div>
          <button type="button" class="title" :title="t('ui.tasks.open')" @click="openConversation(x.conversationId)">
            {{ x.title || x.conversationTitle || t('ui.tasks.untitled') }}
          </button>
          <p class="facts">
            <span v-if="x.requestedBy">{{ t('ui.tasks.requestedBy', { who: x.requestedBy }) }}</span>
            <span v-if="grantText(x)">{{ grantText(x) }}</span>
            <span>{{ t('ui.tasks.runs', { n: x.runs }) }}</span>
            <span :class="{ out: x.tokenBudget > 0 && x.tokensUsed >= x.tokenBudget }">{{ tokens(x) }}</span>
          </p>
          <template v-if="liveRun(x)">
            <p class="step">{{ liveRun(x)!.paused ? t('ui.pause.paused') : liveRun(x)!.step || t('ui.tasks.thinking') }}</p>
            <div v-if="liveRun(x)!.total" class="bar" :title="`${liveRun(x)!.done}/${liveRun(x)!.total}`">
              <span :style="{ width: `${Math.round((liveRun(x)!.done / liveRun(x)!.total) * 100)}%` }" />
            </div>
          </template>
          <div v-if="watchdogWarn(x)" class="watchdog">
            <ShieldAlert :size="14" />
            <span class="what"><strong>{{ watchdogTitle(x) }}</strong>{{ x.watchdog!.reason ? `：${x.watchdog!.reason}` : '' }}</span>
            <button type="button" class="link" @click="dismissWatchdog(x)">{{ t('ui.tasks.watchdog.dismiss') }}</button>
          </div>
          <p v-else-if="x.watchdog?.stage === 'stopped' && x.watchdog.reason" class="step"><ShieldAlert :size="12" /> {{ t('ui.tasks.watchdog.stoppedBecause', { reason: x.watchdog.reason }) }}</p>
          <p v-if="finished(x) && x.stopReason" class="step">{{ x.stopReason }}</p>
          <p v-if="x.conversationTitle && x.conversationTitle !== x.title" class="step muted">{{ t('ui.tasks.conversation', { title: x.conversationTitle }) }}</p>
          <div class="row actions">
            <span v-if="liveRun(x)?.total" class="meta">{{ t('ui.tasks.planProgress', { done: liveRun(x)!.done, total: liveRun(x)!.total }) }}</span>
            <span class="spacer" />
            <button v-if="liveRun(x)" type="button" class="btn" @click="togglePause(liveRun(x)!)">
              <component :is="liveRun(x)!.paused ? Play : Pause" :size="14" /> {{ t(liveRun(x)!.paused ? 'ui.pause.resume' : 'ui.tasks.pause') }}
            </button>
            <template v-if="x.state === 'interrupted'">
              <button type="button" class="btn" @click="dismiss(x)"><X :size="14" /> {{ t('ui.tasks.dismiss') }}</button>
              <button type="button" class="btn" @click="resume(x)"><RotateCcw :size="14" /> {{ t('ui.tasks.resume') }}</button>
            </template>
            <button v-else-if="!finished(x)" type="button" class="btn" @click="stopTask(x)"><Square :size="13" /> {{ t('ui.tasks.stop') }}</button>
            <button type="button" class="btn primary" @click="openConversation(x.conversationId)">{{ t('ui.tasks.open') }}</button>
          </div>
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
.filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 12px;
}
.chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 10px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth);
  color: var(--ink-soft);
  font-size: var(--t-sm);
  cursor: pointer;
}
.chip.on {
  border-color: var(--indigo);
  color: var(--indigo);
}
.chip .n {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.origin-select {
  padding: 3px 8px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth);
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.list-tasks {
  margin-top: 10px;
}
.from {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.pill.st-running {
  color: var(--indigo);
}
.pill.st-waiting_confirm,
.pill.st-interrupted {
  background: color-mix(in srgb, var(--amber, #d97706) 14%, transparent);
  color: var(--amber, #d97706);
}
.pill.st-done {
  color: var(--green, #16a34a);
}
.pill.st-failed {
  color: var(--danger, #dc2626);
}
.card .title {
  display: block;
  margin-top: 6px;
}
.facts {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  margin: 4px 0 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.facts .out {
  color: var(--danger, #dc2626);
}
.muted {
  color: var(--ink-faint);
}
.card.st-done,
.card.st-stopped,
.card.st-failed {
  background: var(--cloth-sunk, var(--cloth));
}
.watchdog {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 8px;
  padding: 6px 10px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--amber, #d97706) 12%, transparent);
  color: var(--amber, #d97706);
  font-size: var(--t-sm);
}
.watchdog .what {
  flex: 1;
  min-width: 0;
  color: var(--ink);
}
.watchdog .link {
  flex: none;
  border: 0;
  background: none;
  color: var(--indigo);
  cursor: pointer;
  font-size: var(--t-sm);
}
</style>
