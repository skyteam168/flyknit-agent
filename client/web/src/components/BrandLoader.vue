<script setup lang="ts">
// 统一的加载动画：标志轻轻呼吸，外圈一段弧在转，下面一行字。
// size="lg" 用在启动卡片，"md" 用在页面中间，"sm" 是一行小字（列表底部加载更多之类）。
import KnitMark from './KnitMark.vue'

withDefaults(defineProps<{ text?: string; sub?: string; size?: 'sm' | 'md' | 'lg' }>(), { text: '', sub: '', size: 'md' })
</script>

<template>
  <div class="brand-loader" :class="size" role="status" aria-live="polite">
    <span class="orbit">
      <span class="ring" />
      <KnitMark :size="size === 'lg' ? 50 : size === 'md' ? 34 : 18" class="logo" />
    </span>
    <span v-if="text || sub" class="words">
      <span v-if="text" class="text">{{ text }}<i class="dots"><b>.</b><b>.</b><b>.</b></i></span>
      <span v-if="sub" class="sub">{{ sub }}</span>
    </span>
  </div>
</template>

<style scoped>
.brand-loader {
  display: flex;
  flex-direction: column;
  gap: 14px;
  align-items: center;
  justify-content: center;
  color: var(--ink-soft, #52525b);
  text-align: center;
}
.brand-loader.sm {
  flex-direction: row;
  gap: 8px;
}
.orbit {
  position: relative;
  display: grid;
  place-items: center;
  width: 52px;
  height: 52px;
}
.lg .orbit {
  width: 76px;
  height: 76px;
}
.sm .orbit {
  width: 26px;
  height: 26px;
}
.ring {
  position: absolute;
  inset: 0;
  border-radius: 50%;
  border: 3px solid var(--line, #e9ebf2);
  border-top-color: var(--indigo, #3446c9);
  animation: fk-loader-spin 1.1s linear infinite;
}
.sm .ring {
  border-width: 2px;
}
.logo {
  animation: fk-loader-breathe 1.8s ease-in-out infinite;
}
.words {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.text {
  font-size: var(--t-sm, 13px);
}
.lg .text {
  color: var(--ink, #1f2330);
  font-size: 17px;
  font-weight: 600;
}
.sub {
  color: var(--ink-faint, #7a8092);
  font-size: var(--t-xs, 12px);
}
.lg .sub {
  font-size: 13px;
}
.dots {
  display: inline-block;
  width: 1.2em;
  font-style: normal;
  text-align: left;
}
.dots b {
  font-weight: inherit;
  animation: fk-loader-dot 1.2s infinite;
}
.dots b:nth-child(2) {
  animation-delay: 0.2s;
}
.dots b:nth-child(3) {
  animation-delay: 0.4s;
}
@keyframes fk-loader-spin {
  to {
    transform: rotate(360deg);
  }
}
@keyframes fk-loader-breathe {
  0%,
  100% {
    transform: scale(0.92);
  }
  50% {
    transform: scale(1);
  }
}
@keyframes fk-loader-dot {
  0%,
  20% {
    opacity: 0.15;
  }
  50%,
  100% {
    opacity: 1;
  }
}
@media (prefers-reduced-motion: reduce) {
  .ring,
  .logo,
  .dots b {
    animation-duration: 3s;
  }
}
</style>
