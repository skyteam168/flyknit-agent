<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'

// 轻量弹出层：点击外部或按 Esc 关闭，默认向上展开（输入框在页面底部）
const props = withDefaults(defineProps<{ placement?: 'up' | 'down'; align?: 'start' | 'end'; width?: number }>(), {
  placement: 'up',
  align: 'start',
  width: 280,
})
const emit = defineEmits<{ change: [open: boolean] }>()
const open = ref(false)
const root = ref<HTMLElement>()
const panel = ref<HTMLElement>()
// 面板超出窗口左右边缘时往回挪（输入框很窄、按钮收进「…」后靠近右边时会出现）
const shift = ref(0)
function fit() {
  shift.value = 0
  void nextTick(() => {
    const rect = panel.value?.getBoundingClientRect()
    if (!rect) return
    const margin = 8
    if (rect.right > window.innerWidth - margin) shift.value = window.innerWidth - margin - rect.right
    if (rect.left + shift.value < margin) shift.value = margin - rect.left
  })
}

function onDoc(e: MouseEvent) {
  if (root.value && !root.value.contains(e.target as Node)) open.value = false
}
function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') open.value = false
}
watch(open, (v) => {
  emit('change', v)
  if (v) {
    fit()
    document.addEventListener('mousedown', onDoc)
    document.addEventListener('keydown', onKey)
  } else {
    document.removeEventListener('mousedown', onDoc)
    document.removeEventListener('keydown', onKey)
  }
})
onBeforeUnmount(() => (open.value = false))

const close = () => (open.value = false)
const show = () => (open.value = true)
defineExpose({ close, show })
</script>

<template>
  <div ref="root" class="pop-root">
    <slot name="trigger" :open="open" :toggle="() => (open = !open)" />
    <div
      v-if="open"
      ref="panel"
      class="panel"
      :class="[props.placement, props.align]"
      :style="{ width: `${props.width}px`, maxWidth: 'calc(100vw - 16px)', translate: shift ? `${shift}px 0` : undefined }"
      role="dialog"
    >
      <slot :close="close" />
    </div>
  </div>
</template>

<style scoped>
.pop-root {
  position: relative;
  display: inline-flex;
}
.panel {
  position: absolute;
  z-index: 50;
  max-height: 360px;
  overflow-y: auto;
  padding: 6px;
  border-radius: var(--r-md);
  border: 1px solid var(--line);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  animation: pop 140ms ease-out;
}
.panel.up {
  bottom: calc(100% + 8px);
}
.panel.down {
  top: calc(100% + 8px);
}
.panel.start {
  left: 0;
}
.panel.end {
  right: 0;
}
@keyframes pop {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
}
</style>
