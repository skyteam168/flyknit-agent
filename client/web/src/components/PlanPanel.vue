<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Circle, CircleDot } from '@lucide/vue'
import type { PlanItem } from '../types'

const props = defineProps<{ plan: PlanItem[] }>()
const { t } = useI18n()
const done = computed(() => props.plan.filter((p) => p.status === 'completed').length)
</script>

<template>
  <aside class="plan" :aria-label="t('plan.title')">
    <h2>{{ t('plan.title') }}</h2>
    <p class="progress">{{ t('plan.progress', { done, total: plan.length }) }}</p>
    <div class="meter" role="progressbar" :aria-valuenow="done" :aria-valuemax="plan.length">
      <span :style="{ width: `${plan.length ? (done / plan.length) * 100 : 0}%` }" />
    </div>
    <ol>
      <li v-for="(p, i) in plan" :key="i" :class="p.status">
        <Check v-if="p.status === 'completed'" :size="15" />
        <CircleDot v-else-if="p.status === 'in_progress'" :size="15" />
        <Circle v-else :size="15" />
        <span>{{ p.step }}</span>
      </li>
    </ol>
  </aside>
</template>

<style scoped>
.plan {
  width: var(--plan-w);
  flex: none;
  padding: 22px 20px;
  border-left: 1px solid var(--line);
  background: var(--loom);
  overflow-y: auto;
}
h2 {
  margin: 0;
  font-size: var(--t-md);
  font-weight: 600;
}
.progress {
  margin: 2px 0 10px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.meter {
  height: 4px;
  border-radius: 2px;
  background: var(--line);
  overflow: hidden;
  margin-bottom: 16px;
}
.meter span {
  display: block;
  height: 100%;
  background: var(--thread);
  transition: width 300ms ease;
}
ol {
  margin: 0;
  padding: 0;
  list-style: none;
}
li {
  display: flex;
  gap: 10px;
  padding: 7px 0;
  font-size: var(--t-sm);
  color: var(--ink-soft);
  line-height: 1.45;
}
li svg {
  flex: none;
  margin-top: 2px;
  color: var(--ink-faint);
}
li.completed {
  color: var(--ink-faint);
}
li.completed svg {
  color: var(--thread);
}
li.in_progress {
  color: var(--ink);
  font-weight: 500;
}
li.in_progress svg {
  color: var(--indigo);
}
</style>
