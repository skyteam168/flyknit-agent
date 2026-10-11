<script setup lang="ts">
import { computed } from 'vue'

/** 专家头像：管理员填的 emoji 或图片；都没有就用名字的第一个字。 */
const props = defineProps<{ avatar: string; name: string; id: string; size?: number }>()

const size = computed(() => props.size ?? 36)
const isImage = computed(() => /^(https?:|data:image\/)/.test(props.avatar))
const initial = computed(() => [...props.name.trim()][0] ?? '?')
const hue = computed(() => {
  let h = 0
  for (const ch of props.id || props.name) h = (h * 31 + ch.charCodeAt(0)) % 360
  return h
})
</script>

<template>
  <img v-if="isImage" :src="avatar" alt="" class="expert-avatar" :style="{ width: `${size}px`, height: `${size}px` }" />
  <span
    v-else-if="avatar"
    class="expert-avatar emoji"
    :style="{ width: `${size}px`, height: `${size}px`, fontSize: `${Math.round(size * 0.55)}px` }"
    aria-hidden="true"
    >{{ avatar }}</span
  >
  <span
    v-else
    class="expert-avatar letter"
    :style="{ width: `${size}px`, height: `${size}px`, fontSize: `${Math.round(size * 0.42)}px`, background: `hsl(${hue} 55% 48%)` }"
    aria-hidden="true"
    >{{ initial }}</span
  >
</template>

<style scoped>
.expert-avatar {
  flex: none;
  display: inline-grid;
  place-items: center;
  border-radius: 28%;
  object-fit: cover;
  line-height: 1;
}
.emoji {
  background: var(--chip);
}
.letter {
  color: #fff;
  font-weight: 700;
}
</style>
