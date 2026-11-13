<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ClipboardList, Play, X } from '@lucide/vue'
import ExpertAvatar from './ExpertAvatar.vue'
import { answerTeamPlan, currentState, current } from '../store'

/**
 * 分工计划卡（docs/team-mode.md 7.4）：队长提交计划后停在输入框上方，员工看过才执行。
 * - 可以去掉成员（下游对它的依赖一起去掉）、让队长改（写一句话）、取消；
 * - 点「开始执行」之前不会动任何东西。
 */
const { t } = useI18n()
const pending = computed(() => currentState.value?.team?.pending ?? null)
const removed = ref<string[]>([])
const revising = ref(false)
const reviseText = ref('')

watch(
  () => pending.value?.requestId,
  () => {
    removed.value = []
    revising.value = false
    reviseText.value = ''
  },
)

const kept = computed(() => (pending.value?.items ?? []).filter((i) => !removed.value.includes(i.expertId)))
const nameOf = (id: string) => pending.value?.items.find((i) => i.expertId === id)?.name ?? id
const indexOf = (id: string) => (pending.value?.items.findIndex((i) => i.expertId === id) ?? -1) + 1
const wan = (n: number) => Math.round(n / 10000)
const readOnlyCount = computed(() => kept.value.filter((i) => i.readOnly).length)

function toggle(id: string) {
  removed.value = removed.value.includes(id) ? removed.value.filter((x) => x !== id) : [...removed.value, id]
}

function deps(dependsOn: string[]) {
  const live = dependsOn.filter((d) => !removed.value.includes(d))
  return live.length ? t('ui.teamRun.waitFor', { list: live.map(indexOf).join('、') }) : t('ui.teamRun.startNow')
}

function act(action: 'start' | 'revise' | 'cancel') {
  const id = current.value?.id
  if (!id) return
  if (action === 'revise' && !reviseText.value.trim()) return
  void answerTeamPlan(id, action, removed.value, reviseText.value.trim())
}
</script>

<template>
  <div v-if="pending" class="plan-card" role="dialog" :aria-label="t('ui.teamRun.planTitle')">
    <div class="head">
      <ClipboardList :size="16" />
      <strong>{{ pending.round === 1 ? t('ui.teamRun.planTitle') : t('ui.teamRun.assignTitle') }}</strong>
    </div>
    <p class="goal">{{ pending.round === 1 ? t('ui.teamRun.goal', { goal: pending.goal }) : t('ui.teamRun.reason', { reason: pending.goal }) }}</p>
    <p v-if="pending.skipped.length" class="skipped">{{ t('ui.teamRun.skipped', { names: pending.skipped.join('、') }) }}</p>
    <div class="table">
      <div v-for="(item, i) in pending.items" :key="item.expertId" class="row" :class="{ removed: removed.includes(item.expertId) }">
        <span class="n">{{ i + 1 }}</span>
        <span class="who">
          <ExpertAvatar :avatar="item.avatar" :name="item.name" :id="item.expertId" :size="22" />
          <b>{{ item.name }}</b>
        </span>
        <span class="what">
          {{ item.task }}
          <small class="done">{{ t('ui.teamRun.done', { done: item.done }) }}</small>
        </span>
        <span class="dep">
          {{ deps(item.dependsOn) }}
          <small :class="item.readOnly ? 'ro' : 'rw'">{{ item.readOnly ? t('ui.teamRun.readOnly') : t('ui.teamRun.writes') }}</small>
        </span>
        <button type="button" class="rm" @click="toggle(item.expertId)">
          {{ removed.includes(item.expertId) ? t('ui.teamRun.addBack') : t('ui.teamRun.remove') }}
        </button>
      </div>
    </div>
    <p class="foot">
      <span>{{ t('ui.teamRun.members', { n: kept.length }) }}</span>
      <span>{{ t('ui.teamRun.estimate', { n: wan((pending.estimateTokens / Math.max(1, pending.items.length)) * kept.length), max: wan(pending.budgetTokens) }) }}</span>
      <span v-if="readOnlyCount">{{ t('ui.teamRun.parallel', { n: readOnlyCount }) }}</span>
    </p>
    <div class="actions">
      <button type="button" class="btn primary" :disabled="!kept.length" @click="act('start')">
        <Play :size="14" /> {{ t('ui.teamRun.start') }}
      </button>
      <button type="button" class="btn" @click="revising = !revising">{{ t('ui.teamRun.revise') }}</button>
      <button type="button" class="btn ghost" @click="act('cancel')"><X :size="14" /> {{ t('ui.teamRun.cancel') }}</button>
      <span class="hint">{{ t('ui.teamRun.hint') }}</span>
    </div>
    <div v-if="revising" class="revise">
      <input
        id="team-revise"
        v-model="reviseText"
        :placeholder="t('ui.teamRun.revisePlaceholder')"
        @keydown.enter.prevent="act('revise')"
      />
      <button type="button" class="btn sm" :disabled="!reviseText.trim()" @click="act('revise')">{{ t('ui.teamRun.reviseSend') }}</button>
    </div>
    <p v-if="removed.length" class="note">{{ t('ui.teamRun.removedNote', { names: removed.map(nameOf).join('、') }) }}</p>
  </div>
</template>

<style scoped>
.plan-card {
  max-width: var(--column);
  margin: 0 auto 10px;
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--indigo) 35%, var(--line));
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-card);
}
.head {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--indigo);
  font-size: var(--t-sm);
}
.head strong {
  color: var(--ink);
}
.goal {
  margin: 6px 0 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.skipped {
  margin: -4px 0 8px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.table {
  display: flex;
  flex-direction: column;
  max-height: 300px;
  overflow: auto;
}
.row {
  display: grid;
  grid-template-columns: 18px minmax(120px, auto) minmax(0, 1fr) auto auto;
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
.rm {
  border: 0;
  background: none;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  cursor: pointer;
}
.foot {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 14px;
  margin: 8px 0 10px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
.actions .hint {
  margin-left: auto;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}
.btn.ghost {
  border-color: transparent;
  background: none;
}
.btn.sm {
  padding: 3px 10px;
  font-size: var(--t-xs);
}
.revise {
  display: flex;
  gap: 8px;
  margin-top: 10px;
}
.revise input {
  flex: 1;
  min-width: 0;
  padding: 6px 10px;
  border: 1px solid var(--line-strong);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
}
.note {
  margin: 8px 0 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
@media (max-width: 760px) {
  .row {
    grid-template-columns: 18px minmax(0, 1fr) auto;
  }
  .what,
  .dep {
    grid-column: 2 / -1;
  }
}
</style>
