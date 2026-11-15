<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import NoticeBody from './NoticeBody.vue'
import { closePopup, notices } from './store'

/** 重要通知：打开程序时没读过的弹出来（每条只弹一次），点「知道了」算已读 */
const { t } = useI18n()
</script>

<template>
  <div v-if="notices.popup" class="scrim" @keydown.esc="closePopup()">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="notices.popup.title">
      <div class="scroll">
        <NoticeBody :notice="notices.popup" />
      </div>
      <div class="actions">
        <button type="button" class="btn" @click="closePopup(true)">{{ t('ui.notices.openCenter') }}</button>
        <button type="button" class="btn primary" @click="closePopup()">{{ t('ui.notices.gotIt') }}</button>
      </div>
    </div>
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
  background: color-mix(in srgb, var(--ink) 30%, transparent);
}
.dialog {
  display: flex;
  flex-direction: column;
  width: min(640px, 100%);
  max-height: min(80vh, 760px);
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.scroll {
  overflow: auto;
  padding: 22px 24px 8px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 12px 18px 16px;
  border-top: 1px solid var(--line);
}
</style>
