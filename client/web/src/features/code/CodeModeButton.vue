<script setup lang="ts">
/** 标题栏上的「编码模式」开关：开发者工具开着、办事对话选了工作区时出现 */
import { useI18n } from 'vue-i18n'
import { Code2 } from '@lucide/vue'
import { codeAvailable, ide, toggleIde } from './store'

const { t } = useI18n()
</script>

<template>
  <button
    v-if="codeAvailable"
    type="button"
    class="code-mode"
    :class="{ on: ide.on }"
    :aria-pressed="ide.on"
    :title="ide.on ? t('ui.code.leave') : t('ui.code.enterHint')"
    @click="toggleIde()"
  >
    <Code2 :size="15" />
    <span>{{ t('ui.code.mode') }}</span>
  </button>
</template>

<style scoped>
.code-mode {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: transparent;
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
  cursor: pointer;
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
.code-mode:hover {
  color: var(--ink);
  border-color: var(--indigo);
}
.code-mode.on {
  border-color: var(--indigo);
  background: var(--indigo-wash);
  color: var(--indigo);
}
</style>
