<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { onBeforeUnmount, onMounted } from 'vue'
import { ChevronLeft, ChevronRight, ExternalLink, X } from '@lucide/vue'
import { stepGallery } from '../imageGallery'
import { asOutputFile, launchFile, state } from '../store'

/**
 * 大图查看器：首页输入框里还没发出去的图片、窄窗口（没有右侧分屏）时点图片附件，在这里看原图。
 * 点空白处或按 Esc 关闭（Esc 在 App.vue 的 chat.stop 里处理）。
 * 一组图片（对话里 AI 看过的图，imageGallery.ts）：左右箭头、← → 键翻页，顶上显示「第 i 项，共 n 项」。
 */
const { t } = useI18n()

function close() {
  state.imageView = null
}

function onKey(e: KeyboardEvent) {
  if (!state.imageView?.items?.length) return
  if (e.key === 'ArrowLeft') stepGallery(-1)
  else if (e.key === 'ArrowRight') stepGallery(1)
  else return
  e.preventDefault()
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))

function openOutside() {
  const v = state.imageView
  if (v?.path) void launchFile(asOutputFile({ path: v.path, name: v.name, exists: true }))
}
</script>

<template>
  <div v-if="state.imageView" class="viewer" role="dialog" aria-modal="true" :aria-label="state.imageView.name" @mousedown.self="close">
    <div class="bar">
      <span class="name" :title="state.imageView.path || state.imageView.name">{{ state.imageView.name }}</span>
      <span v-if="state.imageView.items && state.imageView.items.length > 1" class="count">
        {{ t('ui.imageView.count', { i: (state.imageView.index ?? 0) + 1, n: state.imageView.items.length }) }}
      </span>
      <button v-if="state.imageView.path" type="button" class="btn" @click="openOutside">
        <ExternalLink :size="15" /> {{ t('ui.imageView.openOutside') }}
      </button>
      <button type="button" class="btn icon" :aria-label="t('settings.close')" @click="close"><X :size="18" /></button>
    </div>
    <img v-if="state.imageView.src" :src="state.imageView.src" :alt="state.imageView.name" @mousedown.self="close" />
    <div v-else class="loading">{{ t('ui.imageView.loading') }}</div>
    <template v-if="state.imageView.items && state.imageView.items.length > 1">
      <button type="button" class="nav prev" :disabled="(state.imageView.index ?? 0) === 0" :aria-label="t('ui.imageView.prev')" @click="stepGallery(-1)">
        <ChevronLeft :size="26" />
      </button>
      <button type="button" class="nav next" :disabled="(state.imageView.index ?? 0) >= state.imageView.items.length - 1" :aria-label="t('ui.imageView.next')" @click="stepGallery(1)">
        <ChevronRight :size="26" />
      </button>
    </template>
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
.count {
  flex: none;
  color: rgb(255 255 255 / 75%);
  font-size: var(--t-sm);
}
.loading {
  color: rgb(255 255 255 / 70%);
  font-size: var(--t-sm);
}
.nav {
  position: absolute;
  top: 50%;
  display: grid;
  place-items: center;
  width: 44px;
  height: 44px;
  margin-top: -22px;
  border-radius: 50%;
  background: rgb(255 255 255 / 14%);
  color: #fff;
}
.nav:hover:not(:disabled) {
  background: rgb(255 255 255 / 26%);
}
.nav:disabled {
  opacity: 0.3;
  cursor: default;
}
.prev {
  left: 20px;
}
.next {
  right: 20px;
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
