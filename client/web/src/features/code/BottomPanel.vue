<script setup lang="ts">
/**
 * IDE 底部：终端 / 日志 / Agent Trace。
 * - 终端：AI 执行的命令和输出（和右侧「终端」标签同一份）；
 * - 日志：这个对话里每次工具调用的完整输出，按时间排成一条流，方便复制、对照；
 * - Agent Trace：AI 这一轮每一步做了什么（工具、对象、结果），点「完整链路」看每一步的耗时和 token。
 */
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, Check, ChevronDown, ChevronUp, Loader2, Route, ShieldAlert, X } from '@lucide/vue'
import { currentState, openTrace } from '../../store'
import TerminalTab from '../../components/TerminalTab.vue'
import { clampBottom, ide } from './store'

const { t } = useI18n()

const steps = computed(() => Object.values(currentState.value?.tools ?? {}))
const running = computed(() => steps.value.filter((s) => s.state === 'running' || s.state === 'waiting').length)
const lastTrace = computed(() => [...(currentState.value?.messages ?? [])].reverse().find((m) => m.trace)?.trace ?? null)

const logs = computed(() =>
  steps.value
    .filter((s) => s.output)
    .map((s) => `▸ ${s.name}  ${s.summary}\n${(s.output ?? '').length > 4000 ? s.output!.slice(0, 4000) + '\n…' : s.output}`)
    .join('\n\n'),
)

const logBox = ref<HTMLElement>()
watch(logs, async () => {
  await nextTick()
  const el = logBox.value
  if (el && el.scrollHeight - el.scrollTop - el.clientHeight < 80) el.scrollTop = el.scrollHeight
})

const icon = (state: string) =>
  state === 'running' || state === 'waiting' ? Loader2 : state === 'done' ? Check : state === 'blocked' || state === 'rejected' ? ShieldAlert : state === 'failed' ? AlertTriangle : X

// 顶上的横条：拖动调高度
const dragging = ref(false)
let startY = 0
let startH = 0
function down(e: PointerEvent) {
  dragging.value = true
  startY = e.clientY
  startH = ide.bottomHeight
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  e.preventDefault()
}
function move(e: PointerEvent) {
  if (dragging.value) ide.bottomHeight = clampBottom(startH + startY - e.clientY)
}
function up(e: PointerEvent) {
  dragging.value = false
  ;(e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId)
}

const tabs = ['terminal', 'logs', 'trace'] as const
function pick(tab: (typeof tabs)[number]) {
  ide.bottomTab = tab
  ide.bottomOpen = true
}
</script>

<template>
  <section class="bottom" :style="ide.bottomOpen ? { height: ide.bottomHeight + 'px' } : undefined">
    <div
      v-if="ide.bottomOpen"
      class="grip"
      :class="{ active: dragging }"
      role="separator"
      aria-orientation="horizontal"
      @pointerdown="down"
      @pointermove="move"
      @pointerup="up"
      @pointercancel="up"
    />
    <div class="bar" role="tablist">
      <button v-for="tab in tabs" :key="tab" type="button" role="tab" :aria-selected="ide.bottomTab === tab" :class="{ on: ide.bottomOpen && ide.bottomTab === tab }" @click="pick(tab)">
        {{ t(`ui.code.bottom.${tab}`) }}
        <span v-if="tab === 'trace' && running" class="badge">{{ running }}</span>
      </button>
      <span class="spacer" />
      <button v-if="ide.bottomTab === 'trace' && lastTrace" type="button" class="link" @click="openTrace(lastTrace)"><Route :size="13" /> {{ t('ui.code.fullTrace') }}</button>
      <button type="button" class="icon" :title="ide.bottomOpen ? t('ui.code.collapse') : t('ui.code.expand')" @click="ide.bottomOpen = !ide.bottomOpen">
        <ChevronDown v-if="ide.bottomOpen" :size="15" />
        <ChevronUp v-else :size="15" />
      </button>
    </div>
    <div v-if="ide.bottomOpen" class="content">
      <TerminalTab v-if="ide.bottomTab === 'terminal'" class="embedded" />
      <pre v-else-if="ide.bottomTab === 'logs'" ref="logBox" class="logs">{{ logs || t('ui.code.noLogs') }}</pre>
      <ol v-else class="trace">
        <li v-for="(s, i) in steps" :key="s.callId" :class="s.state">
          <span class="n">{{ i + 1 }}</span>
          <component :is="icon(s.state)" :size="13" class="st" :class="{ spin: s.state === 'running' }" />
          <code>{{ s.name }}</code>
          <span class="sum">{{ s.summary }}</span>
          <span class="state">{{ t(`ui.code.state.${s.state}`) }}</span>
        </li>
        <li v-if="!steps.length" class="none">{{ t('ui.code.noTrace') }}</li>
      </ol>
    </div>
  </section>
</template>

<style scoped>
.bottom {
  position: relative;
  display: flex;
  flex: none;
  flex-direction: column;
  min-height: 0;
  border-top: 1px solid var(--line);
  background: var(--loom);
}
.grip {
  position: absolute;
  top: -3px;
  left: 0;
  right: 0;
  height: 6px;
  cursor: row-resize;
  z-index: 5;
}
.grip:hover,
.grip.active {
  background: color-mix(in srgb, var(--indigo) 45%, transparent);
}
.bar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 2px;
  height: 32px;
  padding: 0 6px;
  border-bottom: 1px solid var(--line);
}
.bar button {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 100%;
  padding: 0 10px;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
  text-transform: uppercase;
  letter-spacing: 0.03em;
  cursor: pointer;
}
.bar button.on {
  border-bottom-color: var(--indigo);
  color: var(--ink);
}
.bar .link,
.bar .icon {
  text-transform: none;
  letter-spacing: 0;
}
.bar .link:hover,
.bar .icon:hover {
  color: var(--ink);
}
.badge {
  min-width: 16px;
  padding: 0 4px;
  border-radius: 999px;
  background: var(--indigo);
  color: #fff;
  font-size: 10px;
  line-height: 16px;
  text-align: center;
}
.spacer {
  flex: 1;
}
.content {
  display: flex;
  flex: 1;
  min-height: 0;
}
.embedded {
  flex: 1;
  width: auto !important;
  border-left: 0 !important;
}
.logs {
  flex: 1;
  margin: 0;
  padding: 8px 12px;
  overflow: auto;
  color: var(--ink-soft);
  font: calc(12px * var(--font-scale)) / 1.5 'Cascadia Code', Consolas, monospace;
  white-space: pre-wrap;
  word-break: break-all;
}
.trace {
  flex: 1;
  margin: 0;
  padding: 4px 0;
  overflow: auto;
  list-style: none;
  font-size: calc(12.5px * var(--font-scale));
}
.trace li {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 3px 12px;
}
.trace li:hover {
  background: color-mix(in srgb, var(--ink) 4%, transparent);
}
.n {
  width: 22px;
  color: var(--ink-faint);
  font-variant-numeric: tabular-nums;
  text-align: right;
}
.st {
  flex: none;
  color: var(--thread);
}
.failed .st,
.blocked .st,
.rejected .st {
  color: var(--red);
}
.running .st,
.waiting .st {
  color: var(--indigo);
}
.spin {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
code {
  flex: none;
  color: var(--indigo);
  font-size: calc(12px * var(--font-scale));
}
.sum {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  white-space: nowrap;
  text-overflow: ellipsis;
}
.state {
  flex: none;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
}
.none {
  color: var(--ink-faint);
}
</style>
