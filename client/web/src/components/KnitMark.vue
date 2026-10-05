<script setup lang="ts">
// Flyknit 标志：一个针织线圈（knit stitch），两股纱线交叉成 V 形
defineProps<{ size?: number }>()
</script>

<template>
  <svg :width="size ?? 28" :height="size ?? 28" viewBox="0 0 32 32" aria-hidden="true" class="knit-mark">
    <rect width="32" height="32" rx="9" class="bg" />
    <path d="M8.5 9.5c2.4 0 4.6 1.7 5.6 4.3L16 19l1.9-5.2c1-2.6 3.2-4.3 5.6-4.3" class="yarn" />
    <path d="M10.5 13.5c1.6.4 2.9 1.6 3.5 3.3L16 23.5l2-6.7c.6-1.7 1.9-2.9 3.5-3.3" class="yarn thin" />
  </svg>
</template>

<style scoped>
.bg {
  fill: var(--indigo);
}
.yarn {
  fill: none;
  stroke: #fff;
  stroke-width: 2.4;
  stroke-linecap: round;
  stroke-linejoin: round;
}
.thin {
  stroke: #9be7df;
  stroke-width: 1.8;
}
</style>
