<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { ExternalLink, X } from '@lucide/vue'
import { asOutputFile, launchFile, state } from '../store'

/**
 * 大图查看器：首页输入框里还没发出去的图片、窄窗口（没有右侧分屏）时点图片附件，在这里看原图。
 * 点空白处或按 Esc 关闭（Esc 在 App.vue 的 chat.stop 里处理）。
 */
const { t } = useI18n()

function close() {
  state.imageView = null
}

function openOutside() {
  const v = state.imageView
  if (v?.path) void launchFile(asOutputFile({ path: v.path, name: v.name, exists: true }))
}
</script>

<template>
  <div v-if="state.imageView" class="viewer" role="dialog" aria-modal="true" :aria-label="state.imageView.name" @mousedown.self="close">
    <div class="bar">
      <span class="name" :title="state.imageView.path || state.imageView.name">{{ state.imageView.name }}</span>
      <button v-if="state.imageView.path" type="button" class="btn" @click="openOutside">
        <ExternalLink :size="15" /> {{ t('ui.imageView.openOutside') }}
      </button>
      <button type="button" class="btn icon" :aria-label="t('settings.close')" @click="close"><X :size="18" /></button>
    </div>
    <img :src="state.imageView.src" :alt="state.imageView.name" @mousedown.self="close" />
  </div>
</template>

<style scoped>
.viewer {
  position: fixed;
  inset: 0;
  z-index: 80;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  padding: 56px 24px 24px;
  background: color-mix(in srgb, #000 78%, transparent);
  animation: fade 120ms ease-out;
}
.bar {
  position: absolute;
  top: 12px;
  left: 16px;
  right: 16px;
  display: flex;
  align-items: center;
  gap: 8px;
  color: #fff;
}
.name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: var(--t-sm);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border-radius: 8px;
  background: rgb(255 255 255 / 12%);
  color: #fff;
  font-size: var(--t-sm);
}
.btn:hover {
  background: rgb(255 255 255 / 22%);
}
.btn.icon {
  padding: 6px;
}
img {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
  border-radius: 6px;
  box-shadow: 0 8px 40px rgb(0 0 0 / 45%);
}
@keyframes fade {
  from {
    opacity: 0;
  }
}
</style>
