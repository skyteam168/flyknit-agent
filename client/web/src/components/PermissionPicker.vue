<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, ChevronDown, Eye, FolderPen, ShieldAlert } from '@lucide/vue'
import Popover from './Popover.vue'
import FullAccessDialog from './FullAccessDialog.vue'
import { selectedPermission, setPermission } from '../store'
import type { Permission } from '../types'

const { t } = useI18n()
const askFull = ref(false)

const options: { key: Permission; icon: unknown }[] = [
  { key: 'readonly', icon: Eye },
  { key: 'workspace', icon: FolderPen },
  { key: 'full', icon: ShieldAlert },
]
const currentIcon = computed(() => options.find((o) => o.key === selectedPermission.value)!.icon)

async function choose(p: Permission, close: () => void) {
  close()
  if (p === selectedPermission.value) return
  if (p === 'full') {
    askFull.value = true // 先弹窗确认风险
    return
  }
  await setPermission(p)
}

async function confirmFull() {
  askFull.value = false
  await setPermission('full')
}
</script>

<template>
  <Popover :width="320">
    <template #trigger="{ toggle, open }">
      <button
        type="button"
        class="trigger"
        :class="[selectedPermission, { open }]"
        :title="t('ui.permission.title')"
        @click="toggle"
      >
        <component :is="currentIcon" :size="14" />
        <span class="label">{{ t(`ui.permission.${selectedPermission}`) }}</span>
        <ChevronDown :size="13" class="chev" />
      </button>
    </template>
    <template #default="{ close }">
      <div class="head">{{ t('ui.permission.title') }}</div>
      <button
        v-for="o in options"
        :key="o.key"
        type="button"
        class="opt"
        :class="[o.key, { on: selectedPermission === o.key }]"
        @click="choose(o.key, close)"
      >
        <component :is="o.icon" :size="16" class="ico" />
        <span class="text">
          <strong>{{ t(`ui.permission.${o.key}`) }}</strong>
          <small>{{ t(`ui.permission.${o.key}Hint`) }}</small>
        </span>
        <Check v-if="selectedPermission === o.key" :size="15" class="tick" />
      </button>
      <p class="hint">{{ t('ui.permission.always') }}</p>
    </template>
  </Popover>
  <FullAccessDialog v-if="askFull" @ok="confirmFull" @cancel="askFull = false" />
</template>

<style scoped>
.trigger {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 8px;
  border-radius: 7px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  font-weight: 500;
  white-space: nowrap;
}
.trigger:hover,
.trigger.open {
  background: var(--chip);
  color: var(--ink);
}
.trigger.full {
  color: var(--amber);
  background: var(--amber-wash);
}
.chev {
  flex: none;
  color: var(--ink-faint);
}
.head {
  padding: 6px 8px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.opt {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  width: 100%;
  padding: 9px 8px;
  border-radius: 8px;
  text-align: left;
}
.opt:hover {
  background: var(--chip);
}
.ico {
  flex: none;
  margin-top: 1px;
  color: var(--ink-faint);
}
.on .ico {
  color: var(--indigo);
}
.opt.full .ico {
  color: var(--amber);
}
.text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  line-height: 1.4;
}
.text strong {
  color: var(--ink);
  font-weight: 500;
  font-size: var(--t-sm);
}
.text small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.tick {
  flex: none;
  margin-top: 2px;
  color: var(--indigo);
}
.hint {
  margin: 6px 8px 6px;
  padding-top: 8px;
  border-top: 1px solid var(--line);
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.45;
}
</style>
