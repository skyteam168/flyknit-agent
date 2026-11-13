<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Activity, Eye, FilePen, FileText, FolderTree, Globe, Info, ListChecks, Maximize2, Minimize2, Rows2, SquareTerminal, Users, X } from '@lucide/vue'
import { closeRightPanel, current, currentState, setPreviewWidth, setRightRatio, setRightSplit, setRightTab2, state } from '../store'
import RightPaneBody from './RightPaneBody.vue'
import { backgroundCount, hasBackground } from '../background'

/**
 * 右侧面板：文档、计划、文件预览、改动的文件、AI 浏览器、终端、会话信息放在同一块地方，用标签切换。
 * 哪几个标签出现看当前有没有内容（没有计划就没有「计划」），显示哪个由 state.rightTab 决定。
 *
 * 两种展开方式（像 IDE 的面板一样）：
 * - 上下分屏：下面再开一格（state.rightTab2），比如上面看文档、下面看终端；中间的横条拖动调高度；
 * - 铺满：右侧面板占满聊天区，看长文档、大表格时用，再点一下恢复。
 * 只有一格时各标签保持自己的宽度（和以前一样）；分屏、铺满时整块面板统一宽度，左边缘拖动调宽。
 */
const { t } = useI18n()

type Tab = 'team' | 'document' | 'plan' | 'preview' | 'changes' | 'browser' | 'terminal' | 'files' | 'background' | 'session'
const icons = { team: Users, document: FileText, plan: ListChecks, preview: Eye, changes: FilePen, browser: Globe, terminal: SquareTerminal, files: FolderTree, background: Activity, session: Info }

const tabs = computed<Tab[]>(() => {
  const list: Tab[] = []
  const id = current.value?.id
  // 团队任务（docs/team-mode.md 7.5）：成员进度、共享看板
  if (currentState.value?.team) list.push('team')
  if (id && state.docs[id]) list.push('document')
  if (currentState.value?.plan.length) list.push('plan')
  if (state.preview) list.push('preview')
  if (id && state.changes[id]?.some((x) => x.files.length)) list.push('changes')
  if (state.browser.active) list.push('browser')
  if (id && state.terminal[id]?.length) list.push('terminal')
  // 文件：办事对话的工作区（只读浏览、搜索）
  if (current.value?.mode === 'agent' && current.value.workspace) list.push('files')
  // 后台：所有对话里正在进行的事（不只是当前对话）
  if (hasBackground.value) list.push('background')
  list.push('session')
  return list
})

const active = computed<Tab>(() => (tabs.value.includes(state.rightTab as Tab) ? (state.rightTab as Tab) : tabs.value[0]))

/** 分屏：至少要有两个标签才分得开 */
const split = computed(() => state.rightLayout.split && tabs.value.length > 1)
/** 下面一格能选的标签：上面正在看的除外（同一个面板不同时开两份） */
const tabs2 = computed(() => tabs.value.filter((x) => x !== active.value))
const active2 = computed<Tab>(() => (tabs2.value.includes(state.rightTab2 as Tab) ? (state.rightTab2 as Tab) : tabs2.value[0] ?? 'session'))

// 新出现计划、打开了预览时自动切过去（只在面板本来没选中有效标签时）
watch(
  () => tabs.value.join(),
  () => {
    if (!tabs.value.includes(state.rightTab as Tab)) state.rightTab = tabs.value[0]
  },
)

/** 上面一格点了下面正在显示的标签：两格对调 */
function pickTop(tab: Tab) {
  if (split.value && tab === active2.value) setRightTab2(active.value)
  state.rightTab = tab
}

function toggleSplit() {
  setRightSplit(!state.rightLayout.split)
}

// ---------- 上下两格之间的横条：拖动调高度 ----------
const panes = ref<HTMLElement>()
const draggingRow = ref(false)
function startRow(e: PointerEvent) {
  draggingRow.value = true
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  e.preventDefault()
}
function onRow(e: PointerEvent) {
  const box = panes.value?.getBoundingClientRect()
  if (draggingRow.value && box && box.height > 0) setRightRatio((e.clientY - box.top) / box.height)
}
function endRow(e: PointerEvent) {
  draggingRow.value = false
  ;(e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId)
}
function onRowKey(e: KeyboardEvent) {
  if (e.key === 'ArrowUp') setRightRatio(state.rightLayout.ratio - 0.05)
  else if (e.key === 'ArrowDown') setRightRatio(state.rightLayout.ratio + 0.05)
}

// ---------- 分屏时左边缘：拖动调整整块面板的宽度（和文件预览共用一个宽度） ----------
const draggingCol = ref(false)
function startCol(e: PointerEvent) {
  draggingCol.value = true
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  e.preventDefault()
}
function onCol(e: PointerEvent) {
  if (draggingCol.value) setPreviewWidth(window.innerWidth - e.clientX)
}
function endCol(e: PointerEvent) {
  draggingCol.value = false
  ;(e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId)
}
function onColKey(e: KeyboardEvent) {
  if (e.key === 'ArrowLeft') setPreviewWidth(state.previewWidth + 32)
  else if (e.key === 'ArrowRight') setPreviewWidth(state.previewWidth - 32)
}

/** 统一宽度：分屏时用拖出来的宽度，铺满时撑满 */
const sized = computed(() => split.value || state.rightMax)
</script>

<template>
  <div
    class="right"
    :class="{ sized, max: state.rightMax }"
    :style="split && !state.rightMax ? { width: state.previewWidth + 'px' } : undefined"
    :aria-label="t('ui.right.title')"
  >
    <div
      v-if="split && !state.rightMax"
      class="col-grip"
      :class="{ dragging: draggingCol }"
      role="separator"
      aria-orientation="vertical"
      tabindex="0"
      :aria-label="t('ui.preview.resize')"
      @pointerdown="startCol"
      @pointermove="onCol"
      @pointerup="endCol"
      @pointercancel="endCol"
      @keydown="onColKey"
    />
    <nav class="tabs" role="tablist">
      <button
        v-for="tab in tabs"
        :key="tab"
        type="button"
        role="tab"
        class="tab"
        :class="{ on: tab === active, below: split && tab === active2 }"
        :aria-selected="tab === active"
        :title="t(`ui.right.${tab}`)"
        @click="pickTop(tab)"
      >
        <component :is="icons[tab]" :size="14" />
        <span>{{ t(`ui.right.${tab}`) }}</span>
        <span v-if="tab === 'background' && backgroundCount" class="num">{{ backgroundCount }}</span>
      </button>
      <span class="spacer" />
      <button
        v-if="tabs.length > 1"
        type="button"
        class="act"
        :class="{ on: split }"
        :title="t(split ? 'ui.right.unsplit' : 'ui.right.split')"
        :aria-pressed="split"
        @click="toggleSplit"
      >
        <Rows2 :size="14" />
      </button>
      <button
        type="button"
        class="act"
        :class="{ on: state.rightMax }"
        :title="t(state.rightMax ? 'ui.right.restore' : 'ui.right.maximize')"
        :aria-pressed="state.rightMax"
        @click="state.rightMax = !state.rightMax"
      >
        <Minimize2 v-if="state.rightMax" :size="14" />
        <Maximize2 v-else :size="14" />
      </button>
      <button type="button" class="act" :title="t('ui.right.close')" :aria-label="t('ui.right.close')" @click="closeRightPanel">
        <X :size="14" />
      </button>
    </nav>

    <div v-if="!split" class="pane only">
      <RightPaneBody :tab="active" />
    </div>
    <div v-else ref="panes" class="panes">
      <div class="pane" :style="{ flexBasis: state.rightLayout.ratio * 100 + '%' }">
        <RightPaneBody :tab="active" />
      </div>
      <div
        class="row-grip"
        :class="{ dragging: draggingRow }"
        role="separator"
        aria-orientation="horizontal"
        tabindex="0"
        :aria-label="t('ui.right.resizePanes')"
        @pointerdown="startRow"
        @pointermove="onRow"
        @pointerup="endRow"
        @pointercancel="endRow"
        @keydown="onRowKey"
      />
      <nav class="tabs sub" role="tablist" :aria-label="t('ui.right.lowerPane')">
        <button
          v-for="tab in tabs2"
          :key="tab"
          type="button"
          role="tab"
          class="tab"
          :class="{ on: tab === active2 }"
          :aria-selected="tab === active2"
          :title="t(`ui.right.${tab}`)"
          @click="setRightTab2(tab)"
        >
          <component :is="icons[tab]" :size="14" />
          <span>{{ t(`ui.right.${tab}`) }}</span>
          <span v-if="tab === 'background' && backgroundCount" class="num">{{ backgroundCount }}</span>
        </button>
        <span class="spacer" />
        <button type="button" class="act" :title="t('ui.right.closePane')" @click="setRightSplit(false)">
          <X :size="14" />
        </button>
      </nav>
      <div class="pane" :style="{ flexBasis: (1 - state.rightLayout.ratio) * 100 + '%' }">
        <RightPaneBody :tab="active2" />
      </div>
    </div>
  </div>
</template>

<style scoped>
.right {
  position: relative;
  display: flex;
  flex: none;
  flex-direction: column;
  min-height: 0;
  max-width: 70vw;
}
.right.sized {
  min-width: 320px;
  border-left: 1px solid var(--line);
  background: var(--cloth);
}
.right.max {
  flex: 1;
  max-width: none;
}
.tabs {
  display: flex;
  flex: none;
  align-items: center;
  gap: 2px;
  padding: 6px 6px 0;
  border-left: 1px solid var(--line);
  border-bottom: 1px solid var(--line);
  background: var(--loom);
  overflow-x: auto;
}
.sized .tabs {
  border-left: 0;
}
.tabs.sub {
  padding-top: 4px;
}
.tab {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  padding: 5px 9px 7px;
  border-bottom: 2px solid transparent;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.tab:hover {
  color: var(--ink-soft);
}
.tab.on {
  border-bottom-color: var(--indigo);
  color: var(--ink);
}
/* 在下面一格里显示着的标签：淡淡标一下 */
.tab.below {
  border-bottom-color: var(--line-strong, var(--line));
}
.num {
  padding: 0 5px;
  border-radius: 999px;
  background: var(--indigo);
  color: #fff;
  font-size: 10px;
  line-height: 15px;
}
.spacer {
  flex: 1;
}
.act {
  display: grid;
  flex: none;
  place-items: center;
  width: 24px;
  height: 24px;
  margin-bottom: 4px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.act:hover,
.act.on {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.pane {
  display: flex;
  min-height: 0;
}
.pane.only {
  flex: 1;
}
.panes {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}
.panes .pane {
  flex-grow: 1;
  flex-shrink: 1;
  overflow: hidden;
}
/* 分屏、铺满时各标签不再用自己的宽度，统一撑满面板；它们自己的拖拽条交给面板 */
.sized :deep(.body) {
  width: auto !important;
  max-width: none !important;
  min-width: 0 !important;
  border-left: 0 !important;
}
.sized :deep(.grip) {
  display: none;
}
.col-grip {
  position: absolute;
  top: 0;
  bottom: 0;
  left: -3px;
  z-index: 3;
  width: 6px;
  cursor: col-resize;
}
.row-grip {
  flex: none;
  height: 6px;
  margin: -3px 0;
  z-index: 2;
  cursor: row-resize;
}
.col-grip:hover,
.col-grip.dragging,
.row-grip:hover,
.row-grip.dragging,
.col-grip:focus-visible,
.row-grip:focus-visible {
  background: var(--indigo);
  opacity: 0.35;
  outline: none;
}
</style>
