<script setup lang="ts">
import { bridge } from '../bridge'
import { state } from '../store'

/**
 * 窗口四边和四角的透明拖拽热区。
 * 无边框窗口里 WebView2 盖住整个客户区，系统自带的边框拖拽收不到鼠标，
 * 所以在网页上放热区，按下后交给宿主走系统的调整大小流程。
 */
const edges = ['top', 'bottom', 'left', 'right', 'topLeft', 'topRight', 'bottomLeft', 'bottomRight'] as const

function start(direction: string, e: MouseEvent) {
  if (e.button !== 0) return
  e.preventDefault()
  void bridge.startResize(direction)
}
</script>

<template>
  <div v-if="!state.maximized" class="edges" aria-hidden="true">
    <i v-for="d in edges" :key="d" :class="d" @mousedown="start(d, $event)" />
  </div>
</template>

<style scoped>
.edges i {
  position: fixed;
  z-index: 100;
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
.top,
.bottom {
  left: 8px;
  right: 8px;
  height: 5px;
  cursor: ns-resize;
}
.top {
  top: 0;
}
.bottom {
  bottom: 0;
}
.left,
.right {
  top: 8px;
  bottom: 8px;
  width: 5px;
  cursor: ew-resize;
}
.left {
  left: 0;
}
.right {
  right: 0;
}
.topLeft,
.topRight,
.bottomLeft,
.bottomRight {
  width: 12px;
  height: 12px;
}
.topLeft {
  top: 0;
  left: 0;
  cursor: nwse-resize;
}
.topRight {
  top: 0;
  right: 0;
  cursor: nesw-resize;
}
.bottomLeft {
  bottom: 0;
  left: 0;
  cursor: nesw-resize;
}
.bottomRight {
  bottom: 0;
  right: 0;
  cursor: nwse-resize;
}
</style>
