<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, ChevronDown, Cpu, Eye, RefreshCw, Search, Sparkles } from '@lucide/vue'
import Popover from './Popover.vue'
import { loadModels, selectModel, selectedModelId, state } from '../store'
import type { Mode } from '../types'

const props = defineProps<{ mode: Mode }>()
const { t } = useI18n()

const query = ref('')
const current = computed(() => state.models.find((m) => m.id === selectedModelId.value) ?? null)
const groups = computed(() => {
  const map = new Map<string, typeof state.models>()
  const q = query.value.trim().toLowerCase()
  for (const m of state.models.filter((x) => !q || x.name.toLowerCase().includes(q) || x.model.toLowerCase().includes(q))) {
    if (!map.has(m.provider)) map.set(m.provider, [])
    map.get(m.provider)!.push(m)
  }
  return [...map.entries()]
})

async function pick(id: number | null, close: () => void) {
  close()
  query.value = ''
  await selectModel(id)
}
</script>

<template>
  <Popover :width="300">
    <template #trigger="{ toggle, open }">
      <button type="button" class="trigger" :class="{ open }" :title="t('ui.model.title')" @click="toggle">
        <component :is="current ? Cpu : Sparkles" :size="15" />
        <span class="label">{{ current ? current.name : t('ui.model.auto') }}</span>
        <ChevronDown :size="14" class="chev" />
      </button>
    </template>
    <template #default="{ close }">
      <div class="head">
        <span>{{ t('ui.model.title') }}</span>
        <button type="button" class="refresh" :title="t('ui.model.title')" @click="loadModels(true)"><RefreshCw :size="13" /></button>
      </div>
      <label v-if="state.models.length > 8" class="search">
        <Search :size="14" />
        <input v-model="query" :placeholder="t('ui.model.search')" @keydown.stop />
      </label>
      <button v-if="!query" type="button" class="opt" :class="{ on: selectedModelId === null }" @click="pick(null, close)">
        <Sparkles :size="15" class="ico" />
        <span class="text">
          <strong>{{ t('ui.model.auto') }}</strong>
          <small>{{ t('ui.model.autoHint') }}</small>
        </span>
        <Check v-if="selectedModelId === null" :size="15" class="tick" />
      </button>
      <p v-if="state.models.length === 0" class="empty">{{ t('ui.model.empty') }}</p>
      <template v-for="[provider, items] in groups" :key="provider">
        <div class="group">{{ provider }}</div>
        <button
          v-for="m in items"
          :key="m.id"
          type="button"
          class="opt"
          :class="{ on: selectedModelId === m.id }"
          :disabled="props.mode === 'agent' && !m.supportsTools"
          @click="pick(m.id, close)"
        >
          <Cpu :size="15" class="ico" />
          <span class="text">
            <strong>{{ m.name }}</strong>
            <small v-if="props.mode === 'agent' && !m.supportsTools">{{ t('ui.model.noTools') }}</small>
            <small v-else-if="m.name !== m.model">{{ m.model }}</small>
          </span>
          <span v-if="m.supportsVision" class="tag"><Eye :size="12" />{{ t('ui.model.vision') }}</span>
          <Check v-if="selectedModelId === m.id" :size="15" class="tick" />
        </button>
      </template>
    </template>
  </Popover>
</template>

<style scoped>
.trigger {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  max-width: 220px;
  padding: 0 10px;
  border-radius: 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 500;
}
.trigger:hover,
.trigger.open {
  background: var(--chip);
  color: var(--ink);
}
.label {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.chev {
  flex: none;
  color: var(--ink-faint);
}
.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 8px 8px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.refresh {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.refresh:hover {
  background: var(--chip);
  color: var(--ink);
}
.search {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0 4px 6px;
  padding: 0 8px;
  height: 32px;
  border-radius: 8px;
  background: var(--chip);
  color: var(--ink-faint);
}
.search input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: 0;
  background: transparent;
  font-size: var(--t-sm);
}
.group {
  margin: 8px 8px 4px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.opt {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 40px;
  padding: 6px 8px;
  border-radius: 8px;
  text-align: left;
}
.opt:hover:not(:disabled) {
  background: var(--chip);
}
.opt:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
.ico {
  flex: none;
  color: var(--ink-faint);
}
.on .ico {
  color: var(--indigo);
}
.text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.3;
}
.text strong {
  font-weight: 500;
  font-size: var(--t-sm);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.text small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.tag {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 6px;
  border-radius: 10px;
  background: var(--thread-wash);
  color: var(--thread);
  font-size: calc(11px * var(--font-scale));
}
.tick {
  flex: none;
  color: var(--indigo);
}
.empty {
  margin: 8px;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
</style>
