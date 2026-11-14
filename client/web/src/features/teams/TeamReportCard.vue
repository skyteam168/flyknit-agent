<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, FileText, ThumbsDown, ThumbsUp } from '@lucide/vue'
import ExpertAvatar from '../../components/ExpertAvatar.vue'
import { currentState, launchFile, state } from '../../store'
import { rateTeamRun, teams } from './store'

/**
 * 团队报告卡（team_report 这次调用，和演示一致）：
 * 总体结论 → 各成员要点（从这次任务的看板里取交卷结论和核验标记，点开看）→ 产出文件 → 交叉验证 → 需要你确认 → 改进建议 → 评价。
 * 报告内容在工具调用的参数里，成员结论在本地存的面板数据里，都不另外请求。
 */
const props = defineProps<{ name: string; callId: string; args: string; conversationId: string; output: string }>()
const { t } = useI18n()

interface Check {
  kind: string
  text: string
}
const report = computed(() => {
  try {
    const a = JSON.parse(props.args || '{}') as { title?: string; conclusion?: string; checks?: Check[]; confirm?: string[]; suggestions?: string[] }
    return {
      title: a.title ?? '',
      conclusion: a.conclusion ?? '',
      checks: (a.checks ?? []).filter((c) => c && c.text),
      confirm: (a.confirm ?? []).filter(Boolean),
      suggestions: (a.suggestions ?? []).filter(Boolean),
    }
  } catch {
    return { title: '', conclusion: '', checks: [] as Check[], confirm: [] as string[], suggestions: [] as string[] }
  }
})

/** 这份报告属于哪次团队任务：工具结果里写着「任务 xxx」 */
const runId = computed(() => /任务 ([0-9a-zA-Z_-]+)）/.exec(props.output)?.[1] ?? '')
const run = computed(() => {
  const r = teams.runs[props.conversationId]
  return r && (!runId.value || r.runId === runId.value) ? r : null
})
watch(
  () => run.value?.runId,
  (id) => {
    if (id) teams.reported[id] = true
  },
  { immediate: true },
)
onBeforeUnmount(() => {
  if (run.value) delete teams.reported[run.value.runId]
})

const expert = (id: string) => state.experts.find((e) => e.id === id)

/** 看板上的结论文字形如「结论（已核验）」：拆出核验标记 */
function split(text: string) {
  const m = /^(.*)（(已核验|来源已核实|未核实|核验失败)）$/.exec(text.trim())
  if (!m) return { text, tag: '', cls: '' }
  const cls = m[2] === '已核验' ? 'ok' : m[2] === '来源已核实' ? 'src' : 'no'
  return { text: m[1], tag: m[2], cls }
}
const members = computed(() => {
  const r = run.value
  if (!r) return []
  return r.order
    .map((id) => {
      const findings = r.board.filter((b) => b.expertId === id && b.kind === 'finding').map((b) => split(b.text))
      return { id, name: r.members[id] ? (expert(id)?.name ?? r.board.find((b) => b.expertId === id)?.name ?? id) : id, findings }
    })
    .filter((m) => m.findings.length)
})
const files = computed(() => [...new Set((run.value?.board ?? []).filter((b) => b.kind === 'file').map((b) => b.text))])
const base = (p: string) => p.split(/[\\/]/).pop() ?? p
const ext = (p: string) => (base(p).split('.').pop() ?? '').slice(0, 4).toUpperCase()

const CHECK = ['consistent', 'differs', 'conflict', 'met', 'missed']
const checkKind = (k: string) => (CHECK.includes(k) ? k : 'consistent')

// ---------- 评价 ----------
const choosing = ref(0)
const feedback = ref('')
const canRate = computed(() => !!run.value && run.value.finished && !currentState.value?.busy)
function pick(r: number) {
  if (!run.value) return
  if (run.value.rating === r && !choosing.value) {
    void rateTeamRun(props.conversationId, 0)
    return
  }
  choosing.value = r
  feedback.value = run.value.rating === r ? (run.value.feedback ?? '') : ''
}
function send() {
  if (!choosing.value) return
  void rateTeamRun(props.conversationId, choosing.value, feedback.value.trim())
  choosing.value = 0
}
function openFile(path: string) {
  const name = base(path)
  void launchFile({ path, name, extension: name.includes('.') ? '.' + (name.split('.').pop() ?? '') : '', size: 0, modifiedAt: '', previewable: false, exists: true })
}
</script>

<template>
  <div v-if="report.title || report.conclusion" class="report">
    <h3><FileText :size="16" /> {{ t('ui.teamRun.report.title', { title: report.title }) }}</h3>
    <div class="verdict"><b>{{ t('ui.teamRun.report.conclusion') }}</b>{{ report.conclusion }}</div>

    <details v-for="m in members" :key="m.id" class="member">
      <summary>
        <ExpertAvatar :avatar="expert(m.id)?.avatar ?? ''" :name="m.name" :id="m.id" :size="24" />
        <b>{{ m.name }}</b>
        <span class="chev">{{ t('ui.teamRun.report.findings', { n: m.findings.length }) }}<ChevronRight :size="13" /></span>
      </summary>
      <ul>
        <li v-for="(f, i) in m.findings" :key="i">
          {{ f.text }}<span v-if="f.tag" class="ev" :class="f.cls">{{ f.tag }}</span>
        </li>
      </ul>
    </details>

    <div v-if="files.length" class="files">
      <button v-for="f in files" :key="f" type="button" class="file" :title="f" @click="openFile(f)">
        <i>{{ ext(f) }}</i>{{ base(f) }}
      </button>
    </div>

    <div v-if="report.checks.length" class="xcheck">
      <b>{{ t('ui.teamRun.report.checks') }}</b>
      <div v-for="(c, i) in report.checks" :key="i">
        <span class="ev" :class="checkKind(c.kind)">{{ t(`ui.teamRun.report.kind.${checkKind(c.kind)}`) }}</span>{{ c.text }}
      </div>
    </div>
    <div v-if="report.confirm.length" class="todo">
      <b>{{ t('ui.teamRun.report.confirm') }}</b>
      <div v-for="(c, i) in report.confirm" :key="i">{{ c }}</div>
    </div>
    <div v-if="report.suggestions.length" class="todo">
      <b>{{ t('ui.teamRun.report.suggestions') }}</b>
      <div>{{ report.suggestions.map((s, i) => `${'①②③④⑤⑥⑦⑧⑨⑩'[i] ?? i + 1 + '.'} ${s}`).join('；') }}</div>
    </div>

    <div v-if="canRate" class="rate">
      <span>{{ run!.rating ? t('ui.teamRun.panel.ratedAs') : t('ui.teamRun.report.rateQuestion') }}</span>
      <button type="button" :aria-pressed="(choosing || run!.rating) === 1" @click="pick(1)"><ThumbsUp :size="13" /> {{ t('ui.teamRun.report.up') }}</button>
      <button type="button" :aria-pressed="(choosing || run!.rating) === -1" @click="pick(-1)"><ThumbsDown :size="13" /> {{ t('ui.teamRun.report.down') }}</button>
      <span v-if="run!.feedback && !choosing" class="fb">“{{ run!.feedback }}”</span>
      <div v-if="choosing" class="fbrow">
        <input
          :id="`team-report-feedback-${callId}`"
          v-model="feedback"
          maxlength="200"
          :placeholder="choosing === 1 ? t('ui.teamRun.panel.feedbackUp') : t('ui.teamRun.panel.feedbackDown')"
          @keydown.enter.prevent="send"
        />
        <button type="button" class="send" @click="send">{{ t('ui.teamRun.panel.sendRating') }}</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.report {
  margin: 8px 0 4px;
  padding: 14px 16px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-card);
  overflow-wrap: anywhere;
}
h3 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: var(--t-md, 15px);
  font-weight: 600;
}
.verdict {
  margin: 8px 0 12px;
  padding: 10px 12px;
  border-radius: 10px;
  background: var(--cloth-sunk);
  font-size: var(--t-sm);
}
.member {
  padding: 8px 0;
  border-top: 1px solid var(--line);
}
.member summary {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: var(--t-sm);
  list-style: none;
  cursor: pointer;
}
.member summary::-webkit-details-marker {
  display: none;
}
.member .chev {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  margin-left: auto;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.member[open] .chev :deep(svg) {
  transform: rotate(90deg);
}
.member ul {
  margin: 6px 0 2px 34px;
  padding: 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.ev {
  margin-left: 4px;
  padding: 0 6px;
  border-radius: 8px;
  font-size: 11px;
  white-space: nowrap;
}
.ev.ok,
.ev.consistent,
.ev.met {
  background: var(--thread-wash);
  color: var(--thread);
}
.ev.src {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.ev.no,
.ev.differs {
  background: var(--amber-wash);
  color: var(--amber);
}
.ev.conflict,
.ev.missed {
  background: var(--red-wash);
  color: var(--red);
}
.files {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin: 10px 0;
}
.file {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  max-width: 100%;
  padding: 6px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-xs);
  cursor: pointer;
}
.file i {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--thread);
  color: #fff;
  font-size: 10px;
  font-style: normal;
  font-weight: 600;
}
.xcheck {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin: 10px 0 0;
  padding: 10px 12px;
  border-radius: 10px;
  background: var(--cloth-sunk);
  font-size: var(--t-sm);
}
.xcheck .ev {
  margin: 0 6px 0 0;
}
.todo {
  margin: 8px 0 0;
  padding: 10px 12px;
  border: 1px dashed var(--line-strong);
  border-radius: 10px;
  font-size: var(--t-sm);
}
.todo b {
  display: block;
  margin-bottom: 4px;
}
.rate {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 12px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.rate button {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink-soft);
  cursor: pointer;
}
.rate button[aria-pressed='true'] {
  border-color: var(--thread);
  background: var(--thread-wash);
  color: var(--thread);
}
.fb {
  flex-basis: 100%;
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
.rate .send {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
</style>
