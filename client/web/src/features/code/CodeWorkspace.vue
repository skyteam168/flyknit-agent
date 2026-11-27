<script setup lang="ts">
/**
 * 编码模式（AI IDE）的左边和中间：项目 / 技能 / MCP / 知识 + Monaco + 终端 / 日志 / Agent Trace。
 * 右边的 AI Agent 就是这个对话（App 里原来的对话区，编码模式下放窄、放在最右边），不另做一份。
 * 右边缘的竖条拖动调对话区的宽度。
 */
import { computed, ref } from 'vue'
import PreviewPane from '../../components/PreviewPane.vue'
import { closePreview, current, state } from '../../store'
import BottomPanel from './BottomPanel.vue'
import EditorArea from './EditorArea.vue'
import ExplorerPane from './ExplorerPane.vue'
import { clampAgent, ide, inWorkspace, openInEditor } from './store'

const emit = defineEmits<{ useSkill: [name: string]; useText: [text: string]; insertText: [text: string] }>()

// 对话里点「预览」：编码模式没有普通界面的右侧分屏，预览放在编辑器右边（对话区左边）。
// 工作区里的文本文件多一个「在编辑器里打开」，打开后预览收起
const editable = computed(() => !!current.value && !!state.preview && inWorkspace(current.value.id, state.preview.file.path))
async function editFile(path: string) {
  if (current.value && (await openInEditor(current.value.id, path))) closePreview()
}

const dragging = ref(false)
let startX = 0
let startW = 0
function down(e: PointerEvent) {
  dragging.value = true
  startX = e.clientX
  startW = ide.agentWidth
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  e.preventDefault()
}
function move(e: PointerEvent) {
  if (dragging.value) ide.agentWidth = clampAgent(startW + startX - e.clientX)
}
function up(e: PointerEvent) {
  dragging.value = false
  ;(e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId)
}
</script>

<template>
  <div class="ide" :class="{ dragging }">
    <ExplorerPane @use-skill="(n) => emit('useSkill', n)" @use-text="(x) => emit('useText', x)" />
    <div class="center">
      <EditorArea @insert-text="(x) => emit('insertText', x)" />
      <BottomPanel />
    </div>
    <PreviewPane v-if="state.preview" class="ide-preview" ide :open-in-editor="editable ? editFile : undefined" />
    <div
      class="agent-grip"
      :class="{ active: dragging }"
      role="separator"
      aria-orientation="vertical"
      @pointerdown="down"
      @pointermove="move"
      @pointerup="up"
      @pointercancel="up"
    />
  </div>
</template>

<style scoped>
.ide {
  position: relative;
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
}
/* 拖动时编辑器别抢鼠标 */
.ide.dragging :deep(.monaco-host) {
  pointer-events: none;
}
.center {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}
.ide-preview {
  max-width: 60%;
}
.agent-grip {
  flex: none;
  width: 5px;
  margin-right: -3px;
  border-right: 1px solid var(--line);
  cursor: col-resize;
  z-index: 5;
}
.agent-grip:hover,
.agent-grip.active {
  background: color-mix(in srgb, var(--indigo) 45%, transparent);
}
</style>
