<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { FolderOpen } from '@lucide/vue'
import { bridge } from '../bridge'
import { uiLanguages } from '../i18n'
import { setLanguage, setTheme, state } from '../store'
import type { Theme } from '../types'

const { t } = useI18n()
const themes: Theme[] = ['system', 'light', 'dark']
</script>

<template>
  <div class="scrim" @mousedown.self="state.settingsOpen = false" @keydown.esc="state.settingsOpen = false">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('settings.title')">
      <h2>{{ t('settings.title') }}</h2>

      <section>
        <h3>{{ t('settings.language') }}</h3>
        <div class="choices">
          <button
            v-for="l in uiLanguages"
            :key="l.code"
            type="button"
            :class="{ on: state.app?.uiLanguage === l.code }"
            :aria-pressed="state.app?.uiLanguage === l.code"
            @click="setLanguage(l.code)"
          >
            {{ l.label }}
          </button>
        </div>
      </section>

      <section>
        <h3>{{ t('settings.theme') }}</h3>
        <div class="choices">
          <button
            v-for="th in themes"
            :key="th"
            type="button"
            :class="{ on: state.app?.theme === th }"
            :aria-pressed="state.app?.theme === th"
            @click="setTheme(th)"
          >
            {{ t(`settings.themes.${th}`) }}
          </button>
        </div>
      </section>

      <section>
        <h3>{{ t('settings.memory') }}</h3>
        <p class="hint">{{ t('settings.memoryHint') }}</p>
        <button type="button" class="btn" @click="bridge.openMemoryFolder()">
          <FolderOpen :size="15" /> {{ t('settings.openMemory') }}
        </button>
      </section>

      <footer>
        <span class="about">{{ t('app.name') }} {{ t('settings.version', { v: state.app?.version ?? '' }) }}<br />{{ state.app?.userName }}, {{ state.app?.machineName }}</span>
        <button type="button" class="btn primary" @click="state.settingsOpen = false">{{ t('settings.close') }}</button>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
}
.dialog {
  width: min(460px, 100%);
  padding: 24px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
h2 {
  margin: 0 0 18px;
  font-size: var(--t-xl);
  font-weight: 600;
}
section {
  margin-bottom: 20px;
}
h3 {
  margin: 0 0 8px;
  font-size: var(--t-sm);
  font-weight: 600;
  color: var(--ink-soft);
}
.choices {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}
.choices button {
  height: 34px;
  padding: 0 14px;
  border-radius: var(--r-sm);
  border: 1px solid var(--line-strong);
  font-size: var(--t-sm);
}
.choices button.on {
  border-color: var(--indigo);
  background: var(--indigo-wash);
  color: var(--indigo);
  font-weight: 500;
}
.hint {
  margin: 0 0 10px;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
footer {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 12px;
  padding-top: 16px;
  border-top: 1px solid var(--line);
}
.about {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
</style>
