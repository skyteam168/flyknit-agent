<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'

// 轻量弹出层：点击外部或按 Esc 关闭，默认向上展开（输入框在页面底部）
const props = withDefaults(defineProps<{ placement?: 'up' | 'down'; align?: 'start' | 'end'; width?: number }>(), {
  placement: 'up',
  align: 'start',
  width: 280,
})
const open = ref(false)
const root = ref<HTMLElement>()

function onDoc(e: MouseEvent) {
  if (root.value && !root.value.contains(e.target as Node)) open.value = false
}
function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') open.value = false
}
watch(open, (v) => {
  if (v) {
    document.addEventListener('mousedown', onDoc)
    document.addEventListener('keydown', onKey)
  } else {
    document.removeEventListener('mousedown', onDoc)
    document.removeEventListener('keydown', onKey)
  }
})
onBeforeUnmount(() => (open.value = false))

const close = () => (open.value = false)
defineExpose({ close })
</script>

<template>
  <div ref="root" class="pop-root">
    <slot name="trigger" :open="open" :toggle="() => (open = !open)" />
    <div
      v-if="open"
      class="panel"
      :class="[props.placement, props.align]"
      :style="{ width: `${props.width}px` }"
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
