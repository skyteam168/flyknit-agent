<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FolderOpen, Keyboard, Loader, TerminalSquare, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { uiLanguages } from '../i18n'
import { setFontScale, setLanguage, setLearning, setNotifications, setTheme, state, toast } from '../store'
import type { ApprovalInfo, StorageInfo, Theme } from '../types'

const { t } = useI18n()
const themes: Theme[] = ['system', 'light', 'dark']
const approvals = ref<ApprovalInfo[]>([])

async function loadApprovals() {
  approvals.value = await bridge.listApprovals().catch(() => [])
}
async function revoke(a: ApprovalInfo) {
  approvals.value = approvals.value.filter((x) => x.id !== a.id)
  await bridge.revokeApproval(a.id).catch(() => {})
}
async function clearAll() {
  approvals.value = []
  await bridge.clearApprovals().catch(() => {})
}
// ---------- 字号 ----------
const FontSteps = [0.85, 0.925, 1, 1.1, 1.25]
const fontIndex = computed({
  get: () => {
    const i = FontSteps.indexOf(FontSteps.reduce((a, b) => (Math.abs(b - state.fontScale) < Math.abs(a - state.fontScale) ? b : a)))
    return i < 0 ? 2 : i
  },
  set: (i: number) => void setFontScale(FontSteps[i]),
})

// ---------- 开机自启 ----------
const autoStart = ref(false)
async function toggleAutoStart(on: boolean) {
  const r = await bridge.setAutoStart(on).catch(() => ({ ok: false, message: '设置失败', enabled: autoStart.value }))
  autoStart.value = r.enabled
  if (!r.ok && r.message) toast(r.message)
}

// ---------- 网络代理 ----------
const proxyMode = ref('system')
const proxyUrl = ref('')
const proxyUser = ref('')
const proxyPassword = ref('')
const proxyTesting = ref(false)

async function saveProxy() {
  const r = await bridge
    .setProxy(proxyMode.value, proxyUrl.value, proxyUser.value, proxyPassword.value)
    .catch(() => ({ ok: false, message: '保存失败' }))
  if (!r.ok) {
    toast(r.message)
    return
  }
  toast(t('settings.proxySaved'))
}

async function testProxy() {
  proxyTesting.value = true
  try {
    const r = await bridge.testProxy().catch(() => ({ ok: false, message: '测试失败' }))
    toast(r.ok ? t('settings.proxyOk') : t('settings.proxyFail', { msg: r.message }))
  } finally {
    proxyTesting.value = false
  }
}

// ---------- 存储 ----------
const storage = ref<StorageInfo | null>(null)
const storageLoading = ref(true)

function gb(bytes: number) {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`
  return `${(bytes / 1024).toFixed(0)} KB`
}

/** 数据目录占整块磁盘的百分比，用来画那条进度条 */
const diskPercent = computed(() => {
  const s = storage.value
  if (!s || !s.diskTotal) return 0
  return Math.max(0.3, (s.diskUsed / s.diskTotal) * 100)
})

async function changeWorkspace() {
  // addWorkspace 不带参数时会弹系统的选文件夹对话框，并把选中的目录加进工作区列表
  const picked = await bridge.addWorkspace().catch(() => null)
  if (!picked) return
  await bridge.setDefaultWorkspace(picked).catch(() => {})
  if (state.app) state.app.defaultWorkspace = picked
  storage.value = await bridge.storageInfo().catch(() => storage.value)
  toast(t('settings.workspaceChanged'))
}

onMounted(async () => {
  await loadApprovals()
  autoStart.value = state.app?.autoStart ?? false
  proxyMode.value = state.app?.proxyMode ?? 'system'
  proxyUrl.value = state.app?.proxyUrl ?? ''
  proxyUser.value = state.app?.proxyUser ?? ''
  try {
    storage.value = await bridge.storageInfo()
  } catch {
    storage.value = null
  } finally {
    storageLoading.value = false
  }
})
</script>

<template>
  <div class="scrim" @mousedown.self="state.settingsOpen = false" @keydown.esc="state.settingsOpen = false">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('settings.title')">
      <h2>{{ t('settings.title') }}</h2>

      <div class="cols">
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

      <section class="toggles">
        <label class="toggle">
          <span>
            <strong>{{ t('settings.notifications') }}</strong>
            <small>{{ t('settings.notificationsHint') }}</small>
          </span>
          <input type="checkbox" :checked="state.app?.notifications ?? true" @change="setNotifications(($event.target as HTMLInputElement).checked)" />
        </label>
        <label class="toggle">
          <span>
            <strong>{{ t('ui.memory.learning') }}</strong>
            <small>{{ t('ui.memory.learningHint') }}</small>
          </span>
          <input type="checkbox" :checked="state.app?.learning ?? true" @change="setLearning(($event.target as HTMLInputElement).checked)" />
        </label>
        <label class="row-select">
          <span>
            <strong>{{ t('settings.sound') }}</strong>
            <small>{{ t('settings.soundHint') }}</small>
          </span>
          <select
            :value="state.app?.notificationSound ?? 'none'"
            @change="bridge.setNotificationSound(($event.target as HTMLSelectElement).value); state.app && (state.app.notificationSound = ($event.target as HTMLSelectElement).value)"
          >
            <option value="none">{{ t('settings.soundNone') }}</option>
            <option value="soft">{{ t('settings.soundSoft') }}</option>
            <option value="alert">{{ t('settings.soundAlert') }}</option>
          </select>
        </label>
      </section>

      <section>
        <h3>{{ t('settings.memory') }}</h3>
        <p class="hint">{{ t('settings.memoryHint') }}</p>
        <button type="button" class="btn" @click="bridge.openMemoryFolder()">
          <FolderOpen :size="15" /> {{ t('settings.openMemory') }}
        </button>
      </section>

      <section>
        <h3>{{ t('settings.fontSize') }}</h3>
        <div class="font-row">
          <span class="tick">{{ t('settings.fontSmall') }}</span>
          <input v-model.number="fontIndex" type="range" min="0" max="4" step="1" :aria-label="t('settings.fontSize')" />
          <span class="tick">{{ t('settings.fontLarge') }}</span>
        </div>
        <p class="hint">{{ t('settings.fontHint') }}</p>
      </section>

      <section>
        <h3>{{ t('settings.shortcuts') }}</h3>
        <p class="hint">{{ t('settings.shortcutsHint') }}</p>
        <button type="button" class="btn" @click="state.shortcutsOpen = true">
          <Keyboard :size="15" /> {{ t('settings.openShortcuts') }}
        </button>
      </section>
      </div>

      <section class="wide">
        <h3>{{ t('settings.startup') }}</h3>
        <label class="toggle">
          <span>
            <strong>{{ t('settings.autoStart') }}</strong>
            <small>{{ t('settings.autoStartHint') }}</small>
          </span>
          <input type="checkbox" :checked="autoStart" @change="toggleAutoStart(($event.target as HTMLInputElement).checked)" />
        </label>
      </section>

      <section class="wide">
        <h3>{{ t('settings.proxy') }}</h3>
        <p class="hint">{{ t('settings.proxyHint') }}</p>
        <div class="proxy">
          <select v-model="proxyMode" :aria-label="t('settings.proxyMode')" @change="saveProxy">
            <option value="direct">{{ t('settings.proxyDirect') }}</option>
            <option value="system">{{ t('settings.proxySystem') }}</option>
            <option value="manual">{{ t('settings.proxyManual') }}</option>
          </select>
          <template v-if="proxyMode === 'manual'">
            <input v-model="proxyUrl" type="text" placeholder="http://10.0.0.8:8080" @blur="saveProxy" />
            <input v-model="proxyUser" type="text" :placeholder="t('settings.proxyUser')" @blur="saveProxy" />
            <input v-model="proxyPassword" type="password" :placeholder="t('settings.proxyPassword')" @blur="saveProxy" />
          </template>
          <button type="button" class="btn" :disabled="proxyTesting" @click="testProxy">
            <Loader v-if="proxyTesting" :size="14" class="spin" />
            {{ proxyTesting ? t('settings.proxyTesting') : t('settings.proxyTest') }}
          </button>
        </div>
      </section>

      <section class="wide">
        <h3>{{ t('settings.storage') }}</h3>
        <p v-if="storageLoading" class="hint">{{ t('settings.storageLoading') }}</p>
        <template v-else-if="storage">
          <div class="store-card">
            <div class="store-head">
              <strong>{{ t('settings.dataDir') }}</strong>
              <span>{{ t('settings.storageUsed', { size: gb(storage.bytes), p: ((storage.bytes / (storage.diskTotal || 1)) * 100).toFixed(2) }) }}</span>
            </div>
            <div class="track"><i :style="{ width: diskPercent + '%' }" /></div>
            <div class="store-legend">
              <span class="dot app" />{{ t('settings.appUses', { size: gb(storage.bytes) }) }}
              <span class="dot used" />{{ t('settings.diskUsed', { size: gb(storage.diskUsed) }) }}
              <span class="dot free" />{{ t('settings.diskFree', { size: gb(storage.diskFree) }) }}
              <button type="button" class="link" @click="bridge.openDataFolder()">
                <FolderOpen :size="14" /> {{ t('settings.openFolder') }}
              </button>
            </div>
          </div>

          <h4 class="sub">{{ t('settings.workspaceTitle') }}</h4>
          <p class="hint">{{ t('settings.workspaceHint') }}</p>
          <div class="path-row">
            <code>{{ state.app?.defaultWorkspace ?? storage.workspace }}</code>
            <button type="button" class="btn" @click="changeWorkspace">{{ t('settings.change') }}</button>
          </div>
        </template>
      </section>

      <section class="wide">
        <h3 class="row-head">
          <span>{{ t('settings.approvals') }}</span>
          <button v-if="approvals.length" type="button" class="link" @click="clearAll">{{ t('settings.clearAll') }}</button>
        </h3>
        <p class="hint">{{ t('settings.approvalsHint') }}</p>
        <p v-if="approvals.length === 0" class="empty">{{ t('settings.approvalsEmpty') }}</p>
        <ul v-else class="approvals">
          <li v-for="a in approvals" :key="a.id">
            <TerminalSquare :size="15" class="ico" />
            <span class="cmd">
              <code :title="a.display">{{ a.prefix }} *</code>
              <small>
                <template v-if="a.scope && a.scope !== '*'">{{ t('settings.ruleScope', { path: a.scope }) }}</template>
                <template v-else>{{ t('settings.ruleAnyWorkspace') }}</template>
                <template v-if="a.uses"> · {{ t('settings.uses', { n: a.uses }) }}</template>
              </small>
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
.font-row {
  display: flex;
  gap: 12px;
  align-items: center;
}
.font-row input[type='range'] {
  flex: 1;
  accent-color: var(--accent, var(--indigo));
}
.tick {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.proxy {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}
.proxy select,
.proxy input {
  padding: 7px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: inherit;
  font: inherit;
  font-size: var(--t-sm);
}
.proxy input {
  min-width: 190px;
}
.spin {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
.store-card {
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth-sunk, var(--cloth));
}
.store-head {
  display: flex;
  justify-content: space-between;
  font-size: var(--t-sm);
}
.store-head span {
  color: var(--ink-soft);
}
.track {
  margin: 10px 0 8px;
  height: 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
  overflow: hidden;
}
.track i {
  display: block;
  height: 100%;
  border-radius: 999px;
  background: var(--thread, var(--indigo));
}
.store-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 14px;
  align-items: center;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  margin-right: 4px;
  border-radius: 50%;
}
.dot.app {
  background: var(--thread, var(--indigo));
}
.dot.used {
  background: color-mix(in srgb, var(--ink) 35%, transparent);
}
.dot.free {
  background: color-mix(in srgb, var(--ink) 15%, transparent);
}
.store-legend .link {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  margin-left: auto;
  border: 0;
  background: transparent;
  color: var(--indigo);
  font-size: var(--t-xs);
  cursor: pointer;
}
.sub {
  margin: 16px 0 4px;
  font-size: var(--t-sm);
}
.path-row {
  display: flex;
  gap: 8px;
  align-items: center;
}
.path-row code {
  flex: 1;
  overflow: hidden;
  padding: 8px 11px;
  border: 1px solid var(--line);
  border-radius: 8px;
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.row-select {
  display: flex;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
  padding: 12px 14px;
  border-radius: var(--r-lg);
  background: var(--cloth-sunk, transparent);
}
.row-select span {
  display: flex;
  flex-direction: column;
}
.row-select small {
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.row-select select {
  padding: 6px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: inherit;
  font: inherit;
  font-size: var(--t-sm);
}
.cols {
  display: grid;
  grid-template-columns: 1fr 1fr;
  column-gap: 32px;
  align-items: start;
}
.wide {
  grid-column: 1 / -1;
}
@media (max-width: 720px) {
  .font-row {
  display: flex;
  gap: 12px;
  align-items: center;
}
.font-row input[type='range'] {
  flex: 1;
  accent-color: var(--accent, var(--indigo));
}
.tick {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.proxy {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}
.proxy select,
.proxy input {
  padding: 7px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: inherit;
  font: inherit;
  font-size: var(--t-sm);
}
.proxy input {
  min-width: 190px;
}
.spin {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
.store-card {
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth-sunk, var(--cloth));
}
.store-head {
  display: flex;
  justify-content: space-between;
  font-size: var(--t-sm);
}
.store-head span {
  color: var(--ink-soft);
}
.track {
  margin: 10px 0 8px;
  height: 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
  overflow: hidden;
}
.track i {
  display: block;
  height: 100%;
  border-radius: 999px;
  background: var(--thread, var(--indigo));
}
.store-legend {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 14px;
  align-items: center;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  margin-right: 4px;
  border-radius: 50%;
}
.dot.app {
  background: var(--thread, var(--indigo));
}
.dot.used {
  background: color-mix(in srgb, var(--ink) 35%, transparent);
}
.dot.free {
  background: color-mix(in srgb, var(--ink) 15%, transparent);
}
.store-legend .link {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  margin-left: auto;
  border: 0;
  background: transparent;
  color: var(--indigo);
  font-size: var(--t-xs);
  cursor: pointer;
}
.sub {
  margin: 16px 0 4px;
  font-size: var(--t-sm);
}
.path-row {
  display: flex;
  gap: 8px;
  align-items: center;
}
.path-row code {
  flex: 1;
  overflow: hidden;
  padding: 8px 11px;
  border: 1px solid var(--line);
  border-radius: 8px;
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.row-select {
  display: flex;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
  padding: 12px 14px;
  border-radius: var(--r-lg);
  background: var(--cloth-sunk, transparent);
}
.row-select span {
  display: flex;
  flex-direction: column;
}
.row-select small {
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.row-select select {
  padding: 6px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: inherit;
  font: inherit;
  font-size: var(--t-sm);
}
.cols {
    grid-template-columns: 1fr;
  }
}
.dialog {
  width: min(840px, 100%);
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
.toggles {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.toggle {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 12px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  cursor: pointer;
}
.toggle span {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.toggle strong {
  font-size: var(--t-sm);
  font-weight: 500;
}
.toggle small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.toggle input {
  width: 18px;
  height: 18px;
  accent-color: var(--indigo);
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
  font-size: calc(11px * var(--font-scale));
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
