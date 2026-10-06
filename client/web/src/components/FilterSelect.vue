<script setup lang="ts">
import { computed } from 'vue'
import { Check, ChevronDown } from '@lucide/vue'
import Popover from './Popover.vue'

export interface FilterOption {
  value: string
  label: string
  count?: number
}

const props = defineProps<{ modelValue: string; options: FilterOption[]; width?: number }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()

const current = computed(() => props.options.find((o) => o.value === props.modelValue) ?? props.options[0])

function pick(value: string, close: () => void) {
  emit('update:modelValue', value)
  close()
}
</script>

<template>
  <Popover placement="down" :width="width ?? 168">
    <template #trigger="{ open, toggle }">
      <button type="button" class="trigger" :class="{ open, active: modelValue !== '' }" @click="toggle">
        <span>{{ current?.label }}</span>
        <ChevronDown :size="14" />
      </button>
    </template>
    <template #default="{ close }">
      <button
        v-for="o in options"
        :key="o.value"
        type="button"
        class="opt"
        :class="{ on: o.value === modelValue }"
        @click="pick(o.value, close)"
      >
        <span class="name">{{ o.label }}</span>
        <span v-if="o.count !== undefined" class="count">{{ o.count }}</span>
        <Check v-if="o.value === modelValue" :size="14" class="tick" />
        <span v-else class="tick" />
      </button>
    </template>
  </Popover>
</template>

<style scoped>
.trigger {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  height: 32px;
  padding: 0 10px 0 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink-soft);
  font: inherit;
  font-size: var(--t-sm);
  white-space: nowrap;
  cursor: pointer;
}
.trigger:hover,
.trigger.open {
  border-color: var(--line-strong);
  color: var(--ink);
}
.trigger.active {
  color: var(--ink);
}
.opt {
  display: flex;
  gap: 8px;
  align-items: center;
  width: 100%;
  padding: 8px 10px;
  border: 0;
  border-radius: 7px;
  background: none;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
  text-align: left;
  cursor: pointer;
}
.opt:hover {
  background: var(--cloth-sunk);
}
.name {
  flex: 1;
}
.count {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.tick {
  flex: none;
  width: 14px;
  color: var(--ink);
}
</style>
