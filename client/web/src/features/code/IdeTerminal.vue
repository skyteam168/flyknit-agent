<script setup lang="ts">
/**
 * 编码模式底部的「终端」（参照 VS Code）：左边是选中的终端，右边是终端列表。
 * - 员工自己的 PowerShell：可以开好几个，直接敲命令（和自己打开 PowerShell 一样，不经过 AI）；
 * - AI 跑的命令（run_shell，包括一直在跑的后台命令，比如开发服务器）：看输出，还在跑的可以停。
 *   AI 的命令照样走命令确认，这里只是看和停。
 */
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, Plus, Square, SquareTerminal, X } from '@lucide/vue'
import { current, state, stopTerminalRun } from '../../store'
import type { TerminalRun } from '../../types'
import XTermView from './XTermView.vue'
import { closePty, openPty, pty } from './pty'

const { t } = useI18n()

const mine = computed(() => pty.sessions.filter((s) => s.conversationId === current.value?.id))
const runs = computed<TerminalRun[]>(() => (current.value ? [...(state.terminal[current.value.id] ?? [])].reverse() : []))

/** 选中的：pty:<id> 或 ai:<runId> */
const selected = ref('')
const selectedPty = computed(() => (selected.value.startsWith('pty:') ? mine.value.find((s) => 'pty:' + s.id === selected.value) ?? null : null))
const selectedRun = computed(() => (selected.value.startsWith('ai:') ? runs.value.find((r) => 'ai:' + r.id === selected.value) ?? null : null))

// 没选或者选中的没了：先选还在跑的 AI 命令，再选自己的终端
watch(
  () => [mine.value.map((s) => s.id).join(), runs.value.map((r) => r.id + r.running).join()],
  () => {
    if (selectedPty.value || selectedRun.value) return
    const running = runs.value.find((r) => r.running)
    selected.value = running ? 'ai:' + running.id : mine.value[0] ? 'pty:' + mine.value[0].id : runs.value[0] ? 'ai:' + runs.value[0].id : ''
  },
  { immediate: true },
)

const opening = ref(false)
async function newShell() {
  if (!current.value || opening.value) return
  opening.value = true
  const s = await openPty(current.value.id, 120, 30)
  opening.value = false
  if (s) selected.value = 'pty:' + s.id
}

const base = (p: string) => p.split(/[\\/]/).filter(Boolean).pop() ?? p

function runStatus(r: TerminalRun) {
  if (r.running) return t(r.background ? 'ui.terminal.background' : 'ui.terminal.running')
  if (r.stopped) return t('ui.terminal.stopped')
  return t('ui.terminal.exit', { code: r.exitCode ?? '?' })
}
</script>

<template>
  <div class="ide-term">
    <div class="screen">
      <XTermView v-if="selectedPty && !selectedPty.exited" :id="selectedPty.id" :key="selectedPty.id" />
      <div v-else-if="selectedPty" class="ended">{{ t('ui.code.term.exited', { code: selectedPty.exitCode ?? '?' }) }}</div>
      <div v-else-if="selectedRun" class="run">
        <header>
          <Bot :size="13" />
          <code>{{ selectedRun.command }}</code>
          <span class="muted">{{ selectedRun.cwd }}</span>
          <span class="state" :class="{ on: selectedRun.running }">{{ runStatus(selectedRun) }}</span>
          <button v-if="selectedRun.running" type="button" class="stop" @click="stopTerminalRun(current!.id, selectedRun.id)"><Square :size="11" /> {{ t('ui.terminal.stop') }}</button>
        </header>
        <!-- 在伪控制台里跑的（Windows 上的后台命令）：带颜色，员工能直接和它交互 -->
        <XTermView
          v-if="selectedRun.pty"
          :key="'run' + selectedRun.id"
          :run="{ conversationId: current!.id, runId: selectedRun.id, seed: selectedRun.raw, ended: !selectedRun.running }"
        />
        <pre v-else>{{ selectedRun.output || t('ui.code.term.noOutput') }}</pre>
      </div>
      <div v-else class="empty">
        <SquareTerminal :size="26" />
        <p>{{ t('ui.code.term.emptyHint') }}</p>
        <button type="button" class="primary" :disabled="opening" @click="newShell"><Plus :size="13" /> {{ t('ui.code.term.newShell') }}</button>
      </div>
    </div>
    <aside class="list">
      <header>
        <span>{{ t('ui.code.term.list') }}</span>
        <button type="button" class="icon" :title="t('ui.code.term.newShell')" :disabled="opening" @click="newShell"><Plus :size="14" /></button>
      </header>
      <button v-for="s in mine" :key="s.id" type="button" class="item" :class="{ on: selected === 'pty:' + s.id, ended: s.exited }" @click="selected = 'pty:' + s.id">
        <SquareTerminal :size="13" class="lead" />
        <span class="label">{{ s.title }}</span>
        <span class="sub">{{ base(s.cwd) }}</span>
        <span class="x" role="button" :title="t('ui.code.close')" @click.stop="closePty(s.id)"><X :size="12" /></span>
      </button>
      <button v-for="r in runs" :key="r.id" type="button" class="item ai" :class="{ on: selected === 'ai:' + r.id }" :title="r.command" @click="selected = 'ai:' + r.id">
        <span class="lead loop">∞</span>
        <span class="label">AI ({{ r.command }})</span>
        <span class="sub">{{ base(r.cwd) }}</span>
        <span v-if="r.running" class="dot" :title="runStatus(r)" />
      </button>
      <p v-if="!mine.length && !runs.length" class="muted pad">{{ t('ui.code.term.none') }}</p>
    </aside>
  </div>
</template>

<style scoped>
.ide-term {
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
}
.screen {
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
  background: var(--cloth);
}
.list {
  display: flex;
  flex: none;
  flex-direction: column;
  width: 260px;
  overflow-y: auto;
  border-left: 1px solid var(--line);
  background: var(--loom);
}
.list header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 4px 6px 4px 10px;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.icon {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.icon:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.item {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 4px 8px 4px 10px;
  border: 0;
  border-left: 2px solid transparent;
  background: transparent;
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
  text-align: left;
  cursor: pointer;
}
.item:hover {
  background: color-mix(in srgb, var(--ink) 5%, transparent);
}
.item.on {
  border-left-color: var(--red);
  background: color-mix(in srgb, var(--ink) 7%, transparent);
}
.item.ended .label {
  color: var(--ink-faint);
  text-decoration: line-through;
}
.lead {
  flex: none;
  color: var(--ink-soft);
}
.loop {
  width: 13px;
  font-size: 15px;
  line-height: 1;
  text-align: center;
}
.label {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.sub {
  flex: none;
  max-width: 90px;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
  white-space: nowrap;
  text-overflow: ellipsis;
}
.x {
  display: grid;
  place-items: center;
  margin-left: auto;
  width: 18px;
  height: 18px;
  border-radius: 4px;
  opacity: 0;
}
.item:hover .x {
  opacity: 0.7;
}
.x:hover {
  opacity: 1 !important;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
}
.dot {
  flex: none;
  margin-left: auto;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--thread);
}
.run {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}
.run header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 12px;
  border-bottom: 1px solid var(--line);
  color: var(--ink-soft);
  font-size: calc(12px * var(--font-scale));
}
.run header code {
  min-width: 0;
  overflow: hidden;
  color: var(--ink);
  white-space: nowrap;
  text-overflow: ellipsis;
}
.state {
  margin-left: auto;
  color: var(--ink-faint);
  white-space: nowrap;
}
.state.on {
  color: var(--thread);
}
.stop {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--red);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.run pre {
  flex: 1;
  margin: 0;
  padding: 8px 12px;
  overflow: auto;
  color: var(--ink);
  font: calc(12.5px * var(--font-scale)) / 1.5 'Cascadia Mono', Consolas, monospace;
  white-space: pre-wrap;
  word-break: break-all;
}
.empty,
.ended {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--ink-faint);
  font-size: calc(12.5px * var(--font-scale));
}
.empty p {
  margin: 0;
}
.primary {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 12px;
  border: 0;
  border-radius: 6px;
  background: var(--indigo);
  color: #fff;
  cursor: pointer;
}
.muted {
  color: var(--ink-faint);
}
.pad {
  margin: 6px 10px;
  font-size: calc(12px * var(--font-scale));
}
</style>
