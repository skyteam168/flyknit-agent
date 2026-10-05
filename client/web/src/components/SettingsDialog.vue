<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FolderOpen, TerminalSquare, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { uiLanguages } from '../i18n'
import { setLanguage, setTheme, state } from '../store'
import type { ApprovalInfo, Theme } from '../types'

const { t } = useI18n()
const themes: Theme[] = ['system', 'light', 'dark']
const approvals = ref<ApprovalInfo[]>([])

async function loadApprovals() {
  approvals.value = await bridge.listApprovals().catch(() => [])
}
async function revoke(a: ApprovalInfo) {
  approvals.value = approvals.value.filter((x) => x.key !== a.key)
  await bridge.revokeApproval(a.key).catch(() => {})
}
async function clearAll() {
  approvals.value = []
  await bridge.clearApprovals().catch(() => {})
}
onMounted(loadApprovals)
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

      <section>
        <h3 class="row-head">
          <span>{{ t('settings.approvals') }}</span>
          <button v-if="approvals.length" type="button" class="link" @click="clearAll">{{ t('settings.clearAll') }}</button>
        </h3>
        <p class="hint">{{ t('settings.approvalsHint') }}</p>
        <p v-if="approvals.length === 0" class="empty">{{ t('settings.approvalsEmpty') }}</p>
        <ul v-else class="approvals">
          <li v-for="a in approvals" :key="a.key">
            <TerminalSquare :size="15" class="ico" />
            <span class="cmd">
              <code :title="a.display">{{ a.display }}</code>
              <small v-if="a.uses">{{ t('settings.uses', { n: a.uses }) }}</small>
            </span>
            <button type="button" class="mini" :title="t('settings.revoke')" @click="revoke(a)"><X :size="14" /></button>
          </li>
        </ul>
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
  width: min(500px, 100%);
  max-height: calc(100vh - 32px);
  overflow-y: auto;
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
.row-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.link {
  font-size: var(--t-xs);
  font-weight: 500;
  color: var(--indigo);
}
.empty {
  margin: 0;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
.approvals {
  margin: 0;
  padding: 4px;
  list-style: none;
  max-height: 180px;
  overflow-y: auto;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
}
.approvals li {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 6px 6px 8px;
  border-radius: 6px;
}
.approvals li:hover {
  background: var(--chip);
}
.approvals .ico {
  flex: none;
  color: var(--ink-faint);
}
.cmd {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}
.cmd code {
  font-family: var(--font-code);
  font-size: var(--t-xs);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.cmd small {
  font-size: 11px;
  color: var(--ink-faint);
}
.mini {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.mini:hover {
  background: var(--line);
  color: var(--ink);
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
