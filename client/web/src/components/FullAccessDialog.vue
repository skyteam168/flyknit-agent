<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ShieldAlert } from '@lucide/vue'

const emit = defineEmits<{ ok: []; cancel: [] }>()
const { t } = useI18n()
const agreed = ref(false)
const cancelBtn = ref<HTMLButtonElement>()
onMounted(() => cancelBtn.value?.focus())
</script>

<template>
  <Teleport to="body">
    <div class="scrim" @mousedown.self="emit('cancel')" @keydown.esc="emit('cancel')">
      <div class="dialog" role="alertdialog" aria-modal="true" :aria-label="t('ui.fullAccess.title')">
        <div class="badge"><ShieldAlert :size="22" /></div>
        <h2>{{ t('ui.fullAccess.title') }}</h2>
        <p class="lead">{{ t('ui.fullAccess.body') }}</p>
        <ul>
          <li>{{ t('ui.fullAccess.point1') }}</li>
          <li>{{ t('ui.fullAccess.point2') }}</li>
          <li>{{ t('ui.fullAccess.point3') }}</li>
        </ul>
        <label class="agree">
          <input v-model="agreed" type="checkbox" />
          <span>{{ t('ui.fullAccess.agree') }}</span>
        </label>
        <div class="actions">
          <button ref="cancelBtn" type="button" class="btn" @click="emit('cancel')">{{ t('ui.fullAccess.cancel') }}</button>
          <button type="button" class="btn warn" :disabled="!agreed" @click="emit('ok')">{{ t('ui.fullAccess.ok') }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 90;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 32%, transparent);
}
.dialog {
  width: min(440px, 100%);
  padding: 24px 24px 20px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  animation: pop 160ms ease-out;
}
.badge {
  display: grid;
  place-items: center;
  width: 44px;
  height: 44px;
  margin-bottom: 14px;
  border-radius: 12px;
  background: var(--amber-wash);
  color: var(--amber);
}
h2 {
  margin: 0 0 8px;
  font-size: var(--t-lg);
  font-weight: 600;
}
.lead {
  margin: 0 0 10px;
  color: var(--ink-soft);
  line-height: 1.6;
}
ul {
  margin: 0 0 18px;
  padding-left: 18px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.65;
}
.agree {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 20px;
  padding: 10px 12px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  font-size: var(--t-sm);
  cursor: pointer;
}
.agree input {
  width: 16px;
  height: 16px;
  accent-color: var(--amber);
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
.btn.warn {
  border-color: transparent;
  background: var(--amber);
  color: #fff;
}
.btn.warn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
@keyframes pop {
  from {
    opacity: 0;
    transform: translateY(6px) scale(0.98);
  }
}
</style>
