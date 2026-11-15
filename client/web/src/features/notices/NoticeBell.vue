<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bell } from '@lucide/vue'
import { notices, openNotices } from './store'

/** 左下角用户卡片上的铃铛：未读数角标，点开消息中心 */
const emit = defineEmits<{ open: [] }>()
const { t } = useI18n()
const badge = computed(() => (notices.unread > 99 ? '99+' : String(notices.unread)))
function open() {
  openNotices()
  emit('open')
}
</script>

<template>
  <button
    v-if="notices.supported"
    type="button"
    class="bell"
    :title="notices.unread ? t('ui.notices.bellUnread', { n: notices.unread }) : t('ui.notices.bell')"
    :aria-label="notices.unread ? t('ui.notices.bellUnread', { n: notices.unread }) : t('ui.notices.bell')"
    @click.stop="open"
  >
    <Bell :size="17" />
    <span v-if="notices.unread" class="badge">{{ badge }}</span>
  </button>
</template>

<style scoped>
.bell {
  position: relative;
  display: grid;
  flex: none;
  place-items: center;
  width: 32px;
  height: 32px;
  border-radius: 9px;
  color: var(--ink-soft);
}
.bell:hover {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.badge {
  position: absolute;
  top: -2px;
  right: -4px;
  box-sizing: border-box;
  min-width: 18px;
  height: 18px;
  padding: 0 4px;
  border: 2px solid var(--cloth);
  border-radius: 9px;
  background: var(--red);
  color: #fff;
  font-size: 10px;
  font-weight: 600;
  line-height: 14px;
  text-align: center;
}
</style>
