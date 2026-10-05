<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Building2,
  ChevronDown,
  CloudDownload,
  FileCode2,
  FolderOpen,
  Link2,
  Loader2,
  Package,
  Puzzle,
  RefreshCw,
  Sparkles,
  Trash2,
  TriangleAlert,
  Upload,
  User,
  X,
} from '@lucide/vue'
import { bridge } from '../bridge'
import { loadSkills, state, toast } from '../store'
import type { LibrarySkill, SkillInfo } from '../types'

const emit = defineEmits<{ use: [name: string] }>()
const { t } = useI18n()

type Tab = 'installed' | 'library' | 'add'
const tab = ref<Tab>('installed')
const expanded = ref<string | null>(null)
const busy = ref('')
const url = ref('')
const library = ref<LibrarySkill[] | null>(null)
const libraryError = ref('')
const removing = ref<SkillInfo | null>(null)

const installed = computed(() => state.skills)
const kb = (n: number) => (n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`)

async function loadLibrary() {
  busy.value = 'library'
  libraryError.value = ''
  try {
    library.value = await bridge.skillLibrary()
  } catch (e) {
    library.value = []
    libraryError.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = ''
  }
}

function openTab(next: Tab) {
  tab.value = next
  if (next === 'library' && library.value === null) void loadLibrary()
}

/** 安装结果统一提示：成功装了几个、有什么要注意的 */
function report(outcome: Awaited<ReturnType<typeof bridge.installSkill>>) {
  if (!outcome) return
  if (outcome.ok) {
    toast(t('ui.skills.installed', { names: outcome.installed.join('、') }))
    if (outcome.warnings.length) setTimeout(() => toast(outcome.warnings[0]), 2600)
  } else {
    toast(outcome.messages[0] ?? t('ui.skills.installFailed'))
  }
  void loadSkills()
}

async function installFile() {
  busy.value = 'file'
  try {
    report(await bridge.installSkill())
  } catch (e) {
    toast(String(e))
  } finally {
    busy.value = ''
  }
}

async function installFolder() {
  busy.value = 'folder'
  try {
    report(await bridge.installSkillFolder())
  } catch (e) {
    toast(String(e))
  } finally {
    busy.value = ''
  }
}

async function installUrl() {
  const link = url.value.trim()
  if (!link) return
  busy.value = 'url'
  try {
    report(await bridge.installSkillFromUrl(link))
    url.value = ''
  } catch (e) {
    toast(String(e))
  } finally {
    busy.value = ''
  }
}

async function installFromLibrary(s: LibrarySkill) {
  busy.value = s.name
  try {
    report(await bridge.installSkillFromLibrary(s.name))
    await loadLibrary()
  } catch (e) {
    toast(String(e))
  } finally {
    busy.value = ''
  }
}

async function toggle(s: SkillInfo) {
  const r = await bridge.setSkillEnabled(s.name, !s.enabled).catch(() => null)
  if (r && !r.ok) toast(r.message)
  await loadSkills()
}

async function confirmRemove() {
  const s = removing.value
  removing.value = null
  if (!s) return
  const r = await bridge.uninstallSkill(s.name).catch(() => null)
  if (r && !r.ok) toast(r.message)
  await loadSkills()
  if (library.value) void loadLibrary()
}

onMounted(loadSkills)
const close = () => (state.skillsOpen = false)
</script>

<template>
  <div class="scrim" @mousedown.self="close" @keydown.esc="close">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.skills.title')">
      <header>
        <div class="badge"><Puzzle :size="20" /></div>
        <div class="head-text">
          <h2>{{ t('ui.skills.title') }}</h2>
          <p>{{ t('ui.skills.hint') }}</p>
        </div>
        <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="close"><X :size="18" /></button>
      </header>

      <nav class="tabs" role="tablist">
        <button type="button" role="tab" :aria-selected="tab === 'installed'" :class="{ on: tab === 'installed' }" @click="openTab('installed')">
          <Package :size="15" /><span>{{ t('ui.skills.tabs.installed') }}</span><em>{{ installed.length }}</em>
        </button>
        <button type="button" role="tab" :aria-selected="tab === 'library'" :class="{ on: tab === 'library' }" @click="openTab('library')">
          <Building2 :size="15" /><span>{{ t('ui.skills.tabs.library') }}</span>
        </button>
        <button type="button" role="tab" :aria-selected="tab === 'add'" :class="{ on: tab === 'add' }" @click="openTab('add')">
          <Upload :size="15" /><span>{{ t('ui.skills.tabs.add') }}</span>
        </button>
      </nav>

      <div class="body">
        <!-- 已安装 -->
        <template v-if="tab === 'installed'">
          <p v-if="installed.length === 0" class="empty">{{ t('ui.skills.empty') }}</p>
          <div v-for="s in installed" :key="s.name" class="card" :class="{ off: !s.enabled, open: expanded === s.name }">
            <div class="row">
              <span class="icons">
                <component :is="s.organization ? Building2 : s.learned ? Sparkles : User" :size="16" />
              </span>
              <span class="main">
                <strong>
                  {{ s.name }}
                  <small v-if="s.version" class="ver">v{{ s.version }}</small>
                </strong>
                <small class="desc">{{ s.description }}</small>
              </span>
              <span class="state" :class="{ on: s.enabled }">
                <i /> {{ s.enabled ? t('ui.skills.on') : t('ui.skills.off') }}
              </span>
              <button
                type="button"
                class="switch"
                role="switch"
                :aria-checked="s.enabled"
                :class="{ on: s.enabled }"
                :disabled="s.required"
                :title="s.required ? t('ui.skills.requiredHint') : t('ui.skills.toggle')"
                @click="toggle(s)"
              >
                <i />
              </button>
              <button type="button" class="chev" :aria-expanded="expanded === s.name" @click="expanded = expanded === s.name ? null : s.name">
                <ChevronDown :size="16" :class="{ up: expanded === s.name }" />
              </button>
            </div>

            <div v-if="expanded === s.name" class="detail">
              <dl>
                <dt>{{ t('ui.skills.fields.source') }}</dt>
                <dd>
                  {{ s.organization ? t('ui.skills.org') : s.learned ? t('ui.skills.learnedSkill') : t('ui.skills.personal') }}
                  <span v-if="s.required" class="tag req">{{ t('ui.skills.required') }}</span>
                </dd>
                <template v-if="s.origin">
                  <dt>{{ t('ui.skills.fields.origin') }}</dt>
                  <dd class="wrap">{{ s.origin }}</dd>
                </template>
                <template v-if="s.author">
                  <dt>{{ t('ui.skills.fields.author') }}</dt>
                  <dd>{{ s.author }}<span v-if="s.license"> · {{ s.license }}</span></dd>
                </template>
                <dt>{{ t('ui.skills.fields.content') }}</dt>
                <dd>{{ t('ui.skills.fileCount', { n: s.files.length + 1 }) }} · {{ kb(s.bytes) }}</dd>
                <dt>{{ t('ui.skills.fields.status') }}</dt>
                <dd>{{ s.enabled ? t('ui.skills.statusOn') : t('ui.skills.statusOff') }}</dd>
              </dl>

              <p v-if="s.scripts.length" class="scripts">
                <FileCode2 :size="14" />
                {{ t('ui.skills.scripts', { n: s.scripts.length }) }}
                <span class="files">{{ s.scripts.join('、') }}</span>
              </p>

              <div class="actions">
                <button type="button" class="btn" @click="emit('use', s.name)">{{ t('ui.skills.useInChat') }}</button>
                <button type="button" class="btn" @click="bridge.openSkillsFolder(s.directory)"><FolderOpen :size="15" /> {{ t('ui.skills.openSkill') }}</button>
                <span class="spacer" />
                <button v-if="!s.organization" type="button" class="btn danger-text" @click="removing = s">
                  <Trash2 :size="15" /> {{ t('ui.skills.uninstall') }}
                </button>
              </div>
            </div>
          </div>
        </template>

        <!-- 公司技能库 -->
        <template v-else-if="tab === 'library'">
          <div class="bar">
            <p class="tab-hint">{{ t('ui.skills.libraryHint') }}</p>
            <button type="button" class="icon-btn" :title="t('ui.model.title')" @click="loadLibrary"><RefreshCw :size="15" /></button>
          </div>
          <p v-if="busy === 'library'" class="empty"><Loader2 :size="18" class="spin" /> {{ t('ui.skills.loading') }}</p>
          <p v-else-if="libraryError" class="empty">{{ t('ui.skills.libraryError', { msg: libraryError }) }}</p>
          <p v-else-if="library && library.length === 0" class="empty">{{ t('ui.skills.libraryEmpty') }}</p>
          <div v-for="s in library ?? []" :key="s.name" class="card">
            <div class="row">
              <span class="icons"><Building2 :size="16" /></span>
              <span class="main">
                <strong>{{ s.name }}<small v-if="s.version" class="ver">v{{ s.version }}</small></strong>
                <small class="desc">{{ s.description }}</small>
              </span>
              <span v-if="s.required" class="tag req">{{ t('ui.skills.required') }}</span>
              <button
                type="button"
                class="btn primary"
                :disabled="busy === s.name || (s.installed && !s.updatable)"
                @click="installFromLibrary(s)"
              >
                <component :is="busy === s.name ? Loader2 : CloudDownload" :size="15" :class="{ spin: busy === s.name }" />
                {{ s.updatable ? t('ui.skills.update') : s.installed ? t('ui.skills.installedShort') : t('ui.skills.install') }}
              </button>
            </div>
          </div>
        </template>

        <!-- 添加 -->
        <template v-else>
          <p class="tab-hint">{{ t('ui.skills.addHint') }}</p>
          <div class="add-row">
            <div class="add-text">
              <strong>{{ t('ui.skills.fromFile') }}</strong>
              <small>{{ t('ui.skills.fromFileHint') }}</small>
            </div>
            <button type="button" class="btn" :disabled="busy === 'file'" @click="installFile">
              <component :is="busy === 'file' ? Loader2 : Upload" :size="15" :class="{ spin: busy === 'file' }" />
              {{ t('ui.skills.choose') }}
            </button>
          </div>
          <div class="add-row">
            <div class="add-text">
              <strong>{{ t('ui.skills.fromFolder') }}</strong>
              <small>{{ t('ui.skills.fromFolderHint') }}</small>
            </div>
            <button type="button" class="btn" :disabled="busy === 'folder'" @click="installFolder">
              <component :is="busy === 'folder' ? Loader2 : FolderOpen" :size="15" :class="{ spin: busy === 'folder' }" />
              {{ t('ui.skills.choose') }}
            </button>
          </div>
          <div class="add-row column">
            <div class="add-text">
              <strong>{{ t('ui.skills.fromUrl') }}</strong>
              <small>{{ t('ui.skills.fromUrlHint') }}</small>
            </div>
            <form class="url" @submit.prevent="installUrl">
              <Link2 :size="15" />
              <input v-model="url" placeholder="https://github.com/owner/repo" :disabled="busy === 'url'" />
              <button type="submit" class="btn primary" :disabled="!url.trim() || busy === 'url'">
                <component :is="busy === 'url' ? Loader2 : CloudDownload" :size="15" :class="{ spin: busy === 'url' }" />
                {{ t('ui.skills.install') }}
              </button>
            </form>
          </div>
          <p class="safety"><TriangleAlert :size="14" /> {{ t('ui.skills.safety') }}</p>
        </template>
      </div>

      <footer>
        <button type="button" class="btn" @click="bridge.openSkillsFolder()"><FolderOpen :size="15" /> {{ t('ui.skills.openFolder') }}</button>
        <span class="spacer" />
        <button type="button" class="btn primary" @click="close">{{ t('settings.close') }}</button>
      </footer>
    </div>

    <div v-if="removing" class="scrim inner" @mousedown.self="removing = null">
      <div class="confirm" role="alertdialog">
        <h3>{{ t('ui.skills.uninstallTitle', { name: removing.name }) }}</h3>
        <p>{{ t('ui.skills.uninstallBody') }}</p>
        <div class="actions">
          <button type="button" class="btn" @click="removing = null">{{ t('ui.fullAccess.cancel') }}</button>
          <button type="button" class="btn danger" @click="confirmRemove">{{ t('ui.skills.uninstall') }}</button>
        </div>
      </div>
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
.scrim.inner {
  z-index: 70;
}
.dialog {
  display: flex;
  flex-direction: column;
  width: min(720px, 100%);
  height: min(640px, calc(100vh - 32px));
  padding: 22px 24px 18px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  animation: pop 160ms ease-out;
}
header {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 14px;
}
.badge {
  display: grid;
  place-items: center;
  flex: none;
  width: 40px;
  height: 40px;
  border-radius: 12px;
  background: var(--indigo-wash);
  color: var(--indigo);
}
.head-text {
  flex: 1;
  min-width: 0;
}
h2 {
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 600;
}
.head-text p {
  margin: 2px 0 0;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
.tabs {
  display: flex;
  gap: 2px;
  border-bottom: 1px solid var(--line);
}
.tabs button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 38px;
  padding: 0 12px;
  border-bottom: 2px solid transparent;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  white-space: nowrap;
}
.tabs button.on {
  border-bottom-color: var(--indigo);
  color: var(--ink);
  font-weight: 500;
}
.tabs em {
  padding: 0 6px;
  border-radius: 8px;
  background: var(--chip);
  color: var(--ink-faint);
  font-style: normal;
  font-size: 11px;
}
.body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 12px 2px;
}
.bar {
  display: flex;
  align-items: center;
  gap: 8px;
}
.tab-hint {
  flex: 1;
  margin: 0 0 10px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.5;
}
.empty {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  margin: 30px 0;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.card {
  margin-bottom: 8px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  transition: border-color 120ms;
}
.card.open {
  border-color: var(--line-strong);
}
.card.off .main {
  opacity: 0.6;
}
.row {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
}
.icons {
  display: grid;
  place-items: center;
  flex: none;
  width: 30px;
  height: 30px;
  border-radius: 8px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
}
.main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.main strong {
  font-size: var(--t-sm);
  font-weight: 600;
  font-family: var(--font-code);
}
.ver {
  margin-left: 6px;
  color: var(--ink-faint);
  font-weight: 400;
  font-size: var(--t-xs);
}
.desc {
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.45;
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.state {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex: none;
  padding: 2px 9px;
  border-radius: 10px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
  font-size: 11px;
}
.state i {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--ink-faint);
}
.state.on {
  background: var(--thread-wash);
  color: var(--thread);
}
.state.on i {
  background: var(--thread);
}
.switch {
  position: relative;
  flex: none;
  width: 38px;
  height: 21px;
  border-radius: 11px;
  background: var(--line-strong);
  transition: background 140ms;
}
.switch i {
  position: absolute;
  top: 3px;
  left: 3px;
  width: 15px;
  height: 15px;
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.2);
  transition: transform 140ms;
}
.switch.on {
  background: var(--indigo);
}
.switch.on i {
  transform: translateX(17px);
}
.switch:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.chev {
  display: grid;
  place-items: center;
  flex: none;
  width: 26px;
  height: 26px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.chev:hover {
  background: var(--chip);
}
.chev svg {
  transition: transform 150ms;
}
.chev .up {
  transform: rotate(180deg);
}
.detail {
  padding: 2px 12px 12px 52px;
  border-top: 1px solid var(--line);
  margin-top: 2px;
}
dl {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 4px 14px;
  margin: 10px 0;
  font-size: var(--t-xs);
}
dt {
  color: var(--ink-faint);
}
dd {
  margin: 0;
  color: var(--ink-soft);
}
dd.wrap {
  word-break: break-all;
}
.tag {
  margin-left: 6px;
  padding: 1px 7px;
  border-radius: 9px;
  font-size: 11px;
}
.tag.req {
  background: var(--amber-wash);
  color: var(--amber);
}
.scripts {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin: 0 0 10px;
  padding: 7px 10px;
  border-radius: var(--r-sm);
  background: var(--amber-wash);
  color: var(--amber);
  font-size: var(--t-xs);
}
.scripts .files {
  color: var(--ink-soft);
  font-family: var(--font-code);
}
.actions {
  display: flex;
  align-items: center;
  gap: 8px;
}
.spacer {
  flex: 1;
}
.danger-text {
  color: var(--red);
}
.add-row {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 8px;
  padding: 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
}
.add-row.column {
  flex-direction: column;
  align-items: stretch;
  gap: 10px;
}
.add-text {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 3px;
}
.add-text strong {
  font-size: var(--t-sm);
  font-weight: 500;
}
.add-text small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.5;
}
.url {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 0 10px;
  height: 38px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  color: var(--ink-faint);
}
.url:focus-within {
  border-color: var(--indigo);
}
.url input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: 0;
  background: transparent;
  font-size: var(--t-sm);
  color: var(--ink);
}
.url .btn {
  height: 28px;
}
.safety {
  display: flex;
  align-items: flex-start;
  gap: 7px;
  margin: 14px 2px 0;
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.6;
}
footer {
  display: flex;
  align-items: center;
  gap: 8px;
  padding-top: 14px;
  border-top: 1px solid var(--line);
}
.confirm {
  width: min(400px, 100%);
  padding: 22px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.confirm h3 {
  margin: 0 0 8px;
  font-size: var(--t-lg);
  font-weight: 600;
}
.confirm p {
  margin: 0 0 18px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.confirm .actions {
  justify-content: flex-end;
}
.spin {
  animation: spin 900ms linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
@keyframes pop {
  from {
    opacity: 0;
    transform: translateY(6px) scale(0.98);
  }
}
</style>
