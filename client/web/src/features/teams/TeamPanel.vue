<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, Square, ThumbsDown, ThumbsUp } from '@lucide/vue'
import ExpertAvatar from '../../components/ExpertAvatar.vue'
import { current, currentState, openSubtaskDetail, state } from '../../store'
import { currentTeamRun, rateTeamRun, stopTeamMember } from './store'
import type { TeamMemberView } from '../../types'

/**
 * 团队面板（docs/team-mode.md 7.5）：右侧面板里的「团队」标签。
 * - 成员：状态、正在做哪一步（取自专家子任务的步骤事件）、交付标准、能单独展开收起、能停掉某一位；
 * - 共享看板：成员交回的结论、文件、进度，只追加。
 * 等员工确认的成员排在最上面。
 */
const { t } = useI18n()
const tab = ref<'members' | 'board'>('members')
const open = ref<Record<string, boolean>>({})
/** 任务书很长时默认只显示几行，点「展开全文」看全部 */
const full = ref<Record<string, boolean>>({})

const run = computed(() => currentTeamRun.value)
const members = computed<TeamMemberView[]>(() => {
  const r = run.value
  if (!r) return []
  const list = r.order.map((id) => r.members[id]).filter(Boolean)
  return [...list].sort((a, b) => Number(isWaiting(b)) - Number(isWaiting(a)))
})
const total = computed(() => members.value.length)
const doneCount = computed(() => members.value.filter((m) => m.status === 'done').length)
const runningCount = computed(() => members.value.filter((m) => m.status === 'running' || m.status === 'retrying').length)
const asking = computed(() => members.value.filter(isWaiting).length)
const pct = computed(() => (total.value ? Math.round((members.value.filter((m) => ['done', 'failed', 'stopped'].includes(m.status)).length / total.value) * 100) : 0))

const now = ref(Date.now())
const timer = window.setInterval(() => (now.value = Date.now()), 1000)
onBeforeUnmount(() => window.clearInterval(timer))
const elapsed = computed(() => {
  const start = run.value ? Date.parse(run.value.startedAt) : now.value
  const s = Math.max(0, Math.round((now.value - start) / 1000))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
})

function subtask(m: TeamMemberView) {
  return currentState.value?.subtasks[m.callId] ?? null
}
function isWaiting(m: TeamMemberView) {
  return subtask(m)?.status === 'waiting_confirmation'
}
const tokens = computed(() => {
  const live = members.value.reduce((sum, m) => {
    const r = subtask(m)?.result
    return sum + (r ? r.tokens.prompt + r.tokens.completion : 0)
  }, 0)
  // 重新打开对话时子任务卡片可能还没恢复：用宿主存下来的总数
  return live || run.value?.tokens || 0
})

// ---------- 评价（docs/team-mode.md 第 10 节）----------
const feedback = ref('')
const choosing = ref(0)
function pick(r: number) {
  const id = current.value?.id
  if (!id || !run.value) return
  if (run.value.rating === r && !choosing.value) {
    void rateTeamRun(id, 0) // 再点一次：撤回
    return
  }
  choosing.value = r
  feedback.value = run.value.rating === r ? (run.value.feedback ?? '') : ''
}
function sendRating() {
  const id = current.value?.id
  if (!id || !choosing.value) return
  void rateTeamRun(id, choosing.value, feedback.value.trim())
  choosing.value = 0
}
function expertOf(id: string) {
  return state.experts.find((e) => e.id === id)
}
function nameOf(id: string) {
  return subtask(run.value!.members[id])?.expert?.name ?? expertOf(id)?.name ?? id
}
function statusKey(m: TeamMemberView) {
  return isWaiting(m) ? 'asking' : m.status
}
function line(m: TeamMemberView) {
  const st = subtask(m)
  if (isWaiting(m)) return t('ui.teamRun.panel.waitingConfirm')
  if (m.status === 'retrying') return m.note
  if (m.status === 'running' && st?.step) return `${st.step.name} ${st.step.summary}`.trim()
  if (m.status === 'upstream') return t('ui.teamRun.panel.waitUpstream', { list: m.dependsOn.map(nameOf).join('、') })
  if (m.status === 'waiting') return m.note || t('ui.teamRun.panel.queued')
  if (m.status === 'done') return st?.result?.summary || m.delivery || ''
  return m.note || m.delivery || ''
}
function stop(m: TeamMemberView) {
  const id = current.value?.id
  if (id) void stopTeamMember(id, m.expertId)
}
const kindLabel = (k: string) => t(`ui.teamRun.panel.kind.${['finding', 'file', 'question', 'progress', 'read'].includes(k) ? k : 'finding'}`)
const board = computed(() => [...(run.value?.board ?? [])].reverse())
const time = (iso: string) => {
  const d = new Date(iso)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}
</script>

<template>
  <section v-if="run" class="team-panel">
    <div class="sum">
      <div class="line">
        <span>{{ t('ui.teamRun.panel.done') }} <b>{{ doneCount }}/{{ total }}</b></span>
        <span>{{ t('ui.teamRun.panel.running') }} <b>{{ runningCount }}</b></span>
        <span v-if="asking" class="ask">{{ t('ui.teamRun.panel.asking') }} <b>{{ asking }}</b></span>
        <span>{{ t('ui.teamRun.panel.elapsed') }} <b>{{ run.finished ? '—' : elapsed }}</b></span>
      </div>
      <div class="bar"><i :style="{ width: pct + '%' }" /></div>
      <div v-if="tokens" class="lbl">{{ t('ui.teamRun.panel.tokens', { n: (tokens / 10000).toFixed(1) }) }}</div>
      <p v-if="run.interrupted" class="notice warn">{{ t('ui.teamRun.panel.interrupted') }}</p>
      <p v-else-if="run.autoStarted" class="notice">{{ t('ui.teamRun.panel.autoStarted') }}</p>
      <p v-if="run.notes" class="notice">{{ t('ui.teamRun.panel.notesRecorded', { n: run.notes }) }}</p>
    </div>
    <div class="tabs" role="tablist">
      <button type="button" role="tab" :aria-pressed="tab === 'members'" @click="tab = 'members'">{{ t('ui.teamRun.panel.members') }}</button>
      <button type="button" role="tab" :aria-pressed="tab === 'board'" @click="tab = 'board'">
        {{ t('ui.teamRun.panel.board') }} <span class="num">{{ run.board.length }}</span>
      </button>
    </div>

    <div v-if="tab === 'members'" class="list">
      <div v-for="m in members" :key="m.expertId" class="mem" :class="{ open: open[m.expertId], attn: isWaiting(m) }">
        <button type="button" class="top" :aria-expanded="!!open[m.expertId]" @click="open[m.expertId] = !open[m.expertId]">
          <ExpertAvatar :avatar="expertOf(m.expertId)?.avatar ?? ''" :name="nameOf(m.expertId)" :id="m.expertId" :size="26" />
          <span class="nm">
            <b>{{ nameOf(m.expertId) }}</b>
            <small>{{ line(m) }}</small>
          </span>
          <span class="tags">
            <span v-if="m.reused" class="reused">{{ t('ui.teamRun.panel.reused') }}</span>
            <span class="st" :class="statusKey(m)">{{ t(`ui.teamRun.panel.status.${statusKey(m)}`) }}</span>
          </span>
          <ChevronRight :size="14" class="chev" />
        </button>
        <div v-if="open[m.expertId]" class="detail">
          <p class="meta task" :class="{ clamp: !full[m.expertId] }">{{ t('ui.teamRun.panel.task', { task: m.task }) }}</p>
          <button v-if="m.task.length > 160" type="button" class="more" @click="full[m.expertId] = !full[m.expertId]">
            {{ full[m.expertId] ? t('ui.teamRun.panel.less') : t('ui.teamRun.panel.more') }}
          </button>
          <p class="meta">{{ t('ui.teamRun.panel.delivery', { done: m.done }) }}<template v-if="m.delivery"> · {{ m.delivery }}</template></p>
          <p v-if="m.retries" class="retry">{{ t('ui.teamRun.panel.retried') }}</p>
          <p v-if="subtask(m)?.step && m.status === 'running'" class="cur">● {{ subtask(m)!.step!.name }} {{ subtask(m)!.step!.summary }}</p>
          <ul v-if="subtask(m)?.result?.findings?.length" class="findings">
            <li v-for="f in subtask(m)!.result!.findings" :key="f.id">{{ f.statement }}</li>
          </ul>
          <div class="mact">
            <button v-if="subtask(m)?.result" type="button" class="btn sm" @click="openSubtaskDetail(subtask(m)!.result!)">{{ t('ui.teamRun.panel.viewResult') }}</button>
            <button
              v-if="['running', 'retrying', 'waiting', 'upstream'].includes(m.status) && !run.finished"
              type="button"
              class="btn sm danger"
              @click="stop(m)"
            >
              <Square :size="11" /> {{ t('ui.teamRun.panel.stopMember') }}
            </button>
          </div>
        </div>
      </div>
      <p v-if="!members.length" class="empty">{{ t('ui.teamRun.panel.empty') }}</p>
    </div>

    <div v-else class="list">
      <p class="hint">{{ t('ui.teamRun.panel.boardHint') }}</p>
      <div v-for="(e, i) in board" :key="i" class="bitem">
        <ExpertAvatar :avatar="expertOf(e.expertId)?.avatar ?? ''" :name="e.name" :id="e.expertId" :size="22" />
        <div class="btext">
          <div class="bmeta"><b>{{ e.name }}</b><span class="kind" :class="e.kind">{{ kindLabel(e.kind) }}</span><span>{{ time(e.at) }}</span></div>
          <div>{{ e.text }}</div>
        </div>
      </div>
      <p v-if="!board.length" class="empty">{{ t('ui.teamRun.panel.boardEmpty') }}</p>
    </div>

    <div v-if="run.finished && members.length && !currentState?.busy" class="rate">
      <span class="q">{{ run.rating ? t('ui.teamRun.panel.ratedAs') : t('ui.teamRun.panel.rateQuestion') }}</span>
      <button
        type="button"
        class="thumb"
        :class="{ on: (choosing || run.rating) === 1 }"
        :aria-pressed="run.rating === 1"
        :title="t('ui.teamRun.panel.rateUp')"
        @click="pick(1)"
      >
        <ThumbsUp :size="15" />
      </button>
      <button
        type="button"
        class="thumb"
        :class="{ on: (choosing || run.rating) === -1 }"
        :aria-pressed="run.rating === -1"
        :title="t('ui.teamRun.panel.rateDown')"
        @click="pick(-1)"
      >
        <ThumbsDown :size="15" />
      </button>
      <p v-if="run.feedback && !choosing" class="fb">“{{ run.feedback }}”</p>
      <div v-if="choosing" class="fbrow">
        <input
          id="team-feedback"
          v-model="feedback"
          maxlength="200"
          :placeholder="choosing === 1 ? t('ui.teamRun.panel.feedbackUp') : t('ui.teamRun.panel.feedbackDown')"
          @keydown.enter.prevent="sendRating"
        />
        <button type="button" class="btn sm primary" @click="sendRating">{{ t('ui.teamRun.panel.sendRating') }}</button>
      </div>
    </div>
  </section>
</template>

<style scoped>
.team-panel {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  /* 宽度由右侧面板定：内容再长也只换行，不把面板撑宽 */
  overflow: hidden;
  overflow-wrap: anywhere;
  background: var(--cloth-sunk);
}
.sum {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  border-bottom: 1px solid var(--line);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.line {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
}
.line b {
  color: var(--ink);
  font-variant-numeric: tabular-nums;
}
.line .ask,
.line .ask b {
  color: var(--amber);
}
.bar {
  height: 6px;
  overflow: hidden;
  border-radius: 4px;
  background: var(--chip);
}
.bar i {
  display: block;
  height: 100%;
  border-radius: 4px;
  background: var(--indigo);
  transition: width 0.4s;
}
.lbl {
  color: var(--ink-faint);
}
.tabs {
  display: inline-flex;
  gap: 2px;
  margin: 8px 8px 0;
  padding: 2px;
  border-radius: 8px;
  background: var(--chip);
  align-self: flex-start;
}
.tabs button {
  padding: 3px 12px;
  border: 0;
  border-radius: 6px;
  background: none;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  cursor: pointer;
}
.tabs button[aria-pressed='true'] {
  background: var(--seg);
  color: var(--seg-ink);
  box-shadow: var(--seg-shadow);
}
.num {
  font-variant-numeric: tabular-nums;
}
.list {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
  min-height: 0;
  padding: 8px;
  overflow-x: hidden;
  overflow-y: auto;
}
.mem {
  flex: none;
  min-width: 0;
  border: 1px solid var(--line);
  border-radius: 12px;
  background: var(--cloth);
}
.mem.attn {
  border-color: color-mix(in srgb, var(--amber) 55%, var(--line));
}
.top {
  display: grid;
  grid-template-columns: 26px minmax(0, 1fr) auto 14px;
  gap: 8px;
  align-items: center;
  width: 100%;
  padding: 9px 10px;
  border: 0;
  background: none;
  color: var(--ink);
  text-align: left;
  cursor: pointer;
}
.nm {
  min-width: 0;
}
.nm b,
.nm small {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.nm b {
  font-size: var(--t-sm);
}
.nm small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.chev {
  color: var(--ink-faint);
  transition: transform 0.2s;
}
.mem.open .chev {
  transform: rotate(90deg);
}
.st {
  padding: 1px 7px;
  border-radius: 9px;
  background: var(--chip);
  color: var(--ink-faint);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.st.running,
.st.retrying {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.st.asking {
  background: var(--amber-wash);
  color: var(--amber);
}
.st.done {
  background: var(--thread-wash);
  color: var(--thread);
}
.st.failed,
.st.stopped {
  background: var(--red-wash);
  color: var(--red);
}
.tags {
  display: inline-flex;
  gap: 4px;
  align-items: center;
}
.task.clamp {
  display: -webkit-box;
  overflow: hidden;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 4;
}
.more {
  margin: -2px 0 4px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--indigo);
  font-size: var(--t-xs);
  cursor: pointer;
}
.detail {
  min-width: 0;
  padding: 0 12px 10px 44px;
  font-size: var(--t-xs);
}
.detail p {
  margin: 0 0 4px;
}
.meta {
  color: var(--ink-faint);
}
.retry {
  color: var(--amber);
}
.cur {
  color: var(--indigo);
}
.findings {
  margin: 4px 0;
  padding-left: 16px;
  color: var(--ink-soft);
}
.mact {
  display: flex;
  gap: 6px;
  margin-top: 6px;
}
.btn.sm {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 9px;
  font-size: var(--t-xs);
}
.btn.danger {
  color: var(--red);
}
.hint,
.empty {
  margin: 2px 4px 4px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.bitem {
  display: grid;
  grid-template-columns: 22px minmax(0, 1fr);
  gap: 8px;
  padding: 8px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--cloth);
  font-size: var(--t-xs);
}
.btext {
  min-width: 0;
  color: var(--ink-soft);
  overflow-wrap: anywhere;
}
.bmeta {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
  color: var(--ink-faint);
}
.bmeta b {
  color: var(--ink);
}
.kind {
  padding: 0 6px;
  border-radius: 8px;
  background: var(--chip);
}
.kind.finding {
  background: var(--thread-wash);
  color: var(--thread);
}
.kind.file {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.kind.question,
.kind.progress {
  background: var(--amber-wash);
  color: var(--amber);
}
@media (prefers-reduced-motion: reduce) {
  .bar i,
  .chev {
    transition: none;
  }
}
.notice {
  margin: 6px 0 0;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.notice.warn {
  color: var(--amber);
}
.reused {
  padding: 1px 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--indigo) 12%, transparent);
  color: var(--indigo);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.rate {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  padding: 10px 12px;
  border-top: 1px solid var(--line);
  font-size: var(--t-sm);
}
.rate .q {
  margin-right: auto;
  color: var(--ink-soft);
}
.thumb {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 28px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink-soft);
  cursor: pointer;
}
.thumb.on {
  border-color: var(--indigo);
  color: var(--indigo);
}
.fb {
  flex-basis: 100%;
  margin: 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.fbrow {
  display: flex;
  flex-basis: 100%;
  gap: 6px;
}
.fbrow input {
  flex: 1;
  min-width: 0;
  padding: 5px 9px;
  border: 1px solid var(--line-strong);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
}
</style>
