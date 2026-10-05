<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AppWindow,
  BarChart3,
  FileText,
  FolderTree,
  Download,
  Languages,
  Lightbulb,
  ListChecks,
  Mail,
  MessageSquare,
  Presentation,
  ScrollText,
  Wrench,
} from '@lucide/vue'
import KnitMark from './KnitMark.vue'
import Composer from './Composer.vue'
import { draftMode, setMode, setTranslate } from '../store'
import type { Mode } from '../types'

const { t } = useI18n()
const composer = ref<InstanceType<typeof Composer>>()
defineExpose({ useSkill: (name: string) => composer.value?.useSkill(name) })

const modes: { key: Mode; icon: unknown }[] = [
  { key: 'agent', icon: Wrench },
  { key: 'chat', icon: MessageSquare },
  { key: 'translate', icon: Languages },
]

const chips = computed(() => {
  switch (draftMode.mode) {
    case 'agent':
      return [
        { key: 'files', icon: FolderTree },
        { key: 'excel', icon: BarChart3 },
        { key: 'ppt', icon: Presentation },
        { key: 'word', icon: FileText },
        { key: 'install', icon: Download },
        { key: 'open', icon: AppWindow },
      ]
    case 'chat':
      return [
        { key: 'email', icon: Mail },
        { key: 'explain', icon: Lightbulb },
        { key: 'summary', icon: ScrollText },
        { key: 'plan', icon: ListChecks },
      ]
    default:
      return [
        { key: 'vi', icon: Languages },
        { key: 'zh', icon: Languages },
        { key: 'en', icon: Languages },
        { key: 'km', icon: Languages },
      ]
  }
})

const targetCode: Record<string, string> = { vi: 'vi', zh: 'zh-CN', en: 'en', km: 'km' }

function isActiveChip(key: string) {
  return draftMode.mode === 'translate' && draftMode.translateTo === targetCode[key]
}

async function pick(key: string) {
  if (draftMode.mode === 'translate') {
    await setTranslate('auto', targetCode[key])
    window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
    return
  }
  composer.value?.fill(t(`ui.prompts.${draftMode.mode}.${key}`))
}
</script>

<template>
  <div class="home-page">
    <section class="hero">
      <div class="mark">
        <KnitMark :size="72" />
      </div>
      <h1>{{ t('ui.home.title') }}</h1>
      <p class="sub">{{ t('ui.home.subtitle') }}</p>
      <div class="stitches" aria-hidden="true" />

      <div class="modes" role="radiogroup">
        <button
          v-for="m in modes"
          :key="m.key"
          type="button"
          role="radio"
          :aria-checked="draftMode.mode === m.key"
          :class="{ on: draftMode.mode === m.key }"
          @click="setMode(m.key)"
        >
          <span class="dot"><component :is="m.icon" :size="15" /></span>
          {{ t(`mode.${m.key}`) }}
        </button>
      </div>

      <div class="chips">
        <button
          v-for="c in chips"
          :key="c.key"
          type="button"
          :class="{ on: isActiveChip(c.key) }"
          @click="pick(c.key)"
        >
          <component :is="c.icon" :size="15" />
          {{ t(`ui.chips.${draftMode.mode}.${c.key}`) }}
        </button>
      </div>

      <Composer ref="composer" home />
    </section>
  </div>
</template>

<style scoped>
.home-page {
  flex: 1;
  overflow-y: auto;
  display: flex;
}
.hero {
  width: 100%;
  max-width: 880px;
  margin: auto;
  padding: 40px 32px 32px;
  display: flex;
  flex-direction: column;
  align-items: center;
}
.mark {
  display: grid;
  place-items: center;
  width: 104px;
  height: 104px;
  border-radius: 30px;
  background: radial-gradient(circle at 50% 40%, var(--indigo-wash), transparent 70%);
}
h1 {
  margin: 14px 0 6px;
  font-size: var(--t-2xl);
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.25;
  text-align: center;
}
.sub {
  margin: 0;
  max-width: 560px;
  color: var(--ink-soft);
  text-align: center;
}
.stitches {
  height: 0;
  margin: 14px 0 18px;
}
.modes {
  display: inline-flex;
  gap: 4px;
  padding: 4px;
  border-radius: 999px;
  background: var(--chip);
}
.modes button {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 42px;
  padding: 0 20px 0 8px;
  border-radius: 999px;
  color: var(--ink-soft);
  font-weight: 500;
}
.modes button:hover {
  color: var(--ink);
}
.dot {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  background: var(--cloth);
  color: var(--ink);
}
.modes button.on {
  background: var(--seg);
  color: var(--seg-ink);
  box-shadow: var(--seg-shadow);
}
.modes button.on .dot {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.chips {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 8px;
  margin: 18px 0 16px;
}
.chips button {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  height: 36px;
  padding: 0 14px;
  border-radius: 999px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.chips button:hover {
  color: var(--ink);
  background: color-mix(in srgb, var(--ink) 9%, var(--chip));
}
.chips button.on {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.hero > :deep(.composer-wrap) {
  width: 100%;
}
@media (max-width: 720px) {
  .hero {
    padding: 24px 14px 16px;
  }
  h1 {
    font-size: var(--t-xl);
  }
  .modes button {
    padding: 0 14px 0 6px;
  }
}
</style>
