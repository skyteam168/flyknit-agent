<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { draftMode, setMode, state } from '../store'
import type { Mode } from '../types'
import StitchRow from './StitchRow.vue'

const emit = defineEmits<{ pick: [text: string] }>()
const { t, locale } = useI18n()

const greeting = computed(() => {
  const h = new Date().getHours()
  return h < 11 ? t('welcome.morning') : h < 18 ? t('welcome.afternoon') : t('welcome.evening')
})

const name = computed(() => {
  const raw = state.app?.userName ?? ''
  // Windows 用户名常见为 nguyen.van.a / zhangsan，取第一段即可
  const first = raw.split(/[.\s_]/)[0]
  return first ? first.charAt(0).toUpperCase() + first.slice(1) : ''
})

const examples: { key: string; mode: Mode }[] = [
  { key: 'files', mode: 'agent' },
  { key: 'excel', mode: 'agent' },
  { key: 'install', mode: 'agent' },
  { key: 'outlook', mode: 'agent' },
  { key: 'translate', mode: 'translate' },
  { key: 'email', mode: 'chat' },
]

const visible = computed(() =>
  draftMode.mode === 'translate' ? examples.filter((e) => e.mode === 'translate') : examples.filter((e) => e.mode !== 'translate'),
)

async function pick(e: { key: string; mode: Mode }) {
  if (draftMode.mode !== e.mode && !(draftMode.mode === 'agent' && e.mode === 'chat')) await setMode(e.mode)
  emit('pick', t(`welcome.examples.${e.key}`))
}
</script>

<template>
  <section class="welcome">
    <StitchRow />
    <h2>{{ greeting }}<template v-if="name">{{ locale === 'zh-CN' ? '，' : ', ' }}{{ name }}</template></h2>
    <p class="sub">{{ draftMode.mode === 'translate' ? t('mode.translateHint') : t('welcome.subtitle') }}</p>
    <ul class="examples">
      <li v-for="e in visible" :key="e.key">
        <button type="button" @click="pick(e)">{{ t(`welcome.examples.${e.key}`) }}</button>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.welcome {
  max-width: var(--column);
  margin: 0 auto;
  padding: 12vh 32px 24px;
}
h2 {
  margin: 18px 0 4px;
  font-size: var(--t-2xl);
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.2;
}
.sub {
  margin: 0 0 28px;
  font-size: var(--t-lg);
  color: var(--ink-soft);
}
.examples {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}
.examples button {
  height: 36px;
  padding: 0 14px;
  border-radius: 18px;
  border: 1px solid var(--line-strong);
  background: var(--cloth);
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.examples button:hover {
  border-color: var(--indigo);
  color: var(--indigo);
}
@media (max-width: 720px) {
  .welcome {
    padding: 8vh 20px 16px;
  }
}
</style>
