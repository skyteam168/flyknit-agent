<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Square, SquareTerminal, Trash2 } from '@lucide/vue'
import { clearTerminal, current, state, stopTerminalRun } from '../store'
import type { TerminalRun } from '../types'

/**
 * 右侧「终端」标签：AI 执行的命令和输出（实时），后台命令（开发服务器之类）一直跑，员工可以随时停。
 * 输出只是看：员工自己要敲命令，在对话里告诉 AI。
 */
const { t } = useI18n()
const box = ref<HTMLElement>()

const runs = computed<TerminalRun[]>(() => (current.value ? state.terminal[current.value.id] ?? [] : []))
const ended = computed(() => runs.value.some((r) => !r.running))

// 有新输出时，如果本来就停在底部，跟着滚到底；员工往上翻着看的时候不打扰
let stick = true
function onScroll() {
  const el = box.value
  if (el) stick = el.scrollHeight - el.scrollTop - el.clientHeight < 40
}
watch(
  () => runs.value.map((r) => `${r.id}:${r.output?.length ?? 0}:${r.running}`).join(),
  async () => {
    if (!stick) return
    await nextTick()
    box.value?.scrollTo({ top: box.value.scrollHeight })
  },
  { immediate: true },
)

function status(r: TerminalRun) {
  if (r.running) return { text: t(r.background ? 'ui.terminal.background' : 'ui.terminal.running'), cls: 'run' }
  if (r.stopped) return { text: t('ui.terminal.stopped'), cls: 'stop' }
  return { text: t('ui.terminal.exit', { code: r.exitCode ?? '?' }), cls: r.exitCode === 0 ? 'ok' : 'bad' }
}

function when(at: string) {
  const d = new Date(at)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}
</script>

<template>
  <aside class="terminal">
    <div class="head">
      <SquareTerminal :size="15" />
      <strong>{{ t('ui.terminal.title') }}</strong>
      <span class="spacer" />
      <button v-if="ended && current" type="button" class="icon" :title="t('ui.terminal.clear')" @click="clearTerminal(current.id)">
        <Trash2 :size="14" />
      </button>
    </div>
    <div ref="box" class="screen" @scroll="onScroll">
      <p v-if="!runs.length" class="empty">{{ t('ui.terminal.empty') }}</p>
      <section v-for="r in runs" :key="r.id" class="run" :class="{ live: r.running }">
        <div class="cmd" :title="r.cwd">
          <span class="prompt">{{ r.shell === 'cmd' ? '>' : 'PS>' }}</span>
          <span class="text">{{ r.command }}</span>
        </div>
        <div class="meta">
          <span class="id">{{ r.id }}</span>
          <span class="badge" :class="status(r).cls">{{ status(r).text }}</span>
          <span class="time">{{ when(r.startedAt) }}</span>
          <span class="spacer" />
          <button v-if="r.running" type="button" class="stop" :title="t('ui.terminal.stopHint')" @click="stopTerminalRun(r.conversationId, r.id)">
            <Square :size="10" fill="currentColor" /> {{ t('ui.terminal.stop') }}
          </button>
        </div>
        <pre v-if="r.output">{{ r.output }}</pre>
      </section>
    </div>
    <p class="hint">{{ t('ui.terminal.hint') }}</p>
  </aside>
</template>

<style scoped>
.terminal {
  display: flex;
  flex-direction: column;
  width: 380px;
  border-left: 1px solid var(--line);
  background: var(--loom);
  font-size: var(--t-sm);
}
.head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 10px 12px 6px;
}
.spacer {
  flex: 1;
}
.icon {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-soft);
}
.icon:hover {
  background: var(--cloth-sunk);
}
/* 终端画面两种主题下都用深底浅字，和命令行习惯一致 */
.screen {
  flex: 1;
  min-height: 0;
  margin: 0 10px;
  padding: 8px 10px;
  overflow-y: auto;
  border-radius: var(--r-sm);
  background: #16181f;
  color: #d7dae3;
  font-family: ui-monospace, 'Cascadia Mono', Consolas, 'Courier New', monospace;
  font-size: 12px;
  line-height: 1.5;
}
.empty {
  margin: 4px 0;
  color: #8a90a2;
  font-family: inherit;
}
.run {
  padding: 6px 0;
  border-bottom: 1px dashed #2c303b;
}
.run:last-child {
  border-bottom: none;
}
.cmd {
  display: flex;
  gap: 6px;
  word-break: break-all;
}
.prompt {
  flex: none;
  color: #7ea7ff;
}
.text {
  color: #f2f3f7;
  white-space: pre-wrap;
}
.meta {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 2px 0 4px;
  color: #8a90a2;
  font-size: 11px;
}
.badge {
  padding: 0 6px;
  border-radius: 999px;
  background: #2a2e39;
}
.badge.run {
  background: #1f3b6e;
  color: #b9d0ff;
}
.badge.ok {
  color: #8fd19e;
}
.badge.bad {
  color: #f2a65a;
}
.live .badge.run::before {
  content: '';
  display: inline-block;
  width: 6px;
  height: 6px;
  margin-right: 4px;
  border-radius: 50%;
  background: #7ea7ff;
  vertical-align: 1px;
  animation: pulse 1.2s infinite;
}
.stop {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 8px;
  border: 1px solid #4a3030;
  border-radius: 6px;
  color: #ff9b9b;
  font-size: 11px;
}
.stop:hover {
  background: #3a2020;
}
pre {
  max-height: 320px;
  margin: 0;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-all;
  font: inherit;
}
.hint {
  margin: 6px 12px 10px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
@keyframes pulse {
  50% {
    opacity: 0.3;
  }
}
</style>
