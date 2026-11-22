<script setup lang="ts">
/**
 * 输入框上方的一行汇总（参照 Cursor）：后台有几个终端还在跑、最近一轮 AI 改了几个文件。
 * 点终端：编码模式下展开底部终端，否则打开右侧「终端」标签。
 * 改动这一行有「撤销 / 保留 / 查看」（Cursor 的 Undo / Keep / Review）：AI 的改动已经写进磁盘，
 * 保留 = 认可、不再提示；撤销 = 这一轮改过的文件恢复成这一轮之前的样子（先确认）；查看 = 打开对比。
 * 编码模式下上面再放一块 ContextHint（上下文用量、建议新开任务）。
 */
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, FilePen, SquareTerminal } from '@lucide/vue'
import ContextHint from './ContextHint.vue'
import TaskBoard from './TaskBoard.vue'
import { current, currentState, openChanges, state } from '../../store'
import { ide, ideActive, keepTurn, pendingTurn, undoTurn } from './store'

const { t } = useI18n()

const running = computed(() => (current.value ? (state.terminal[current.value.id] ?? []).filter((r) => r.running && r.background).length : 0))
/** 最近一轮还没保留、没撤销的改动；AI 还在做的时候先不问（做完再一起决定） */
const lastTurn = computed(() => (current.value ? pendingTurn(current.value.id) : null))
const files = computed(() => lastTurn.value?.files.filter((f) => !f.reverted).length ?? 0)
const busy = computed(() => !!currentState.value?.busy)

function undo() {
  if (!current.value || !lastTurn.value) return
  void undoTurn(
    current.value.id,
    lastTurn.value.turnId,
    (n, dirty) => t('ui.code.decide.undoConfirm', { n }) + (dirty ? '\n' + t('ui.code.decide.undoDirty', { n: dirty }) : ''),
    (n) => t('ui.code.decide.undone', { n }),
  )
}

function showTerminals() {
  if (ideActive.value) {
    ide.bottomOpen = true
    ide.bottomTab = 'terminal'
  } else {
    state.sessionPanel = true
    state.rightClosed = false
    state.rightTab = 'terminal'
  }
}
</script>

<template>
  <!-- 编码模式：上下文用量、该新开任务的提示、带过来的交接摘要 -->
  <ContextHint v-if="ideActive" />
  <!-- 编码模式：任务进度看板（计划步骤 + 这一轮开出去的子代理），不用切回普通模式看「计划」面板 -->
  <TaskBoard v-if="ideActive" />
  <div v-if="current && (running || files)" class="run-summary">
    <button v-if="running" type="button" @click="showTerminals">
      <ChevronRight :size="13" class="chev" />
      <SquareTerminal :size="13" />
      {{ t('ui.code.backgroundTerminals', { n: running }) }}
    </button>
    <div v-if="files && lastTurn && !busy" class="changes-row">
      <button type="button" class="label" @click="openChanges(current.id, lastTurn.turnId)">
        <ChevronRight :size="13" class="chev" />
        <FilePen :size="13" />
        {{ t('ui.code.filesChanged', { n: files }) }}
      </button>
      <button type="button" class="act" :title="t('ui.code.decide.undoHint')" @click="undo">{{ t('ui.code.decide.undo') }}</button>
      <button type="button" class="act" :title="t('ui.code.decide.keepHint')" @click="keepTurn(current.id, lastTurn.turnId)">{{ t('ui.code.decide.keep') }}</button>
      <button type="button" class="act primary" @click="openChanges(current.id, lastTurn.turnId)">{{ t('ui.code.decide.review') }}</button>
    </div>
  </div>
</template>

<style scoped>
.run-summary {
  display: flex;
  flex-direction: column;
  max-width: var(--column);
  margin: 0 auto 6px;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--loom);
}
.run-summary button {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border: 0;
  background: transparent;
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
  text-align: left;
  cursor: pointer;
}

.run-summary button:hover {
  color: var(--ink);
  background: color-mix(in srgb, var(--ink) 4%, transparent);
}
.chev {
  color: var(--ink-faint);
}
.changes-row {
  display: flex;
  align-items: center;
  gap: 4px;
  padding-right: 6px;
}
.run-summary > * + * {
  border-top: 1px solid var(--line);
}
.changes-row .label {
  flex: 1;
  min-width: 0;
}
.run-summary .act {
  flex: none;
  padding: 2px 10px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
}
.run-summary .act:hover {
  border-color: var(--indigo);
}
.run-summary .act.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
</style>
