<script setup lang="ts">
// FlyknitBuddy 标志：戴着耳机、抱着笔记本的小机器人。
// 同一个 SVG 也用来生成程序图标和托盘图标（client/src/Flyknit.Client/Assets/flyknitbuddy.ico）。
import markUrl from '../assets/flyknitbuddy-mark.svg'

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
