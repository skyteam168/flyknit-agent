<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { CalendarClock, History, LoaderCircle, Pause, Play, RotateCcw, Smartphone, Square, SquareTerminal, Users, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { dismissInterrupted, fail, loadRuns, loadSchedules, openConversation, resumeInterrupted, state, stopTerminalRun } from '../store'
import { activeSubtasks, backgroundCommands, backgroundCount, conversationTitle, upcomingSchedules } from '../background'
import type { RunningRun, TerminalRun } from '../types'

/**
 * 右侧「后台」标签：所有对话里正在进行的事放在一起看——
 * 正在跑的任务（可暂停 / 停止）、后台命令（可停）、专家子任务、上次没做完的（接着做 / 不用了）、24 小时内要跑的定时任务。
 * 点标题跳到对应的对话。任务中心页是同一份数据的完整版（含最近做完的）。
 */
const { t, locale } = useI18n()

const now = ref(Date.now())
let tick: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  void loadRuns()
  if (!state.schedules.length) void loadSchedules()
  tick = setInterval(() => (now.value = Date.now()), 1000)
})
onBeforeUnmount(() => clearInterval(tick))

const runs = computed(() => state.runs)
const empty = computed(() => backgroundCount.value === 0 && upcomingSchedules.value.length === 0)

function elapsed(startedAt: string | null | undefined) {
  if (!startedAt) return ''
  const s = Math.max(0, Math.floor((now.value - new Date(startedAt).getTime()) / 1000))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = String(s % 60).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

function at(time: string) {
  const d = new Date(time)
  return Number.isNaN(d.getTime()) ? time : d.toLocaleString(locale.value, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
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

/** 跳到这个后台命令所在的对话，并切到终端看输出 */
async function openCommand(r: TerminalRun) {
  await openConversation(r.conversationId)
  state.rightTab = 'terminal'
}
</script>

<template>
  <aside class="bg" :aria-label="t('ui.background.title')">
    <header class="head">
      <LoaderCircle :size="15" />
      <strong>{{ t('ui.background.title') }}</strong>
      <span v-if="backgroundCount" class="count">{{ backgroundCount }}</span>
    </header>

    <div class="scroll">
      <p v-if="empty" class="empty">{{ t('ui.background.empty') }}</p>

      <!-- 正在进行的任务 -->
      <section v-if="runs.running.length">
        <h3>{{ t('ui.background.running') }}</h3>
        <div v-for="r in runs.running" :key="r.conversationId" class="item" :class="{ paused: r.paused }">
          <div class="row">
            <button type="button" class="title" :title="t('ui.background.open')" @click="openConversation(r.conversationId)">
              <Smartphone v-if="r.remote" :size="13" class="origin" />{{ r.title }}
            </button>
            <span class="meta">{{ elapsed(r.startedAt) }}</span>
          </div>
          <p class="sub">{{ r.paused ? t('ui.pause.paused') : r.step || t('ui.tasks.thinking') }}</p>
          <div v-if="r.total" class="bar"><span :style="{ width: `${Math.round((r.done / r.total) * 100)}%` }" /></div>
          <div class="actions">
            <button type="button" class="act" @click="togglePause(r)">
              <Play v-if="r.paused" :size="12" /><Pause v-else :size="12" />
              {{ r.paused ? t('ui.pause.resume') : t('ui.tasks.pause') }}
            </button>
            <button type="button" class="act danger" @click="stopRun(r)"><Square :size="11" fill="currentColor" /> {{ t('ui.tasks.stop') }}</button>
          </div>
        </div>
      </section>

      <!-- 后台命令 -->
      <section v-if="backgroundCommands.length">
        <h3>{{ t('ui.background.commands') }}</h3>
        <div v-for="r in backgroundCommands" :key="r.conversationId + r.id" class="item">
          <div class="row">
            <SquareTerminal :size="13" class="origin" />
            <button type="button" class="title mono" :title="t('ui.background.openTerminal')" @click="openCommand(r)">{{ r.command }}</button>
            <span class="meta">{{ elapsed(r.startedAt) }}</span>
          </div>
          <p class="sub">{{ conversationTitle(r.conversationId) }}</p>
          <div class="actions">
            <button type="button" class="act danger" @click="stopTerminalRun(r.conversationId, r.id)"><Square :size="11" fill="currentColor" /> {{ t('ui.tasks.stop') }}</button>
          </div>
        </div>
      </section>

      <!-- 专家子任务 -->
      <section v-if="activeSubtasks.length">
        <h3>{{ t('ui.background.experts') }}</h3>
        <div v-for="x in activeSubtasks" :key="x.sub.callId" class="item">
          <div class="row">
            <Users :size="13" class="origin" />
            <button type="button" class="title" :title="t('ui.background.open')" @click="openConversation(x.conversationId)">
              {{ x.sub.expert?.name || t('ui.background.expert') }}
            </button>
            <span class="meta">{{ elapsed(x.sub.startedAt) }}</span>
          </div>
          <p class="sub">{{ x.sub.status === 'waiting_confirmation' ? t('ui.background.waitingConfirm') : x.sub.step?.summary || x.sub.task }}</p>
        </div>
      </section>

      <!-- 上次没做完 -->
      <section v-if="runs.interrupted.length">
        <h3>{{ t('ui.background.interrupted') }}</h3>
        <div v-for="r in runs.interrupted" :key="r.conversationId" class="item">
          <div class="row">
            <History :size="13" class="origin" />
            <button type="button" class="title" :title="t('ui.background.open')" @click="openConversation(r.conversationId)">{{ r.title }}</button>
            <span class="meta">{{ at(r.startedAt) }}</span>
          </div>
          <div class="actions">
            <button type="button" class="act" @click="resumeInterrupted(r.conversationId)"><RotateCcw :size="12" /> {{ t('ui.tasks.resume') }}</button>
            <button type="button" class="act ghost" @click="dismissInterrupted(r.conversationId)"><X :size="12" /> {{ t('ui.tasks.dismiss') }}</button>
          </div>
        </div>
      </section>

      <!-- 快到点的定时任务 -->
      <section v-if="upcomingSchedules.length">
        <h3>{{ t('ui.background.schedules') }}</h3>
        <button v-for="s in upcomingSchedules" :key="s.id" type="button" class="item link" :title="t('ui.background.openSchedules')" @click="state.schedulesOpen = true">
          <div class="row">
            <CalendarClock :size="13" class="origin" />
            <span class="title plain">{{ s.name }}</span>
            <span class="meta">{{ at(s.nextRunAt!) }}</span>
          </div>
        </button>
      </section>
    </div>
  </aside>
</template>

<style scoped>
.bg {
  display: flex;
  flex-direction: column;
  width: 340px;
  min-height: 0;
  border-left: 1px solid var(--line);
  background: var(--loom);
  font-size: var(--t-sm);
}
.head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 10px 12px;
  border-bottom: 1px solid var(--line);
  color: var(--ink-soft);
}
.head strong {
  color: var(--ink);
}
.count {
  padding: 0 7px;
  border-radius: 999px;
  background: var(--indigo);
  color: #fff;
  font-size: var(--t-xs);
  line-height: 18px;
}
.scroll {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 6px 10px 14px;
}
.empty {
  padding: 28px 12px;
  color: var(--ink-faint);
  text-align: center;
}
h3 {
  margin: 12px 2px 6px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-weight: 600;
}
.item {
  display: block;
  width: 100%;
  margin-bottom: 6px;
  padding: 8px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  text-align: left;
}
.item.paused {
  opacity: 0.75;
}
.item.link:hover {
  border-color: var(--indigo);
}
.row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.origin {
  flex: none;
  color: var(--ink-faint);
}
.title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink);
  font-weight: 600;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.title:not(.plain):hover {
  color: var(--indigo);
}
.title.mono {
  font-family: Consolas, 'Cascadia Mono', monospace;
  font-weight: 500;
}
.meta {
  flex: none;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-variant-numeric: tabular-nums;
}
.sub {
  margin: 4px 0 0;
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.bar {
  height: 4px;
  margin-top: 6px;
  overflow: hidden;
  border-radius: 2px;
  background: var(--cloth-sunk);
}
.bar span {
  display: block;
  height: 100%;
  background: var(--indigo);
}
.actions {
  display: flex;
  gap: 6px;
  margin-top: 8px;
}
.act {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 9px;
  border: 1px solid var(--line);
  border-radius: 999px;
  color: var(--ink);
  font-size: var(--t-xs);
}
.act:hover {
  background: var(--cloth-sunk);
}
.act.danger {
  color: var(--danger, #dc2626);
}
.act.ghost {
  color: var(--ink-soft);
}
</style>
