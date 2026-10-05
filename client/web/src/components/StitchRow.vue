<script setup lang="ts">
// 欢迎页上的一行针织纹：一排 V 形线圈，进入时依次“织”出来
const stitches = Array.from({ length: 18 }, (_, i) => i)
</script>

<template>
  <svg class="stitch-row" viewBox="0 0 360 22" aria-hidden="true">
    <g v-for="i in stitches" :key="i" :style="{ animationDelay: `${i * 45}ms` }" class="stitch">
      <path :d="`M${i * 20 + 2} 3c3 0 6 2 7 6l1 9 1-9c1-4 4-6 7-6`" />
    </g>
  </svg>
</template>

<style scoped>
.stitch-row {
  width: 260px;
  height: 16px;
  display: block;
}
.stitch path {
  fill: none;
  stroke: var(--thread);
  stroke-width: 1.8;
  stroke-linecap: round;
  stroke-linejoin: round;
}
.stitch {
  opacity: 0;
  animation: knit 360ms ease-out forwards;
}
.stitch:nth-child(3n) path {
  stroke: var(--indigo);
}
@keyframes knit {
  from {
    opacity: 0;
    transform: translateY(-3px);
  }
  to {
    opacity: 1;
    transform: none;
  }
}
</style>
