<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { ExternalLink, Globe } from '@lucide/vue'
import { bridge } from '../bridge'
import { state } from '../store'

/** 右侧「浏览器」标签：AI 正在操作的网页（地址、标题、最近一张截图），可以把浏览器窗口叫到前面 */
const { t } = useI18n()
</script>

<template>
  <aside class="browser">
    <div class="head">
      <Globe :size="15" />
      <strong>{{ state.browser.title || t('ui.browser.untitled') }}</strong>
    </div>
    <p class="url" :title="state.browser.url">{{ state.browser.url }}</p>
    <p v-if="state.browser.action" class="action">{{ state.browser.action }}</p>
    <img v-if="state.browser.shot" :src="state.browser.shot" alt="" class="shot" />
    <p class="hint">{{ t('ui.browser.hint') }}</p>
    <button type="button" class="btn" @click="bridge.showBrowser().catch(() => {})"><ExternalLink :size="14" /> {{ t('ui.browser.show') }}</button>
  </aside>
</template>

<style scoped>
.browser {
  width: 320px;
  overflow-y: auto;
  padding: 12px;
  border-left: 1px solid var(--line);
  background: var(--loom);
  font-size: var(--t-sm);
}
.head {
  display: flex;
  align-items: center;
  gap: 6px;
}
.head strong {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.url {
  margin: 4px 0 8px;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  white-space: nowrap;
  text-overflow: ellipsis;
}
.action {
  margin: 0 0 8px;
  color: var(--indigo);
}
.shot {
  width: 100%;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
}
.hint {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--cloth);
  font-size: var(--t-sm);
}
</style>
