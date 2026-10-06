<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RotateCcw, Search } from '@lucide/vue'
import { bridge } from '../bridge'
import { state, toast } from '../store'
import { describeEvent, setBindings, setCapturing } from '../shortcuts'
import type { ShortcutInfo } from '../types'

// 快捷键设置：命令表来自宿主，这里只负责显示、录制新按键、以及把结果写回去。
const { t, te } = useI18n()

const query = ref('')
const recording = ref<string | null>(null)
const pending = ref('')

const list = computed(() => {
  const q = query.value.trim().toLowerCase()
  if (!q) return state.shortcuts
  return state.shortcuts.filter(
    (c) => label(c).toLowerCase().includes(q) || c.binding.toLowerCase().includes(q) || c.id.includes(q),
  )
})

function label(c: ShortcutInfo) {
  // vue-i18n 把 key 里的点当层级，所以命令 id 的点在语言包里写成下划线
  const key = `ui.shortcuts.cmd.${c.id.replace(/\./g, '_')}`
  return te(key) ? t(key) : c.id
}

/** 把 "Ctrl+Shift+B" 拆成一个个按键，界面上一键一个小方块 */
function keys(binding: string): string[] {
  return binding ? binding.split('+') : []
}

function start(c: ShortcutInfo) {
  if (c.fixed) return
  recording.value = c.id
  pending.value = ''
  setCapturing(true)
  window.addEventListener('keydown', onCapture, { capture: true })
}

function stop() {
  recording.value = null
  pending.value = ''
  setCapturing(false)
  window.removeEventListener('keydown', onCapture, { capture: true })
}

function onCapture(e: KeyboardEvent) {
  e.preventDefault()
  e.stopPropagation()
  if (e.key === 'Escape') {
    stop()
    return
  }
  const described = describeEvent(e)
  if (!described) return // 只按住修饰键，等真正的那个键
  pending.value = described
  void save(recording.value!, described)
}

async function save(id: string, binding: string) {
  const r = await bridge.setShortcut(id, binding).catch(() => ({ ok: false, reason: 'error' }) as const)
  if (!r.ok) {
    const conflict = 'conflictsWith' in r && r.conflictsWith ? labelOf(r.conflictsWith) : ''
    toast(t(`ui.shortcuts.reject.${r.reason}`, { cmd: conflict }))
    pending.value = ''
    return
  }
  if ('shortcuts' in r && r.shortcuts) apply(r.shortcuts)
  stop()
}

function labelOf(id: string) {
  const c = state.shortcuts.find((x) => x.id === id)
  return c ? label(c) : id
}

function apply(next: ShortcutInfo[]) {
  state.shortcuts = next
  setBindings(next)
}

async function resetOne(c: ShortcutInfo) {
  apply(await bridge.resetShortcuts(c.id).catch(() => state.shortcuts))
}

async function resetAll() {
  apply(await bridge.resetShortcuts().catch(() => state.shortcuts))
  toast(t('ui.shortcuts.resetDone'))
}

onBeforeUnmount(stop)
</script>

<template>
  <div class="panel">
    <header>
      <h2>{{ t('ui.shortcuts.title') }}</h2>
      <span class="count">{{ t('ui.shortcuts.count', { n: state.shortcuts.length }) }}</span>
    </header>

    <div class="tools">
      <label class="search">
        <Search :size="15" />
        <input v-model="query" type="search" :placeholder="t('ui.shortcuts.search')" />
      </label>
      <button type="button" class="btn" @click="resetAll">
        <RotateCcw :size="14" /> {{ t('ui.shortcuts.resetAll') }}
      </button>
    </div>

    <div class="table">
      <div class="head">
        <span>{{ t('ui.shortcuts.command') }}</span>
        <span>{{ t('ui.shortcuts.binding') }}</span>
        <span>{{ t('ui.shortcuts.action') }}</span>
      </div>
      <div v-for="c in list" :key="c.id" class="row" :class="{ recording: recording === c.id }">
        <span class="cmd">
          {{ label(c) }}
          <em v-if="c.global" class="badge">{{ t('ui.shortcuts.global') }}</em>
          <em v-if="c.fixed" class="badge muted">{{ t('ui.shortcuts.fixed') }}</em>
        </span>

        <button
          type="button"
          class="binding"
          :disabled="c.fixed"
          :title="c.fixed ? t('ui.shortcuts.fixedHint') : t('ui.shortcuts.clickToRecord')"
          @click="recording === c.id ? stop() : start(c)"
        >
          <template v-if="recording === c.id">
            <span class="hint">{{ pending || t('ui.shortcuts.pressKeys') }}</span>
          </template>
          <template v-else-if="c.binding">
            <kbd v-for="k in keys(c.binding)" :key="k">{{ k }}</kbd>
          </template>
          <span v-else class="hint">{{ t('ui.shortcuts.disabled') }}</span>
        </button>

        <span class="act">
          <button
            v-if="c.customized"
            type="button"
            class="mini"
            :title="t('ui.shortcuts.reset')"
            @click="resetOne(c)"
          >
            <RotateCcw :size="14" />
          </button>
          <span v-else class="dash">—</span>
        </span>
      </div>
      <p v-if="list.length === 0" class="empty">{{ t('ui.shortcuts.noMatch') }}</p>
    </div>

    <p class="hint foot">{{ t('ui.shortcuts.footer') }}</p>
  </div>
</template>

<style scoped>
header {
  display: flex;
  gap: 10px;
  align-items: baseline;
  margin-bottom: 12px;
}
header h2 {
  margin: 0;
  font-size: var(--t-lg, calc(16px * var(--font-scale)));
  font-weight: 600;
}
.count {
  flex: 1;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.tools {
  display: flex;
  gap: 10px;
  margin-bottom: 12px;
}
.search {
  display: flex;
  flex: 1;
  gap: 8px;
  align-items: center;
  padding: 7px 12px;
  border: 1px solid var(--line);
  border-radius: 999px;
  color: var(--ink-faint);
}
.search input {
  flex: 1;
  border: 0;
  background: transparent;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
  outline: none;
}
.table {
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  overflow: hidden;
}
.head,
.row {
  display: grid;
  grid-template-columns: 1fr 230px 60px;
  gap: 10px;
  align-items: center;
  padding: 9px 12px;
}
.head {
  background: var(--cloth-sunk, var(--cloth));
  border-bottom: 1px solid var(--line);
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.row {
  border-bottom: 1px solid color-mix(in srgb, var(--line) 60%, transparent);
}
.row:last-of-type {
  border-bottom: 0;
}
.row:hover {
  background: color-mix(in srgb, var(--ink) 4%, transparent);
}
.row.recording {
  background: var(--indigo-wash, color-mix(in srgb, var(--indigo) 10%, transparent));
}
.cmd {
  font-size: var(--t-sm);
}
.badge {
  margin-left: 6px;
  padding: 1px 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--indigo) 14%, transparent);
  color: var(--indigo);
  font-size: calc(11px * var(--font-scale));
  font-style: normal;
}
.badge.muted {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
  color: var(--ink-faint);
}
.binding {
  display: flex;
  gap: 4px;
  align-items: center;
  min-height: 30px;
  padding: 3px 8px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  cursor: pointer;
}
.binding:hover:not(:disabled) {
  border-color: var(--line-strong, var(--line));
}
.binding:disabled {
  cursor: default;
}
kbd {
  padding: 2px 7px;
  border: 1px solid var(--line-strong, var(--line));
  border-bottom-width: 2px;
  border-radius: 5px;
  background: var(--cloth-sunk, var(--cloth));
  color: var(--ink);
  font-family: inherit;
  font-size: calc(11.5px * var(--font-scale));
}
.hint {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.foot {
  margin: 12px 0 0;
}
.act {
  text-align: center;
}
.mini {
  display: inline-grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--ink-faint);
  cursor: pointer;
}
.mini:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
  color: var(--ink);
}
.dash {
  color: var(--ink-faint);
}
.empty {
  padding: 24px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
  text-align: center;
}
</style>
