<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'

// 一行输入的小对话框：重命名、新建文件夹
const props = defineProps<{ title: string; value?: string; placeholder?: string; ok: string }>()
const emit = defineEmits<{ ok: [value: string]; cancel: [] }>()
const { t } = useI18n()
const text = ref(props.value ?? '')
const input = ref<HTMLInputElement>()

onMounted(() => {
  const el = input.value
  if (!el) return
  el.focus()
  // 重命名时只选中文件名，不选扩展名
  const dot = text.value.lastIndexOf('.')
  el.setSelectionRange(0, dot > 0 ? dot : text.value.length)
})

function submit() {
  if (text.value.trim()) emit('ok', text.value.trim())
}
</script>

<template>
  <div class="scrim" @mousedown.self="emit('cancel')" @keydown.esc="emit('cancel')">
    <form class="dialog" role="dialog" aria-modal="true" :aria-label="title" @submit.prevent="submit">
      <h2>{{ title }}</h2>
      <input ref="input" v-model="text" :placeholder="placeholder" maxlength="200" />
      <div class="actions">
        <button type="button" class="btn" @click="emit('cancel')">{{ t('confirmDelete.cancel') }}</button>
        <button type="submit" class="btn primary" :disabled="!text.trim()">{{ ok }}</button>
      </div>
    </form>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 70;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
}
.dialog {
  width: min(400px, 100%);
  padding: 22px 22px 18px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
h2 {
  margin: 0 0 14px;
  font-size: var(--t-lg);
  font-weight: 600;
}
input {
  width: 100%;
  height: 38px;
  padding: 0 12px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  outline: 0;
}
input:focus {
  border-color: var(--indigo);
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 18px;
}
</style>
