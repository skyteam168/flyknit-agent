<script setup lang="ts">
import { computed, ref, watch } from 'vue'

/**
 * 连接器图标。厂商给的图片大小、留白、长宽比五花八门，直接摆出来一张大一张小；
 * 这里统一放进同样大小的白色圆角底座，图片按比例缩进去。没有图标（或加载失败）就用名字首字。
 */
const props = withDefaults(defineProps<{ icon?: string; name: string; id?: string; size?: number; round?: boolean }>(), {
  icon: '',
  id: '',
  size: 32,
  round: false,
})

const broken = ref(false)
watch(
  () => props.icon,
  () => (broken.value = false),
)

const letter = computed(() => [...props.name.trim()][0]?.toUpperCase() ?? '?')
const hue = computed(() => {
  let h = 0
  for (const ch of props.id || props.name) h = (h * 31 + ch.charCodeAt(0)) % 360
  return h
})
const style = computed(() => ({
  width: `${props.size}px`,
  height: `${props.size}px`,
  borderRadius: props.round ? '50%' : `${Math.round(props.size * 0.26)}px`,
  padding: `${Math.max(3, Math.round(props.size * (props.round ? 0.18 : 0.12)))}px`,
}))
</script>

<template>
  <span v-if="icon && !broken" class="vendor-icon" :style="style">
    <img :src="icon" alt="" @error="broken = true" />
  </span>
  <span
    v-else
    class="vendor-icon letter"
    :style="{ ...style, background: `hsl(${hue} 55% 48%)`, fontSize: `${Math.round(size * 0.44)}px` }"
    aria-hidden="true"
  >{{ letter }}</span>
</template>

<style scoped>
.vendor-icon {
  display: inline-grid;
  place-items: center;
  flex: none;
  box-sizing: border-box;
  overflow: hidden;
  /* 厂商图标基本都是按白底设计的，深色主题下也给白底，免得透明图标糊进背景 */
  background: #fff;
  box-shadow: inset 0 0 0 1px rgba(0, 0, 0, 0.08);
}
.vendor-icon img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: contain;
}
.letter {
  padding: 0 !important;
  box-shadow: none;
  color: #fff;
  font-weight: 700;
  line-height: 1;
}
</style>
