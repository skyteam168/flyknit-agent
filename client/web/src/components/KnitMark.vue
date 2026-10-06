<script setup lang="ts">
// FlyknitBuddy 标志：戴着耳机、抱着笔记本的小机器人。
// 用位图而不是 SVG：原图是栅格的，描摹成矢量后相邻色块之间会留发丝级缝隙，放大就是一道道裂纹。
// 同一张图也用来生成程序图标和托盘图标（client/src/Flyknit.Client/Assets/flyknitbuddy.ico）。
import markUrl from '../assets/flyknitbuddy-mark.png'

defineProps<{ size?: number }>()
</script>

<template>
  <img :src="markUrl" :width="size ?? 28" :height="size ?? 28" alt="" aria-hidden="true" class="mark" />
</template>

<style scoped>
.mark {
  flex: none;
  display: block;
  object-fit: contain;
}
</style>
