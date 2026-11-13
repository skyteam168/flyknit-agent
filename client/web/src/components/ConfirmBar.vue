<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, Check, Hand, HelpCircle, X } from '@lucide/vue'
import { answerConfirm, findWaitingConfirm, mcpToolLabel, state, subtaskById } from '../store'
import ExpertAvatar from './ExpertAvatar.vue'

// 停靠在输入框上方的确认条：等待确认的操作始终可见，不会被输入框挡住
const { t, te } = useI18n()

/** 不限当前对话：手机办事常在侧栏另一条会话里等确认 */
/**
 * 在拆分的原对话里，子任务的确认已经合在汇总卡里（GroupCard），这里就不再重复弹一张（docs/c2-router.md 3.3）。
 * 别的对话、别处的确认照旧在这里。
 */
const groupChildren = computed(() => new Set((state.currentId ? state.groups[state.currentId] ?? [] : []).flatMap((g) => g.items.map((i) => i.conversationId))))
const pending = computed(() => findWaitingConfirm(groupChildren.value))
const tool = computed(() => pending.value?.tool ?? null)
const conversationId = computed(() => pending.value?.conversationId ?? '')
/** 专家在请求确认：员工要知道是哪位专家在问 */
const actor = computed(() => (tool.value?.confirm?.actor?.kind === 'expert' ? tool.value.confirm.actor : null))
/** 专家要做的那一步：优先用专家最新一步（waiting_confirmation）里的工具名和说明 */
const expertStep = computed(() => {
  if (!actor.value || !conversationId.value) return null
  const step = subtaskById(conversationId.value, actor.value.subtaskId)?.step
  return step?.kind === 'waiting_confirmation' ? step : null
})
const toolName = computed(() => expertStep.value?.name || tool.value?.name || '')
const what = computed(() => expertStep.value?.summary || tool.value?.summary || '')
const label = computed(() =>
  mcpToolLabel(toolName.value) ?? (te(`tool.names.${toolName.value}`) ? t(`tool.names.${toolName.value}`) : toolName.value),
)

// 高危（删除、改系统）走红色样式，且默认按钮是「仅这一次」，避免顺手点成永久放行
const effect = computed(() => tool.value?.confirm?.effect ?? 'unknown')
const danger = computed(() => effect.value === 'destructive')
const ruleDisplay = computed(() => tool.value?.confirm?.ruleDisplay ?? '')

// 是否可以使用会话级授权（非高危命令都可以）
const canSessionApprove = computed(() => effect.value !== 'destructive')

/** 派遣时的一次授权（docs/approval-p0.md 第 5 节）：不是针对某一个调用，按钮换成「这次派遣内都直接执行 / 用到时逐条问我」 */
const bundle = computed(() => tool.value?.confirm?.bundle ?? null)

function answer(choice: 'allowOnce' | 'allowForSession' | 'allowAlways' | 'reject') {
  if (tool.value && conversationId.value) void answerConfirm(conversationId.value, tool.value.callId, choice)
}
</script>

<template>
  <div v-if="tool && tool.confirm" class="confirm-bar" :class="{ danger }" role="alertdialog" :aria-label="t('tool.confirmTitle')">
    <div class="head">
      <AlertTriangle v-if="danger" :size="16" />
      <HelpCircle v-else-if="effect === 'unknown'" :size="16" />
      <Hand v-else :size="16" />
      <strong>{{ danger ? t('tool.confirmDanger') : t('tool.confirmTitle') }}</strong>
      <span v-if="!actor" class="what-name">{{ label }}</span>
    </div>
    <div v-if="actor" class="actor">
      <ExpertAvatar :avatar="actor.avatar" :name="actor.name" :id="actor.id" :size="20" />
      <span class="actor-text">{{ t('ui.delegate.requests', { name: actor.name }) }}</span>
      <span v-if="!bundle" class="what-name">{{ label }}</span>
    </div>
    <template v-if="bundle">
      <p class="why">{{ t('tool.bundle.lead', { n: bundle.length }) }}</p>
      <ul class="bundle">
        <li v-for="b in bundle" :key="b">{{ b }}</li>
      </ul>
      <div class="actions">
        <button type="button" class="btn primary" @click="answer('allowForSession')">
          <Check :size="15" /> {{ t('tool.bundle.allow') }}
        </button>
        <button type="button" class="btn" @click="answer('reject')">{{ t('tool.bundle.perCall') }}</button>
      </div>
      <p class="note">{{ t('tool.bundle.note') }}</p>
    </template>
    <template v-else>
    <code v-if="what" class="what">{{ what }}</code>
    <p v-if="tool.confirm.rationale" class="why">{{ tool.confirm.rationale }}</p>
    <p v-if="tool.confirm.reason" class="reason">{{ tool.confirm.reason }}</p>
    <div class="actions">
      <button type="button" class="btn primary" @click="answer('allowOnce')">
        <Check :size="15" /> {{ t('tool.allowOnce') }}
      </button>
      <button v-if="canSessionApprove" type="button" class="btn" @click="answer('allowForSession')">
        {{ t('tool.allowForSession') }}
      </button>
      <button v-if="tool.confirm.rememberable" type="button" class="btn" @click="answer('allowAlways')">
        {{ t('tool.allowAlways') }}
      </button>
      <button type="button" class="btn" @click="answer('reject')"><X :size="15" /> {{ t('tool.reject') }}</button>
    </div>
    <p v-if="canSessionApprove && !tool.confirm.rememberable" class="note">{{ t('tool.sessionNote') }}</p>
    <p v-else-if="tool.confirm.rememberable && ruleDisplay" class="note">{{ t('tool.ruleNote', { rule: ruleDisplay }) }}</p>
    <p v-else-if="danger" class="note">{{ t('tool.dangerNote') }}</p>
    </template>
  </div>
</template>

<style scoped>
.bundle {
  margin: 4px 0 8px;
  padding-left: 18px;
  font-size: var(--t-sm);
}
.confirm-bar {
  max-width: var(--column);
  margin: 0 auto 10px;
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--amber) 40%, var(--line));
  border-radius: var(--r-lg);
  background: var(--amber-wash);
}
.confirm-bar.danger {
  border-color: color-mix(in srgb, var(--rust, #c0392b) 45%, var(--line));
  background: color-mix(in srgb, var(--rust, #c0392b) 8%, var(--cloth));
  box-shadow: var(--shadow-card);
  animation: rise 160ms ease-out;
}
.head {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--amber);
  font-size: var(--t-sm);
}
.what-name {
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
/* 专家请求的确认：头像 + 「某某专家 请求执行：」，和主助手自己的确认区分开 */
.actor {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  margin-top: 8px;
  font-size: var(--t-sm);
}
.actor-text {
  flex: none;
  color: var(--ink);
  font-weight: 600;
}
.actor .what-name {
  font-family: var(--font-code);
  font-size: var(--t-xs);
}
.more {
  margin-left: auto;
  padding: 0 7px;
  border-radius: 9px;
  background: var(--cloth);
  font-size: var(--t-xs);
}
.what {
  display: block;
  max-height: 120px;
  overflow: auto;
  margin: 8px 0;
  padding: 8px 10px;
  border-radius: var(--r-sm);
  background: var(--cloth);
  font-family: var(--font-code);
  font-size: var(--t-xs);
  white-space: pre-wrap;
  word-break: break-all;
}
.why,
.reason {
  margin: 0 0 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.reason {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
.note {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
@keyframes rise {
  from {
    opacity: 0;
    transform: translateY(6px);
  }
}
</style>
