<script setup lang="ts">
import { onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { Building2, FolderOpen, User, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { loadSkills, state } from '../store'

const emit = defineEmits<{ use: [name: string] }>()
const { t } = useI18n()
onMounted(loadSkills)
</script>

<template>
  <div class="scrim" @mousedown.self="state.skillsOpen = false" @keydown.esc="state.skillsOpen = false">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.skills.title')">
      <header>
        <h2>{{ t('ui.skills.title') }}</h2>
        <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="state.skillsOpen = false"><X :size="18" /></button>
      </header>
      <p class="hint">{{ t('ui.skills.hint') }}</p>
      <p v-if="state.skills.length === 0" class="empty">{{ t('ui.skills.empty') }}</p>
      <ul>
        <li v-for="s in state.skills" :key="s.name">
          <div class="info">
            <strong>{{ s.name }}</strong>
            <span>{{ s.description }}</span>
          </div>
          <span class="badge" :class="{ org: s.organization }">
            <component :is="s.organization ? Building2 : User" :size="12" />
            {{ s.organization ? t('ui.skills.org') : t('ui.skills.personal') }}
          </span>
          <button type="button" class="btn" @click="emit('use', s.name)">{{ t('ui.skills.button') }}</button>
        </li>
      </ul>
      <footer>
        <button type="button" class="btn" @click="bridge.openSkillsFolder()"><FolderOpen :size="15" /> {{ t('ui.skills.openFolder') }}</button>
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
  width: min(560px, 100%);
  max-height: min(640px, 100%);
  display: flex;
  flex-direction: column;
  padding: 22px 24px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
h2 {
  margin: 0;
  font-size: var(--t-xl);
  font-weight: 600;
}
.hint {
  margin: 6px 0 14px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.empty {
  color: var(--ink-faint);
}
ul {
  flex: 1;
  overflow-y: auto;
  margin: 0;
  padding: 0;
  list-style: none;
}
li {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 0;
  border-top: 1px solid var(--line);
}
.info {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}
.info strong {
  font-weight: 600;
}
.info span {
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 10px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.badge.org {
  background: var(--indigo-wash);
  color: var(--indigo);
}
footer {
  padding-top: 14px;
  border-top: 1px solid var(--line);
}
</style>
