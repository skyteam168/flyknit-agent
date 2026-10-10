<script setup lang="ts">
import { computed, onMounted, ref, watch, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Bell,
  CircleArrowUp,
  CircleHelp,
  Database,
  FolderOpen,
  Globe,
  Info,
  Keyboard,
  Lightbulb,
  Loader,
  Lock,
  LogOut,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  TerminalSquare,
  Volume2,
  X,
} from '@lucide/vue'
import { bridge } from '../bridge'
import { uiLanguages } from '../i18n'
import { setFontScale, setLanguage, setLearning, setMaxSteps, setNotificationSound, setNotifications, setTheme, state, toast } from '../store'
import type { ApprovalInfo, KeepAwakeMode, StorageInfo, Theme } from '../types'
import AuditLog from './AuditLog.vue'
import ConfirmDialog from './ConfirmDialog.vue'
import FeedbackDialog from './FeedbackDialog.vue'
import NetworkCheck from './NetworkCheck.vue'
import PersonalizePanel from './PersonalizePanel.vue'
import SecurityPanel from './SecurityPanel.vue'
import ShortcutsPanel from './ShortcutsPanel.vue'

const { t } = useI18n()
const themes: Theme[] = ['system', 'light', 'dark']

// ---------- 左侧菜单 ----------
type Page = 'general' | 'notify' | 'shortcuts' | 'network' | 'personalize' | 'memory' | 'data' | 'security' | 'about'
const nav: { group: string; pages: { id: Page; icon: Component; label: string }[] }[] = [
  {
    group: 'settings.nav.groupSettings',
    pages: [
      { id: 'general', icon: SlidersHorizontal, label: 'settings.nav.general' },
      { id: 'notify', icon: Bell, label: 'settings.nav.notify' },
      { id: 'shortcuts', icon: Keyboard, label: 'settings.shortcuts' },
      { id: 'network', icon: Globe, label: 'settings.nav.network' },
    ],
  },
  {
    group: 'settings.nav.groupFeatures',
    pages: [
      { id: 'personalize', icon: Sparkles, label: 'settings.nav.personalize' },
      { id: 'memory', icon: Lightbulb, label: 'settings.nav.memory' },
    ],
  },
  {
    group: 'settings.nav.groupData',
    pages: [
      { id: 'data', icon: Database, label: 'settings.nav.data' },
      { id: 'security', icon: ShieldCheck, label: 'ui.security.title' },
    ],
  },
]
const page = ref<Page>('general')
/** 安全中心里点了「查看全部」，右侧换成审计明细；切到别的页再回来时回到安全中心首页 */
const auditOpen = ref(false)
const feedbackOpen = ref(false)

/** 退出登录：先确认（有任务在跑要说清楚会被中断），再交给宿主重启到登录窗口 */
const logoutAsk = ref(false)
const loggingOut = ref(false)
const anyBusy = computed(() => Object.values(state.byId).some((c) => c.busy))
async function logout() {
  logoutAsk.value = false
  loggingOut.value = true
  try {
    await bridge.logout()
  } catch (e) {
    loggingOut.value = false
    toast(e instanceof Error ? e.message : String(e))
  }
}

/** 检查更新：马上问服务器，结果用一句话告诉用户；有新版本就在后台下载，顶上的更新条会跟着变 */
const checkingUpdate = ref(false)
async function checkUpdate() {
  if (checkingUpdate.value) return
  checkingUpdate.value = true
  try {
    const r = await bridge.checkUpdate()
    if (r.outcome === 'failed') {
      toast(r.message === 'timeout' ? t('settings.updateResult.timeout') : t('settings.updateResult.failed', { msg: r.message }))
    } else {
      toast(t(`settings.updateResult.${r.outcome}`, { v: r.outcome === 'upToDate' ? r.current : r.version }))
    }
  } catch (e) {
    toast(t('settings.updateResult.failed', { msg: e instanceof Error ? e.message : String(e) }))
  } finally {
    checkingUpdate.value = false
  }
}
const content = ref<HTMLElement>()
watch(page, () => (auditOpen.value = false))
function openAudit() {
  auditOpen.value = true
  content.value?.scrollTo({ top: 0 })
}

function close() {
  state.settingsOpen = false
}

// ---------- 自动执行规则 ----------
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

// ---------- 锁屏运行 ----------
const keepAwakeModes: KeepAwakeMode[] = ['off', 'tasks', 'awake', 'screen']
const keepAwake = ref<KeepAwakeMode>('tasks')
function keepAwakeLocked(mode: KeepAwakeMode) {
  if (mode === 'off') return false
  if (!(state.app?.keepAwakeAllowed ?? true)) return true
  return mode === 'screen' && !(state.app?.keepScreenAllowed ?? true)
}
async function changeKeepAwake(mode: KeepAwakeMode) {
  const before = keepAwake.value
  keepAwake.value = mode
  const r = await bridge.setKeepAwake(mode).catch(() => ({ ok: false, message: '设置失败', mode: before }))
  keepAwake.value = r.mode
  if (state.app) state.app.keepAwake = r.mode
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
  keepAwake.value = state.app?.keepAwake ?? 'tasks'
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
  <div class="scrim" @mousedown.self="close" @keydown.esc="close">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('settings.title')">
      <nav class="menu">
        <template v-for="g in nav" :key="g.group">
          <h4>{{ t(g.group) }}</h4>
          <button
            v-for="p in g.pages"
            :key="p.id"
            type="button"
            :class="{ on: page === p.id }"
            :aria-current="page === p.id ? 'page' : undefined"
            @click="page = p.id"
          >
            <component :is="p.icon" :size="16" />
            <span>{{ t(p.label) }}</span>
          </button>
        </template>
        <div class="menu-foot">
          <button type="button" @click="feedbackOpen = true">
            <CircleHelp :size="16" />
            <span>{{ t('settings.feedback') }}</span>
          </button>
          <button type="button" :disabled="checkingUpdate" @click="checkUpdate">
            <Loader v-if="checkingUpdate" :size="16" class="spin" />
            <CircleArrowUp v-else :size="16" />
            <span>{{ checkingUpdate ? t('settings.checkingUpdate') : t('settings.checkUpdate') }}</span>
          </button>
          <button type="button" :class="{ on: page === 'about' }" @click="page = 'about'">
            <Info :size="16" />
            <span>{{ t('settings.about') }}</span>
          </button>
        </div>
      </nav>

      <div class="main">
      <div class="bar">
        <button type="button" class="close" :aria-label="t('settings.close')" :title="t('settings.close')" @click="close">
          <X :size="18" />
        </button>
      </div>

      <div ref="content" class="content">
        <!-- 通用 -->
        <template v-if="page === 'general'">
          <h2>{{ t('settings.nav.general') }}</h2>
          <section class="card account">
            <div class="row">
              <span class="avatar">{{ (state.app?.owner || state.app?.userName || '?').slice(0, 1).toUpperCase() }}</span>
              <span class="label">
                <strong>{{ t('settings.signedInAs', { name: state.app?.owner || state.app?.userName || '', machine: state.app?.machineName ?? '' }) }}</strong>
                <small v-if="state.app?.serverUrl">{{ t('settings.server', { url: state.app.serverUrl }) }}</small>
              </span>
              <button type="button" class="btn logout" :disabled="loggingOut" @click="logoutAsk = true">
                <Loader v-if="loggingOut" :size="14" class="spin" />
                <LogOut v-else :size="14" />
                {{ loggingOut ? t('settings.loggingOut') : t('settings.logout') }}
              </button>
            </div>
          </section>
          <section class="card">
            <div class="row">
              <span class="label"><strong>{{ t('settings.language') }}</strong></span>
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
            </div>
            <div class="row">
              <span class="label"><strong>{{ t('settings.theme') }}</strong></span>
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
            </div>
            <div class="row col">
              <span class="label">
                <strong>{{ t('settings.fontSize') }}</strong>
                <small>{{ t('settings.fontHint') }}</small>
              </span>
              <div class="font-row">
                <span class="tick">{{ t('settings.fontSmall') }}</span>
                <input v-model.number="fontIndex" type="range" min="0" max="4" step="1" :aria-label="t('settings.fontSize')" />
                <span class="tick">{{ t('settings.fontLarge') }}</span>
              </div>
            </div>
          </section>

          <section class="card">
            <h3>{{ t('settings.agent') }}</h3>
            <div class="row">
              <span class="label">
                <strong>{{ t('settings.maxSteps') }}</strong>
                <small>{{ t('settings.maxStepsHint') }}</small>
              </span>
              <select
                :value="state.app?.maxSteps ?? 100"
                :aria-label="t('settings.maxSteps')"
                @change="setMaxSteps(Number(($event.target as HTMLSelectElement).value))"
              >
                <option v-for="n in [50, 100, 200]" :key="n" :value="n">{{ t('settings.maxStepsUnit', { n }) }}</option>
              </select>
            </div>
          </section>

          <section class="card">
            <h3>{{ t('settings.startup') }}</h3>
            <label class="row">
              <span class="label">
                <strong>{{ t('settings.autoStart') }}</strong>
                <small>{{ t('settings.autoStartHint') }}</small>
              </span>
              <input type="checkbox" class="switch" :checked="autoStart" @change="toggleAutoStart(($event.target as HTMLInputElement).checked)" />
            </label>
            <div class="row">
              <span class="label">
                <strong>
                  {{ t('settings.keepAwake') }}
                  <Lock v-if="!(state.app?.keepAwakeAllowed ?? true)" :size="12" class="locked" />
                </strong>
                <small>{{ t(`settings.keepAwakeModes.${keepAwake}.desc`) }}</small>
                <small v-if="!(state.app?.keepAwakeAllowed ?? true)">{{ t('ui.security.managedBy') }}</small>
                <small v-else>{{ t('settings.keepAwakeNote') }}</small>
              </span>
              <select :value="keepAwake" :aria-label="t('settings.keepAwake')" @change="changeKeepAwake(($event.target as HTMLSelectElement).value as KeepAwakeMode)">
                <option v-for="m in keepAwakeModes" :key="m" :value="m" :disabled="keepAwakeLocked(m)">
                  {{ t(`settings.keepAwakeModes.${m}.name`) }}{{ keepAwakeLocked(m) ? ' 🔒' : '' }}
                </option>
              </select>
            </div>
          </section>
        </template>

        <!-- 个性化 -->
        <template v-else-if="page === 'personalize'">
          <h2>{{ t('settings.nav.personalize') }}</h2>
          <PersonalizePanel />
        </template>

        <!-- 通知 -->
        <template v-else-if="page === 'notify'">
          <h2>{{ t('settings.nav.notify') }}</h2>
          <section class="card">
            <label class="row">
              <span class="label">
                <strong>
                  {{ t('settings.notifications') }}
                  <Lock v-if="state.app?.notificationsLocked" :size="12" class="locked" />
                </strong>
                <small>{{ state.app?.notificationsLocked ? t('ui.security.managedBy') : t('settings.notificationsHint') }}</small>
              </span>
              <input
                type="checkbox"
                class="switch"
                :checked="state.app?.notifications ?? true"
                :disabled="state.app?.notificationsLocked"
                @change="setNotifications(($event.target as HTMLInputElement).checked)"
              />
            </label>
            <div class="row">
              <span class="label">
                <strong>
                  {{ t('settings.sound') }}
                  <Lock v-if="state.app?.soundLocked" :size="12" class="locked" />
                </strong>
                <small>{{ state.app?.soundLocked ? t('ui.security.managedBy') : t('settings.soundHint') }}</small>
              </span>
              <button
                type="button"
                class="btn preview"
                :disabled="(state.app?.notificationSound ?? 'none') === 'none'"
                :title="t('settings.soundPreview')"
                @click="bridge.previewSound(state.app?.notificationSound ?? 'none')"
              >
                <Volume2 :size="14" /> {{ t('settings.soundPreview') }}
              </button>
              <select
                :value="state.app?.notificationSound ?? 'none'"
                :disabled="state.app?.soundLocked"
                @change="setNotificationSound(($event.target as HTMLSelectElement).value)"
              >
                <option value="none">{{ t('settings.soundNone') }}</option>
                <option value="soft">{{ t('settings.soundSoft') }}</option>
                <option value="alert">{{ t('settings.soundAlert') }}</option>
              </select>
            </div>
          </section>
        </template>

        <!-- 快捷键 -->
        <ShortcutsPanel v-else-if="page === 'shortcuts'" />

        <!-- 网络 -->
        <template v-else-if="page === 'network'">
          <h2>{{ t('settings.nav.network') }}</h2>
          <section class="card">
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
          <NetworkCheck />
        </template>

        <!-- 记忆与进化 -->
        <template v-else-if="page === 'memory'">
          <h2>{{ t('settings.nav.memory') }}</h2>
          <section class="card">
            <label class="row">
              <span class="label">
                <strong>{{ t('ui.memory.learning') }}</strong>
                <small>{{ t('ui.memory.learningHint') }}</small>
              </span>
              <input
                type="checkbox"
                class="switch"
                :checked="state.app?.learning ?? true"
                @change="setLearning(($event.target as HTMLInputElement).checked)"
              />
            </label>
            <div class="row">
              <span class="label">
                <strong>{{ t('settings.memory') }}</strong>
                <small>{{ t('settings.memoryHint') }}</small>
              </span>
              <button type="button" class="btn" @click="bridge.openMemoryFolder()">
                <FolderOpen :size="15" /> {{ t('settings.openMemory') }}
              </button>
            </div>
          </section>
        </template>

        <!-- 数据管理 -->
        <template v-else-if="page === 'data'">
          <h2>{{ t('settings.nav.data') }}</h2>
          <section class="card">
            <h3>{{ t('settings.storage') }}</h3>
            <p v-if="storageLoading" class="hint">{{ t('settings.storageLoading') }}</p>
            <template v-else-if="storage">
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
            </template>
          </section>

          <section v-if="storage" class="card">
            <h3>{{ t('settings.workspaceTitle') }}</h3>
            <p class="hint">{{ t('settings.workspaceHint') }}</p>
            <div class="path-row">
              <code>{{ state.app?.defaultWorkspace ?? storage.workspace }}</code>
              <button type="button" class="btn" @click="changeWorkspace">{{ t('settings.change') }}</button>
            </div>
          </section>
        </template>

        <!-- 安全中心 -->
        <AuditLog v-else-if="page === 'security' && auditOpen" @back="auditOpen = false" />
        <template v-else-if="page === 'security'">
          <SecurityPanel @open-audit="openAudit" />
          <section class="card">
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
        </template>

        <!-- 关于 -->
        <template v-else-if="page === 'about'">
          <h2>{{ t('settings.about') }}</h2>
          <section class="card">
            <div class="row">
              <span class="label"><strong>{{ t('app.name') }}</strong></span>
              <span class="value">{{ t('settings.version', { v: state.app?.version ?? '' }) }}</span>
            </div>
            <div class="row">
              <span class="label"><strong>{{ t('settings.nav.device') }}</strong></span>
              <span class="value">{{ state.app?.userName }}, {{ state.app?.machineName }}</span>
            </div>
          </section>
        </template>
      </div>
      </div>
    </div>
    <Teleport to="body">
      <FeedbackDialog v-if="feedbackOpen" @close="feedbackOpen = false" />
      <ConfirmDialog
        v-if="logoutAsk"
        :title="t('settings.logoutTitle')"
        :body="anyBusy ? t('settings.logoutBusy') : t('settings.logoutBody')"
        :ok="t('settings.logout')"
        danger
        @ok="logout"
        @cancel="logoutAsk = false"
      />
    </Teleport>
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
  position: relative;
  display: grid;
  grid-template-columns: 200px 1fr;
  width: min(940px, 100%);
  height: min(680px, calc(100vh - 32px));
  overflow: hidden;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}

/* ---------- 左侧菜单 ---------- */
.menu {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 18px 12px 12px;
  overflow-y: auto;
  border-right: 1px solid var(--line);
  background: var(--cloth-sunk);
}
.menu h4 {
  margin: 14px 10px 6px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-weight: 500;
}
.menu h4:first-child {
  margin-top: 0;
}
.menu button {
  display: flex;
  gap: 10px;
  align-items: center;
  width: 100%;
  padding: 8px 10px;
  border-radius: var(--r-md);
  color: var(--ink-soft);
  font-size: var(--t-sm);
  text-align: left;
}
.menu button:hover {
  background: var(--chip);
  color: var(--ink);
}
.menu button.on {
  background: var(--chip);
  color: var(--ink);
  font-weight: 600;
}
.menu-foot {
  margin-top: auto;
  padding-top: 10px;
  border-top: 1px solid var(--line);
}

/* ---------- 右侧：固定顶栏 + 独立滚动区 ---------- */
.main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}
.bar {
  flex: none;
  display: flex;
  justify-content: flex-end;
  padding: 10px 14px 4px;
}
.close {
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border-radius: var(--r-sm);
  color: var(--ink-soft);
}
.close:hover {
  background: var(--chip);
  color: var(--ink);
}

.content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 0 28px 28px;
}
h2 {
  margin: 0 0 14px;
  font-size: var(--t-lg);
  font-weight: 600;
}
.card {
  margin-bottom: 14px;
  padding: 6px 16px;
  border-radius: var(--r-lg);
  background: var(--chip);
}
.card > h3 {
  margin: 0;
  padding: 10px 0 6px;
  font-size: var(--t-sm);
  font-weight: 600;
}
.card > .hint {
  margin: 0 0 10px;
}
.card > :last-child:not(.row) {
  margin-bottom: 12px;
}
.row {
  display: flex;
  gap: 16px;
  align-items: center;
  justify-content: space-between;
  padding: 12px 0;
  border-top: 1px solid var(--line);
}
.card > .row:first-child {
  border-top: 0;
}
.account .avatar {
  display: grid;
  flex: none;
  place-items: center;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  background: var(--indigo);
  color: #fff;
  font-weight: 600;
}
.account .label small {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.btn.logout {
  display: inline-flex;
  flex: none;
  gap: 6px;
  align-items: center;
  color: var(--red);
}
.row.col {
  flex-direction: column;
  align-items: stretch;
  gap: 8px;
}
label.row {
  cursor: pointer;
}
.label {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.label strong {
  display: flex;
  gap: 6px;
  align-items: center;
  font-size: var(--t-sm);
  font-weight: 500;
}
.label .locked {
  color: var(--ink-faint);
}
.btn.preview {
  height: 30px;
}
.label small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.value {
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.hint {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.choices {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.choices button {
  height: 32px;
  padding: 0 14px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--cloth);
  font-size: var(--t-sm);
}
.choices button.on {
  border-color: var(--indigo);
  background: var(--indigo-wash);
  color: var(--indigo);
  font-weight: 500;
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
select,
.proxy input {
  padding: 7px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: inherit;
  font: inherit;
  font-size: var(--t-sm);
}
.proxy {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
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
.link {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  color: var(--indigo);
  font-size: var(--t-xs);
  font-weight: 500;
}
.store-legend .link {
  margin-left: auto;
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
  background: var(--cloth);
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.row-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.empty {
  margin: 0 0 12px;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
.approvals {
  margin: 0 0 12px;
  padding: 4px;
  list-style: none;
  max-height: 220px;
  overflow-y: auto;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
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

/* 窄窗口：菜单改成顶部横向滚动 */
@media (max-width: 680px) {
  .dialog {
    grid-template-columns: 1fr;
    grid-template-rows: auto 1fr;
  }
  .menu {
    flex-direction: row;
    overflow-x: auto;
    padding: 10px;
    border-right: 0;
    border-bottom: 1px solid var(--line);
  }
  .menu h4 {
    display: none;
  }
  .menu button {
    width: auto;
    white-space: nowrap;
  }
  .menu-foot {
    margin: 0;
    padding: 0;
    border: 0;
  }
}
</style>
