<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { refreshUsage, state, usagePercent } from '../store'
import { compactNumber } from '../experts'

/**
 * 输入框底栏的今日额度圆环（同 Claude Code 的用量环）：按后台给这台电脑设的每日 token 上限和今天的用量画，
 * 悬停看已用 / 剩余 / 几点重置，点一下打开「用量」详情。IT 没设上限（0）时不显示。
 *
 * 数据就是 state.usage（启动时、每次任务结束后都会刷新）；这里再补两种刷新：
 * 过了重置时间立刻刷新一次，窗口开着时每 10 分钟刷一次（别的设备、手机、定时任务也在用同一份额度）。
 */
const { t, locale } = useI18n()

const usage = computed(() => state.usage)
const limited = computed(() => !!usage.value && usage.value.dailyLimit > 0)
const percent = computed(() => usagePercent.value)
const level = computed(() =>
  usage.value?.exceeded || percent.value >= 90 ? 'danger' : percent.value >= 70 ? 'warn' : 'ok',
)

// 圆环：r=7，周长 2πr
const R = 7
const C = 2 * Math.PI * R
const dash = computed(() => `${(C * Math.min(100, percent.value)) / 100} ${C}`)

const now = ref(Date.now())
const resetText = computed(() => {
  const iso = usage.value?.resetsAt
  if (!iso) return ''
  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return ''
  const time = at.toLocaleTimeString(locale.value, { hour: '2-digit', minute: '2-digit' })
  const sameDay = at.toDateString() === new Date(now.value).toDateString()
  return t(sameDay ? 'ui.quota.resetToday' : 'ui.quota.resetTomorrow', { time })
})

let timer: ReturnType<typeof setInterval> | undefined
let last = 0
function tick() {
  now.value = Date.now()
  const reset = usage.value?.resetsAt ? new Date(usage.value.resetsAt).getTime() : 0
  // 过了重置时间（多等 30 秒，让服务端先换天），或者距上次刷新超过 10 分钟
  if ((reset && now.value > reset + 30_000) || now.value - last > 10 * 60_000) {
    last = now.value
    void refreshUsage()
  }
}
onMounted(() => {
  last = Date.now()
  if (!state.usage) void refreshUsage()
  timer = setInterval(tick, 60_000)
})
onBeforeUnmount(() => clearInterval(timer))

const open = () => (state.usageOpen = true)
</script>

<template>
  <button
    v-if="limited && usage"
    type="button"
    class="quota"
    :class="level"
    :aria-label="t('ui.quota.aria', { p: percent })"
    @click="open"
  >
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden="true">
      <circle class="track" cx="9" cy="9" :r="R" />
      <circle class="fill" cx="9" cy="9" :r="R" :stroke-dasharray="dash" transform="rotate(-90 9 9)" />
    </svg>
    <span class="tip" role="tooltip">
      <strong>{{ usage.exceeded ? t('ui.quota.exceeded') : t('ui.quota.used', { p: percent }) }}</strong>
      <span>{{ t('ui.quota.detail', { used: compactNumber(usage.todayTokens), left: compactNumber(usage.remaining), total: compactNumber(usage.dailyLimit) }) }}</span>
      <small v-if="resetText">{{ resetText }}</small>
    </span>
  </button>
</template>

<style scoped>
.quota {
  position: relative;
  display: inline-grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 8px;
  color: var(--thread);
}
.quota:hover {
  background: var(--cloth-sunk);
}
.quota.warn {
  color: var(--amber);
}
.quota.danger {
  color: var(--red);
}
.track {
  fill: none;
  stroke: var(--line);
  stroke-width: 2.4;
}
.fill {
  fill: none;
  stroke: currentColor;
  stroke-width: 2.4;
  stroke-linecap: round;
  transition: stroke-dasharray 300ms ease;
}
.tip {
  position: absolute;
  right: 0;
  bottom: calc(100% + 8px);
  z-index: 30;
  display: none;
  flex-direction: column;
  gap: 2px;
  min-width: 190px;
  padding: 8px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  color: var(--ink);
  font-size: var(--t-xs);
  line-height: 1.5;
  text-align: left;
  white-space: nowrap;
}
.tip strong {
  font-size: var(--t-sm);
  font-weight: 600;
}
.tip small {
  color: var(--ink-faint);
}
.quota:hover .tip,
.quota:focus-visible .tip {
  display: flex;
}
</style>
