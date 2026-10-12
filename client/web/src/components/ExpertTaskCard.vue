<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { BriefcaseBusiness, ChevronRight, CircleAlert, Clock, FileText, Hand, LoaderCircle } from '@lucide/vue'
import { openSubtaskDetail, state, subtaskFor } from '../store'
import { artifactExt, askExpertArgs, formatDuration, oneLine, openArtifact, toolLabel, VerificationOrder, verificationCounts } from '../subtasks'
import type { ToolActivity } from '../types'
import ExpertAvatar from './ExpertAvatar.vue'
import BrandLoader from './BrandLoader.vue'

// 主助手派给专家的子任务（ask_expert 调用）：代替普通工具卡片。
// 运行中显示专家此刻真实在做的那一步；结束后显示结论、核验情况和产出文件，详情在弹窗里
const props = defineProps<{ tool: ToolActivity; conversationId: string }>()
const { t } = useI18n()

const sub = computed(() => subtaskFor(props.conversationId, props.tool.callId))
const fromArgs = computed(() => askExpertArgs(props.tool.args))
const expert = computed(() => sub.value?.expert ?? fromArgs.value.expert)
const task = computed(() => sub.value?.task || fromArgs.value.task)
const result = computed(() => sub.value?.result ?? null)
const live = computed(() => props.tool.state === 'running' || props.tool.state === 'waiting')

/**
 * 卡片处在哪个阶段：
 * dispatching 工具已经开始、专家还没接手；running 专家在跑；final 已有结果；
 * plain 没有子任务信息、工具本身已经结束（没派出去，或者老版本宿主）
 */
const phase = computed<'dispatching' | 'running' | 'final' | 'plain'>(() => {
  if (result.value) return 'final'
  if (sub.value) return 'running'
  return live.value ? 'dispatching' : 'plain'
})

/** 这位专家有没有挂着等员工确认的操作（确认事件可能比 waiting_confirmation 那一步先到） */
const waiting = computed(() => {
  const v = sub.value
  if (!v || phase.value !== 'running') return false
  if (v.status === 'waiting_confirmation') return true
  return Object.values(state.byId[props.conversationId]?.tools ?? {}).some(
    (x) => x.state === 'waiting' && (x.actor?.subtaskId === v.subtaskId || x.callId === props.tool.callId),
  )
})

const status = computed(() => {
  if (result.value) return result.value.status
  if (phase.value === 'plain') return props.tool.state === 'done' ? 'completed' : 'failed'
  return waiting.value ? 'waiting_confirmation' : 'running'
})

/** 运行中那一行：专家这一刻在做什么 */
const stepText = computed(() => {
  const step = sub.value?.step
  if (waiting.value && (!step || step.kind !== 'waiting_confirmation')) return t('ui.delegate.waitingShort')
  if (!step) return t('ui.delegate.starting')
  if (step.kind === 'thinking') return t('ui.delegate.thinking')
  const name = toolLabel(step.name)
  if (step.kind === 'waiting_confirmation') return name ? t('ui.delegate.waiting', { name }) : t('ui.delegate.waitingShort')
  if (step.kind === 'tool_completed') return t(step.ok === false ? 'ui.delegate.callFailed' : 'ui.delegate.called', { name })
  return t('ui.delegate.calling', { name })
})
const stepSummary = computed(() => (sub.value?.step?.kind === 'thinking' ? '' : sub.value?.step?.summary ?? ''))

// ---------- 计时 ----------
const mountedAt = Date.now()
const now = ref(Date.now())
let timer: number | undefined
// 主助手这一轮已经结束、专家的结束事件还没补上时，时间停住，等 subtasks.list 给出最终状态
const ticking = computed(() => (phase.value === 'running' || phase.value === 'dispatching') && live.value)
watch(
  ticking,
  (on) => {
    clearInterval(timer)
    timer = on ? window.setInterval(() => (now.value = Date.now()), 1000) : undefined
  },
  { immediate: true },
)
onBeforeUnmount(() => clearInterval(timer))

const elapsed = computed(() => {
  const start = Date.parse(sub.value?.startedAt ?? '') || mountedAt
  return formatDuration(now.value - start)
})

// ---------- 结束后 ----------
const counts = computed(() => (result.value ? verificationCounts(result.value) : null))
const plainText = computed(() => oneLine(props.tool.output ?? '', 160))
</script>

<template>
  <div class="xtask" :class="[phase, `st-${status}`, { waiting }]">
    <div class="head">
      <ExpertAvatar v-if="expert" :avatar="expert.avatar" :name="expert.name" :id="expert.id" :size="30" />
      <span v-else class="avatar-ph"><BriefcaseBusiness :size="16" /></span>
      <div class="who">
        <span class="name">{{ expert?.name || t('ui.delegate.dispatching') }}</span>
        <span v-if="task" class="task" :title="task">{{ oneLine(task) }}</span>
      </div>
      <span class="badge" :class="status">{{ t(`ui.delegate.status.${status}`) }}</span>
    </div>

    <!-- 派遣中：工具开始了，专家还没接手 -->
    <div v-if="phase === 'dispatching'" class="step">
      <BrandLoader
        size="sm"
        :text="(expert ? t('ui.delegate.dispatchingTo', { name: expert.name }) : t('ui.delegate.dispatching')).replace(/[.…]+$/, '')"
      />
      <span class="time"><Clock :size="12" />{{ elapsed }}</span>
    </div>

    <!-- 运行中：专家此刻真实在做的那一步 -->
    <div v-else-if="phase === 'running'" class="step" :class="{ waiting }" role="status">
      <Hand v-if="waiting" :size="15" class="step-icon" />
      <LoaderCircle v-else :size="15" class="step-icon spin" />
      <span class="step-text">{{ stepText }}</span>
      <code v-if="stepSummary" class="step-sum" :title="stepSummary">{{ stepSummary }}</code>
      <span class="time"><Clock :size="12" />{{ t('ui.delegate.elapsed', { t: elapsed }) }}</span>
    </div>

    <!-- 已结束 -->
    <template v-else-if="phase === 'final' && result">
      <p class="summary">{{ result.summary || t('ui.delegate.noSummary') }}</p>
      <p v-if="result.error || result.errorCode" class="error">
        <CircleAlert :size="14" />
        <span>{{ result.error || result.errorCode }}<code v-if="result.error && result.errorCode">{{ result.errorCode }}</code></span>
      </p>
      <div v-if="result.findings.length && counts" class="verify">
        <strong>{{ t('ui.delegate.findings', { n: result.findings.length }) }}</strong>
        <span
          v-for="v in VerificationOrder"
          :key="v"
          class="vc"
          :class="[v, { zero: counts[v] === 0 }]"
          :title="t(`ui.delegate.verifyHint.${v}`)"
          ><i />{{ t(`ui.delegate.verify.${v}`) }} {{ counts[v] }}</span
        >
      </div>
      <div v-if="result.artifacts.length" class="chips">
        <button
          v-for="a in result.artifacts"
          :key="a.path"
          type="button"
          class="chip"
          :class="{ gone: !a.exists }"
          :disabled="!a.exists"
          :title="a.exists ? a.path : t('ui.delegate.missingFile')"
          @click="openArtifact(a)"
        >
          <FileText :size="13" :data-ext="artifactExt(a)" />
          <span>{{ a.name }}</span>
        </button>
      </div>
      <div class="foot">
        <span class="time"><Clock :size="12" />{{ formatDuration(result.durationMs) }}</span>
        <button type="button" class="more" @click="openSubtaskDetail(result)">
          {{ t('ui.delegate.details') }} <ChevronRight :size="14" />
        </button>
      </div>
    </template>

    <!-- 没有子任务信息：没派出去（开关关了、次数用完），或者老版本宿主 -->
    <p v-else-if="phase === 'plain' && (tool.state !== 'done' || plainText)" class="summary muted">
      <template v-if="tool.state !== 'done'">{{ t('ui.delegate.notStarted') }}<template v-if="plainText">：</template></template>{{ plainText }}
    </p>
  </div>
</template>

<style scoped>
.xtask {
  --tone: var(--indigo);
  margin: 8px 0;
  padding: 10px 12px 10px;
  border: 1px solid color-mix(in srgb, var(--tone) 26%, var(--line));
  border-radius: var(--r-md);
  background: var(--cloth);
}
.xtask.running,
.xtask.dispatching {
  background: color-mix(in srgb, var(--indigo-wash) 55%, var(--cloth));
}
.xtask.waiting {
  --tone: var(--amber);
  background: var(--amber-wash);
}
.head {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}
.avatar-ph {
  flex: none;
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 28%;
  background: var(--chip);
  color: var(--ink-soft);
}
.who {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.name {
  overflow: hidden;
  font-size: var(--t-sm);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.task {
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 状态徽章：每种结果一个颜色 */
.badge {
  flex: none;
  padding: 2px 9px;
  border-radius: 999px;
  background: var(--indigo-wash);
  color: var(--indigo);
  font-size: var(--t-xs);
  font-weight: 600;
  white-space: nowrap;
}
.badge.completed {
  background: var(--thread-wash);
  color: var(--thread);
}
.badge.partial,
.badge.waiting_confirmation {
  background: var(--amber-wash);
  color: var(--amber);
}
.badge.needs_input {
  background: color-mix(in srgb, #7c3aed 13%, var(--cloth));
  color: color-mix(in srgb, #7c3aed 80%, var(--ink));
}
.badge.failed {
  background: var(--red-wash);
  color: var(--red);
}
.badge.timed_out {
  background: color-mix(in srgb, #d9480f 13%, var(--cloth));
  color: color-mix(in srgb, #d9480f 85%, var(--ink));
}
.badge.cancelled {
  background: var(--chip);
  color: var(--ink-soft);
}
.xtask.waiting .badge {
  background: color-mix(in srgb, var(--amber) 16%, var(--cloth));
}

.step {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  margin-top: 8px;
  padding: 6px 10px;
  border-radius: 8px;
  background: var(--cloth);
  font-size: var(--t-sm);
}
.step.waiting {
  border: 1px solid color-mix(in srgb, var(--amber) 45%, transparent);
  color: var(--amber);
  font-weight: 600;
}
.step :deep(.brand-loader) {
  flex: 1;
  justify-content: flex-start;
  min-width: 0;
  font-size: var(--t-sm);
}
.step-icon {
  flex: none;
  color: var(--indigo);
}
.step.waiting .step-icon {
  color: var(--amber);
}
.step-text {
  flex: none;
  max-width: 60%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.step-sum {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  font-family: var(--font-code);
  font-size: var(--t-xs);
  font-weight: 400;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.time {
  flex: none;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-left: auto;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-weight: 400;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}
.spin {
  animation: spin 900ms linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

.summary {
  margin: 8px 0 0;
  font-size: var(--t-sm);
  line-height: 1.55;
  word-break: break-word;
}
.summary.muted {
  color: var(--ink-soft);
}
.error {
  display: flex;
  gap: 6px;
  align-items: flex-start;
  margin: 6px 0 0;
  color: var(--red);
  font-size: var(--t-xs);
  word-break: break-word;
}
.error svg {
  flex: none;
  margin-top: 2px;
}
.error code {
  margin-left: 6px;
  padding: 0 5px;
  border-radius: 4px;
  background: var(--red-wash);
  font-family: var(--font-code);
}

/* 核验情况：一行小字，零条的变淡 */
.verify {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 10px;
  margin-top: 8px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  font-variant-numeric: tabular-nums;
}
.verify strong {
  color: var(--ink);
  font-weight: 600;
}
.vc {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  white-space: nowrap;
}
.vc i {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--thread);
}
.vc.source_verified i {
  background: var(--indigo);
}
.vc.unverified i {
  background: var(--ink-faint);
}
.vc.verification_failed i {
  background: var(--red);
}
.vc.zero {
  color: var(--ink-faint);
}
.vc.zero i {
  opacity: 0.45;
}

.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}
.chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: 100%;
  min-width: 0;
  padding: 3px 10px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-xs);
  cursor: pointer;
}
.chip span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.chip svg {
  flex: none;
  color: var(--indigo);
}
.chip svg[data-ext='xlsx'],
.chip svg[data-ext='xls'],
.chip svg[data-ext='csv'] {
  color: #1d6f42;
}
.chip:hover:not(:disabled) {
  border-color: var(--indigo);
  color: var(--indigo);
}
.chip.gone {
  color: var(--ink-faint);
  text-decoration: line-through;
  cursor: default;
}
.foot {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
}
.foot .time {
  margin-left: 0;
}
.more {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  margin-left: auto;
  padding: 3px 6px 3px 10px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth);
  color: var(--ink-soft);
  font-size: var(--t-xs);
  cursor: pointer;
}
.more:hover,
.more:focus-visible {
  border-color: var(--indigo);
  color: var(--indigo);
  outline: none;
}
@media (prefers-reduced-motion: reduce) {
  .spin {
    animation: none;
  }
}
</style>
