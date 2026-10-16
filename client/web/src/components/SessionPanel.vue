<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronRight, Folder, Plug, Shield, Sparkles, WandSparkles, X } from '@lucide/vue'
import { compactConversation, current, currentState, loadContextUsage, state, toast, toggleSessionPanel } from '../store'
import { convFiles, extLabel, outputFiles, uploadedFiles } from '../conversationFiles'
import { kindIcon } from '../libraryIcons'
import type { ContextPart, LibraryItem } from '../types'

/**
 * 右侧「会话信息」面板：这个对话在哪个工作区、什么权限、用的哪个模型；对话记忆（上下文）用了多少、
 * 什么时候自动整理、可以手动整理；用到的连接器；本对话的文件。
 * 员工默认看简单版（一条进度条 + 一句话）；「详细」展开每一块占多少，IT 可以关掉。
 */
const { t, te } = useI18n()

const usage = computed(() => (current.value ? state.contextUsage[current.value.id] : undefined))
const busy = computed(() => currentState.value?.busy ?? false)
const detailsAllowed = computed(() => state.app?.contextDetails !== false)
const showDetails = ref(false)
const compacting = ref(false)
const open = ref<Record<string, boolean>>({})

watch(
  () => current.value?.id,
  (id) => {
    if (id && state.contextUsage[id] === undefined) void loadContextUsage(id)
  },
  { immediate: true },
)

const colors: Record<string, string> = {
  messages: '#3b82f6',
  tool_outputs: '#8b5cf6',
  tools: '#14b8a6',
  system: '#f59e0b',
}

function label(p: ContextPart) {
  const key = p.key.startsWith('mcp:') ? '' : `ui.session.parts.${p.key}`
  return key && te(key) ? t(key) : p.label
}

function fmt(n: number) {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n >= 10_000_000 ? 0 : 1)}M`
  if (n >= 1000) return `${(n / 1000).toFixed(n >= 100_000 ? 0 : 1)}k`
  return String(n)
}

const total = computed(() => Math.max(1, usage.value?.contextLength ?? 1))
const used = computed(() => usage.value?.used ?? 0)
const percent = computed(() => Math.min(100, Math.round((used.value / total.value) * 100)))
/** 自动整理预留：门槛以上到上下文长度这一段（包括给回答留的） */
const buffer = computed(() => (usage.value ? Math.max(0, usage.value.contextLength - usage.value.compactAt) : 0))
const free = computed(() => (usage.value ? Math.max(0, usage.value.compactAt - used.value) : 0))
const nearlyFull = computed(() => !!usage.value && used.value >= usage.value.compactAt * 0.9)

/** 进度条：按块上色，宽度按占整个上下文的比例 */
const segments = computed(() =>
  (usage.value?.parts ?? [])
    .filter((p) => p.tokens > 0)
    .map((p) => ({ key: p.key, color: colors[p.key] ?? '#94a3b8', width: (p.tokens / total.value) * 100 })),
)
const rows = computed(() => [...(usage.value?.parts ?? [])].sort((a, b) => b.tokens - a.tokens))

/** 连接器：工具定义里 mcp: 开头的那几组 */
const connectors = computed(() => (usage.value?.parts.find((p) => p.key === 'tools')?.items ?? []).filter((g) => g.key.startsWith('mcp:')))

const files = computed(() => [
  ...outputFiles.value.map((i) => ({ item: i, group: t('convFiles.outputs'), list: outputFiles.value })),
  ...uploadedFiles.value.map((i) => ({ item: i, group: t('convFiles.uploads'), list: uploadedFiles.value })),
])
function viewFile(group: string, list: LibraryItem[], item: LibraryItem) {
  convFiles.viewing = { title: group, items: [...list], id: item.id }
}

async function compact() {
  if (!current.value || compacting.value) return
  if (busy.value) {
    toast(t('ui.session.compactBusy'))
    return
  }
  if (!window.confirm(t('ui.session.compactConfirm'))) return
  compacting.value = true
  try {
    toast(await compactConversation(current.value.id))
  } finally {
    compacting.value = false
  }
}
</script>

<template>
  <aside class="session" :aria-label="t('ui.session.title')">
    <header class="head">
      <strong>{{ t('ui.session.title') }}</strong>
      <button type="button" class="icon-btn" :title="t('ui.session.close')" @click="toggleSessionPanel(false)"><X :size="15" /></button>
    </header>

    <div class="scroll">
      <!-- 这个对话的环境 -->
      <section v-if="current" class="card env">
        <div v-if="current.mode === 'agent'" class="kv">
          <Folder :size="14" /><span class="k">{{ t('ui.session.workspace') }}</span>
          <span class="v" :title="current.workspace ?? ''">{{ (current.workspace ?? '').split(/[\\/]/).filter(Boolean).pop() || '—' }}</span>
        </div>
        <div v-if="current.mode === 'agent'" class="kv">
          <Shield :size="14" /><span class="k">{{ t('ui.session.permission') }}</span>
          <span class="v">{{ t(`ui.permission.${current.permission}`) }}</span>
        </div>
        <div class="kv">
          <Sparkles :size="14" /><span class="k">{{ t('ui.session.model') }}</span>
          <span class="v">{{ state.app?.modelName || '—' }}</span>
        </div>
      </section>

      <!-- 对话记忆（上下文） -->
      <section class="card">
        <div class="sec-title">
          <span>{{ t('ui.session.context') }}</span>
          <button v-if="usage && detailsAllowed" type="button" class="link" @click="showDetails = !showDetails">
            {{ showDetails ? t('ui.session.hideDetails') : t('ui.session.details') }}
            <ChevronDown :size="13" :class="{ flip: showDetails }" />
          </button>
        </div>
        <p v-if="!usage" class="muted">{{ t('ui.session.noData') }}</p>
        <template v-else>
          <div class="usage-line">
            <strong :class="{ warn: nearlyFull }">{{ t('ui.session.used', { p: percent }) }}</strong>
            <span class="muted">{{ t('ui.session.usedOf', { used: fmt(used), total: fmt(usage.contextLength) }) }}</span>
          </div>
          <div class="bar" role="img" :aria-label="t('ui.session.used', { p: percent })">
            <span v-for="sg in segments" :key="sg.key" :style="{ width: `${sg.width}%`, background: sg.color }" />
            <i class="mark" :style="{ left: `${(usage.compactAt / total) * 100}%` }" />
          </div>
          <p class="muted small">{{ nearlyFull ? t('ui.session.compactSoon') : t('ui.session.untilCompact', { n: fmt(free) }) }}</p>
          <p v-if="!showDetails" class="muted small">{{ t('ui.session.simple') }}</p>

          <!-- 详细：每一块占多少，系统提示和工具定义还能再展开 -->
          <div v-if="showDetails && detailsAllowed" class="details">
            <template v-for="p in rows" :key="p.key">
              <button type="button" class="row" :class="{ expandable: p.items.length > 0 }" @click="p.items.length && (open[p.key] = !open[p.key])">
                <i class="dot" :style="{ background: colors[p.key] ?? '#94a3b8' }" />
                <span class="name">{{ label(p) }}</span>
                <ChevronRight v-if="p.items.length" :size="12" class="chev" :class="{ flip90: open[p.key] }" />
                <span class="num">{{ fmt(p.tokens) }}</span>
                <span class="pct">{{ ((p.tokens / total) * 100).toFixed(1) }}%</span>
              </button>
              <template v-if="open[p.key]">
                <template v-for="c in p.items" :key="c.key">
                  <button type="button" class="row sub" :class="{ expandable: c.items.length > 0 }" @click="c.items.length && (open[p.key + '/' + c.key] = !open[p.key + '/' + c.key])">
                    <span class="name">{{ label(c) }}</span>
                    <ChevronRight v-if="c.items.length" :size="12" class="chev" :class="{ flip90: open[p.key + '/' + c.key] }" />
                    <span class="num">{{ fmt(c.tokens) }}</span>
                    <span class="pct">{{ ((c.tokens / total) * 100).toFixed(1) }}%</span>
                  </button>
                  <template v-if="open[p.key + '/' + c.key]">
                    <div v-for="g in c.items" :key="g.key" class="row subsub">
                      <span class="name" :title="g.key">{{ g.label }}</span>
                      <span class="num">{{ fmt(g.tokens) }}</span>
                    </div>
                  </template>
                </template>
              </template>
            </template>
            <div class="row">
              <i class="dot hollow" />
              <span class="name">{{ t('ui.session.buffer') }}</span>
              <span class="num">{{ fmt(buffer) }}</span>
              <span class="pct">{{ ((buffer / total) * 100).toFixed(1) }}%</span>
            </div>
            <div class="row">
              <i class="dot empty" />
              <span class="name">{{ t('ui.session.free') }}</span>
              <span class="num">{{ fmt(free) }}</span>
              <span class="pct">{{ ((free / total) * 100).toFixed(1) }}%</span>
            </div>
            <p class="muted small">
              {{ t('ui.session.estimated') }}
              <template v-if="usage.lastPromptTokens">{{ t('ui.session.lastActual', { n: usage.lastPromptTokens.toLocaleString() }) }}</template>
            </p>
          </div>
        </template>
        <button
          v-if="current && current.mode !== 'translate'"
          type="button"
          class="btn compact"
          :disabled="compacting || busy"
          :title="busy ? t('ui.session.compactBusy') : ''"
          @click="compact"
        >
          <WandSparkles :size="14" /> {{ compacting ? t('ui.session.compacting') : t('ui.session.compact') }}
        </button>
      </section>

      <!-- 连接器 -->
      <section v-if="current?.mode === 'agent'" class="card">
        <div class="sec-title"><span>{{ t('ui.session.connectors') }}</span></div>
        <p v-if="connectors.length === 0" class="muted small">{{ t('ui.session.noConnectors') }}</p>
        <div v-for="c in connectors" :key="c.key" class="kv">
          <Plug :size="14" /><span class="v">{{ c.label }}</span>
          <span class="muted small right">{{ t('ui.session.tools', { n: c.items.length }) }}</span>
        </div>
      </section>

      <!-- 本对话的文件 -->
      <section class="card">
        <div class="sec-title"><span>{{ t('ui.session.files') }}</span></div>
        <p v-if="files.length === 0" class="muted small">{{ t('ui.session.noFiles') }}</p>
        <button v-for="f in files" :key="f.item.id" type="button" class="file" :class="{ gone: !f.item.exists }" :title="f.item.name" @click="viewFile(f.group, f.list, f.item)">
          <component :is="kindIcon[f.item.kind]" :size="15" class="kind" />
          <span class="name">{{ f.item.name }}</span>
          <span class="muted small">{{ extLabel(f.item) }}</span>
        </button>
      </section>
    </div>
  </aside>
</template>

<style scoped>
.session {
  display: flex;
  flex: none;
  flex-direction: column;
  width: 300px;
  min-height: 0;
  border-left: 1px solid var(--line);
  background: var(--cloth-sunk);
}
.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 12px 6px;
  font-size: var(--t-sm);
}
.icon-btn {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border: 0;
  border-radius: var(--r-sm);
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.icon-btn:hover {
  background: var(--chip);
}
.scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 4px 10px 12px;
}
.card {
  margin-bottom: 10px;
  padding: 10px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  font-size: var(--t-sm);
}
.sec-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
  font-weight: 600;
}
.link {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  border: 0;
  background: none;
  color: var(--indigo);
  font: inherit;
  font-size: var(--t-xs);
  font-weight: 400;
  cursor: pointer;
}
.flip {
  transform: rotate(180deg);
}
.flip90 {
  transform: rotate(90deg);
}
.kv {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  padding: 3px 0;
  color: var(--ink-soft);
}
.kv .k {
  flex: none;
}
.kv .v {
  min-width: 0;
  overflow: hidden;
  color: var(--ink);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.env .kv .v {
  margin-left: auto;
}
.right {
  margin-left: auto;
}
.muted {
  color: var(--ink-soft);
}
.small {
  margin: 6px 0 0;
  font-size: var(--t-xs);
  line-height: 1.5;
}
.usage-line {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
}
.usage-line .warn {
  color: var(--amber);
}
.bar {
  position: relative;
  display: flex;
  height: 8px;
  margin-top: 6px;
  overflow: hidden;
  border-radius: 4px;
  background: var(--chip);
}
.bar span {
  height: 100%;
}
.bar .mark {
  position: absolute;
  top: 0;
  bottom: 0;
  width: 2px;
  background: var(--ink-faint);
}
.details {
  margin-top: 8px;
}
.row {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 3px 0;
  border: 0;
  background: none;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-xs);
  text-align: left;
}
.row.expandable {
  cursor: pointer;
}
.row.sub {
  padding-left: 16px;
  color: var(--ink-soft);
}
.row.subsub {
  padding-left: 30px;
  color: var(--ink-faint);
}
.row .name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.row .chev {
  flex: none;
  color: var(--ink-faint);
}
.row .num {
  flex: none;
  font-variant-numeric: tabular-nums;
}
.row .pct {
  flex: none;
  width: 44px;
  text-align: right;
  font-variant-numeric: tabular-nums;
  font-weight: 600;
}
.dot {
  flex: none;
  width: 9px;
  height: 9px;
  border-radius: 2px;
}
.dot.hollow {
  background: var(--ink-faint);
}
.dot.empty {
  border: 1px solid var(--line-strong);
}
.btn.compact {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-top: 10px;
}
.btn.compact:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.file {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 5px 4px;
  border: 0;
  border-radius: var(--r-sm);
  background: none;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
  text-align: left;
  cursor: pointer;
}
.file:hover {
  background: var(--chip);
}
.file.gone {
  opacity: 0.5;
}
.file .kind {
  flex: none;
  color: var(--ink-soft);
}
.file .name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
