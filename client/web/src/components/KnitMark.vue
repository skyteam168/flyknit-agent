<script setup lang="ts">
// FlyknitBuddy 标志：一只用针织线圈织成的燕子。
// 燕子代表轻快（Fly），翅膀上的三道圆弧是针织线圈（Knit），圆眼睛代表陪伴（Buddy）。
defineProps<{ size?: number }>()
const gid = `fkb-${Math.random().toString(36).slice(2, 8)}`
</script>

<template>
  <svg :width="size ?? 28" :height="size ?? 28" viewBox="0 0 512 512" aria-hidden="true" class="mark">
    <defs>
      <linearGradient :id="gid" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="#4A5DF0" />
        <stop offset="1" stop-color="#2230A6" />
      </linearGradient>
    </defs>
    <rect width="512" height="512" rx="116" :fill="`url(#${gid})`" />
    <g fill="#fff">
      <path d="M432,152 C414,126 380,118 356,136 C330,156 284,214 246,258 C226,280 210,296 196,306 L80,356 L188,334 L120,428 L236,326 C290,292 340,248 376,204 C394,182 410,164 432,152 Z" />
      <path d="M336,170 C300,120 230,84 150,70 Q156,113 206,111 Q205,161 254,159 Q253,209 296,214 Z" />
    </g>
    <circle cx="396" cy="150" r="10" fill="#2B3BB8" />
  </svg>
</template>

<style scoped>
.mark {
  flex: none;
  display: block;
}
</style>
