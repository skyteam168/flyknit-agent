<script setup lang="ts">
import type { Component } from 'vue'
import { Bottom, Top } from '@element-plus/icons-vue'

defineProps<{
  title: string
  value: string
  unit?: string
  icon: Component
  tone: 'indigo' | 'thread' | 'amber' | 'violet'
  /** 环比百分比；null 表示没法比 */
  delta?: number | null
  deltaLabel?: string
  foot?: string
}>()
</script>

<template>
  <div class="stat panel">
    <div class="top">
      <span class="title">{{ title }}</span>
      <span class="icon" :class="tone"><el-icon :size="18"><component :is="icon" /></el-icon></span>
    </div>
    <div class="value">
      {{ value }}<small v-if="unit">{{ unit }}</small>
    </div>
    <div class="foot">
      <template v-if="delta !== undefined">
        <span v-if="delta === null" class="muted">{{ deltaLabel ?? '较昨日' }} —</span>
        <span v-else class="delta" :class="delta >= 0 ? 'up' : 'down'">
          <el-icon><Top v-if="delta >= 0" /><Bottom v-else /></el-icon>{{ Math.abs(delta) }}%
          <span class="muted">{{ deltaLabel ?? '较昨日' }}</span>
        </span>
      </template>
      <span v-if="foot" class="muted">{{ foot }}</span>
    </div>
  </div>
</template>

<style scoped>
.stat {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}
.top {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.title {
  color: var(--ink-soft);
  font-size: 13px;
  font-weight: 500;
}
.icon {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  border-radius: 10px;
}
.icon.indigo {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.icon.thread {
  background: var(--thread-wash);
  color: var(--thread);
}
.icon.amber {
  background: var(--amber-wash);
  color: var(--amber);
}
.icon.violet {
  background: var(--violet-wash);
  color: var(--violet);
}
.value {
  font-size: 28px;
  font-weight: 600;
  line-height: 1.2;
  letter-spacing: -0.02em;
  font-variant-numeric: tabular-nums;
}
.value small {
  margin-left: 4px;
  color: var(--ink-faint);
  font-size: 13px;
  font-weight: 500;
}
.foot {
  display: flex;
  gap: 10px;
  justify-content: space-between;
  font-size: 12px;
}
.delta {
  display: inline-flex;
  gap: 2px;
  align-items: center;
  font-weight: 500;
}
.delta.up {
  color: var(--thread);
}
.delta.down {
  color: var(--red);
}
.delta .muted {
  margin-left: 4px;
  font-weight: 400;
}
</style>
