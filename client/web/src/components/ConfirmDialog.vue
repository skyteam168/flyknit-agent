<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'

defineProps<{ title: string; body: string; ok: string; danger?: boolean }>()
const emit = defineEmits<{ ok: []; cancel: [] }>()
const { t } = useI18n()
const okBtn = ref<HTMLButtonElement>()
onMounted(() => okBtn.value?.focus())
</script>

<template>
  <div class="scrim" @mousedown.self="emit('cancel')" @keydown.esc="emit('cancel')">
    <div class="dialog" role="alertdialog" aria-modal="true" :aria-label="title">
      <h2>{{ title }}</h2>
      <p>{{ body }}</p>
      <div class="actions">
        <button type="button" class="btn" @click="emit('cancel')">{{ t('confirmDelete.cancel') }}</button>
        <button ref="okBtn" type="button" class="btn" :class="danger ? 'danger' : 'primary'" @click="emit('ok')">
          {{ ok }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
  padding: 16px;
}
.dialog {
  width: min(400px, 100%);
  padding: 22px 22px 18px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  animation: pop 160ms ease-out;
}
h2 {
  margin: 0 0 6px;
  font-size: var(--t-lg);
  font-weight: 600;
}
p {
  margin: 0 0 20px;
  color: var(--ink-soft);
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
@keyframes pop {
  from {
    opacity: 0;
    transform: translateY(6px) scale(0.98);
  }
}
</style>
