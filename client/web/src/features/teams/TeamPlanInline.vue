<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, ClipboardList, Hand, RotateCcw, X } from '@lucide/vue'
import ExpertAvatar from '../../components/ExpertAvatar.vue'
import TeamPlanCard from './TeamPlanCard.vue'
import { state } from '../../store'
import { teams } from './store'

/**
 * 对话里的分工计划卡（team_plan / team_assign 这次调用）：和演示一致，留在队长那条消息下面。
 * - 等员工回答时：可交互的计划卡（去掉成员、让队长改、取消、开始执行）；
 * - 回答之后：定稿的计划表 + 「✓ 你已确认计划，团队开始执行」这类结论；
 * - 分工计划（第 1 轮）下面接着画团队开工授权卡和员工的回答。
 */
const props = defineProps<{ name: string; callId: string; args: string; conversationId: string; output: string }>()
const { t } = useI18n()

interface Item {
  member: string
  task: string
  done: string
  depends_on?: string[]
}

const parsed = computed(() => {
  try {
    const a = JSON.parse(props.args || '{}') as { goal?: string; reason?: string; items?: Item[] }
    return { goal: (a.goal ?? a.reason ?? '').trim(), items: Array.isArray(a.items) ? a.items.filter((i) => i && i.member) : [] }
  } catch {
    return { goal: '', items: [] as Item[] }
  }
})

/** 这次调用对应的那次团队任务：成员的 callId 是「这次调用的 ID#专家」 */
const run = computed(() => {
  const r = teams.runs[props.conversationId]
  if (!r) return null
  if (r.pending?.callId === props.callId) return r
  return Object.values(r.members).some((m) => m.callId.startsWith(`${props.callId}#`)) ? r : null
})
const pending = computed(() => (run.value?.pending?.callId === props.callId ? run.value.pending : null))
const round = computed(() => (props.name === 'team_assign' ? 2 : 1))

const expert = (id: string) => state.experts.find((e) => e.id === id)
const nameOf = (id: string) => expert(id)?.name ?? id
const indexOf = (id: string) => parsed.value.items.findIndex((i) => i.member === id) + 1
/** 员工在计划卡上去掉的成员：这次任务实际没有派到 */
const removed = (id: string) => !!run.value && !pending.value && Object.keys(run.value.members).length > 0 && !run.value.members[id]

const outcome = computed<{ kind: 'ok' | 'revise' | 'cancel' | 'bad' | 'wait'; text: string }>(() => {
  const out = props.output.trim()
  if (pending.value) return { kind: 'wait', text: '' }
  if (!out) {
    // 还没结果：员工已经点了开始（成员已开工），或者还在准备
    return run.value && Object.keys(run.value.members).length
      ? { kind: 'ok', text: t(round.value === 2 ? 'ui.teamRun.inline.assignStarted' : 'ui.teamRun.inline.started') }
      : { kind: 'wait', text: t('ui.teamRun.inline.preparing') }
  }
  if (out.startsWith('员工要求修改计划')) return { kind: 'revise', text: t('ui.teamRun.inline.revised', { text: out.replace(/^员工要求修改计划：/, '').split('\n')[0] }) }
  if (out.startsWith('员工取消') || out.startsWith('员工没有同意补派')) return { kind: 'cancel', text: t(round.value === 2 ? 'ui.teamRun.inline.assignDeclined' : 'ui.teamRun.inline.cancelled') }
  if (out.startsWith('员工把成员都去掉了')) return { kind: 'cancel', text: t('ui.teamRun.inline.allRemoved') }
  if (out.startsWith('分工计划有问题') || out.startsWith('补派有问题') || out.startsWith('已经')) return { kind: 'bad', text: t('ui.teamRun.inline.invalid') }
  return { kind: 'ok', text: t(round.value === 2 ? 'ui.teamRun.inline.assignStarted' : 'ui.teamRun.inline.started') }
})

const kept = computed(() => parsed.value.items.filter((i) => !removed(i.member)))
const readOnlyCount = computed(() => kept.value.filter((i) => expert(i.member)?.readOnly).length)
const budget = computed(() => Math.round((run.value?.budgetTokens ?? 1_000_000) / 10_000))
const grant = computed(() => (round.value === 1 && run.value?.grantLabels?.length ? run.value : null))
</script>

<template>
  <!-- 等员工回答：可交互的计划卡 -->
  <TeamPlanCard v-if="pending" :pending="pending" :conversation-id="conversationId" />
  <template v-else-if="parsed.items.length">
    <div class="team-card">
      <h3><ClipboardList :size="16" /> {{ round === 2 ? t('ui.teamRun.assignTitle') : t('ui.teamRun.planTitle') }}<template v-if="run?.teamName"> · {{ run.teamName }}</template></h3>
      <p v-if="parsed.goal" class="goal">{{ round === 2 ? t('ui.teamRun.reason', { reason: parsed.goal }) : t('ui.teamRun.goal', { goal: parsed.goal }) }}</p>
      <div class="rows">
        <div v-for="(item, i) in parsed.items" :key="item.member" class="row" :class="{ removed: removed(item.member) }">
          <span class="n">{{ i + 1 }}</span>
          <span class="who">
            <ExpertAvatar :avatar="expert(item.member)?.avatar ?? ''" :name="nameOf(item.member)" :id="item.member" :size="22" />
            <b>{{ nameOf(item.member) }}</b>
          </span>
          <span class="what">
            {{ item.task }}
            <small class="done">{{ t('ui.teamRun.done', { done: item.done }) }}</small>
          </span>
          <span class="dep">
            {{ item.depends_on?.length ? t('ui.teamRun.waitFor', { list: item.depends_on.map(indexOf).filter((n) => n > 0).join('、') }) : t('ui.teamRun.startNow') }}
            <small :class="expert(item.member)?.readOnly ? 'ro' : 'rw'">{{ expert(item.member)?.readOnly ? t('ui.teamRun.readOnly') : t('ui.teamRun.writes') }}</small>
          </span>
        </div>
      </div>
      <p class="foot">
        <span>{{ t('ui.teamRun.members', { n: kept.length }) }}</span>
        <span>{{ t('ui.teamRun.estimate', { n: kept.length * 13, max: budget }) }}</span>
        <span v-if="readOnlyCount">{{ t('ui.teamRun.parallel', { n: readOnlyCount }) }}</span>
      </p>
      <p class="note" :class="outcome.kind">
        <Check v-if="outcome.kind === 'ok'" :size="14" />
        <RotateCcw v-else-if="outcome.kind === 'revise'" :size="14" />
        <X v-else-if="outcome.kind === 'cancel'" :size="14" />
        {{ outcome.text }}
      </p>
    </div>
    <!-- 团队开工授权：员工回答之后留在对话里 -->
    <div v-if="grant" class="team-card grant">
      <h3><Hand :size="16" /> {{ t('ui.teamRun.inline.grantTitle') }}</h3>
      <p class="goal">{{ t('ui.teamRun.inline.grantIntro', { n: grant.grantLabels!.length }) }}</p>
      <ul>
        <li v-for="l in grant.grantLabels" :key="l">{{ l }}</li>
      </ul>
      <p class="note" :class="grant.granted ? 'ok' : 'cancel'">
        <Check v-if="grant.granted" :size="14" />
        {{ grant.granted ? t('ui.teamRun.inline.granted') : t('ui.teamRun.inline.notGranted') }}
      </p>
    </div>
  </template>
</template>

<style scoped>
.team-card {
  margin: 8px 0 4px;
  padding: 14px 16px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-card);
}
.team-card h3 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: var(--t-sm);
  font-weight: 600;
}
.goal {
  margin: 6px 0 10px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.rows {
  display: flex;
  flex-direction: column;
}
.row {
  display: grid;
  grid-template-columns: 18px minmax(120px, auto) minmax(0, 1fr) auto;
  gap: 10px;
  align-items: start;
  padding: 8px 2px;
  border-top: 1px solid var(--line);
  font-size: var(--t-sm);
}
.row.removed {
  opacity: 0.4;
}
.row.removed .what {
  text-decoration: line-through;
}
.n {
  color: var(--ink-faint);
}
.who {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  white-space: nowrap;
}
.what {
  min-width: 0;
  overflow-wrap: anywhere;
}
.done {
  display: block;
  color: var(--thread);
  font-size: var(--t-xs);
}
.dep {
  color: var(--ink-faint);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.dep small {
  display: block;
}
.dep .rw {
  color: var(--amber);
}
.foot {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 14px;
  margin: 8px 0;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.note {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.note.ok {
  color: var(--thread);
}
.note.revise {
  color: var(--indigo);
}
.note.cancel,
.note.bad {
  color: var(--ink-faint);
}
.grant {
  border-color: color-mix(in srgb, var(--amber) 45%, var(--line));
  background: var(--amber-wash);
  box-shadow: none;
}
.grant h3 {
  color: var(--amber);
}
.grant ul {
  margin: 0 0 8px;
  padding-left: 20px;
  font-size: var(--t-sm);
}
@media (max-width: 760px) {
  .row {
    grid-template-columns: 18px minmax(0, 1fr);
  }
  .what,
  .dep {
    grid-column: 2 / -1;
  }
}
</style>
