<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, Download, FolderOpen, Globe, Lock, Trash2, TriangleAlert } from '@lucide/vue'
import { bridge } from '../bridge'
import { toast } from '../store'
import { auditType } from '../audit'
import type { NetworkAllowlist, SecurityEvent, SecurityItem } from '../types'
import ConfirmDialog from './ConfirmDialog.vue'

const emit = defineEmits<{ openAudit: [] }>()

// 安全中心：把散在各处的安全设置收到一个面板里。
//
// 这里有条贯穿全文件的规矩：**锁住的项也要显示**。让员工看见自己被什么规则管着，
// 比命令被拦了只告诉他「已被安全策略阻止」要好得多——现在那个黑盒子是真实的体验问题。
const { t, te } = useI18n()

// 条目的名字和风险说明由服务端下发，但服务端只有中文。越南线上的员工看到的必须是越南话，
// 所以本地有翻译就用本地的，没有（IT 以后加了新项）才退回服务端那句。
const titleOf = (i: SecurityItem) => (te(`ui.security.items.${i.key}.title`) ? t(`ui.security.items.${i.key}.title`) : i.title)
const riskOf = (i: SecurityItem) => (te(`ui.security.items.${i.key}.risk`) ? t(`ui.security.items.${i.key}.risk`) : i.risk)

const items = ref<SecurityItem[]>([])
const events = ref<SecurityEvent[]>([])
const network = ref<NetworkAllowlist | null>(null)
const loading = ref(true)
const exporting = ref(false)
/** 正在等用户确认后果的那一项 */
const confirming = ref<SecurityItem | null>(null)

/** 界面按这三组排，和用户的心智对得上：能做什么 / 数据会怎样 / 发生过什么 */
const groups = [
  { id: 'sandbox', keys: ['sandbox', 'network_allowlist', 'system_tools'] },
  { id: 'data', keys: ['delete_protection', 'auto_backup', 'backup_quota_mb', 'batch_delete_threshold'] },
  { id: 'learning', keys: ['learn_preferences', 'learn_facts', 'learn_experience', 'learn_episodes', 'learn_skills'] },
  { id: 'quality', keys: ['plan_guidance', 'verify_outputs'] },
  { id: 'personal', keys: ['persona_playful', 'persona_custom', 'keep_awake', 'keep_screen_on'] },
]

const byKey = computed(() => Object.fromEntries(items.value.map((i) => [i.key, i])))
const grouped = computed(() =>
  groups
    .map((g) => ({ ...g, items: g.keys.map((k) => byKey.value[k]).filter(Boolean) as SecurityItem[] }))
    .filter((g) => g.items.length > 0),
)

/**
 * 每一项下面那行小字。锁住的项也要把这项是干什么的讲清楚——只写「由 IT 配置」
 * 等于让人盯着一个不知道管什么的开关，和命令被拦了只说「已被安全策略阻止」一样糟。
 */
function sub(item: SecurityItem) {
  const risk = riskOf(item)
  if (!item.locked) return risk || t('ui.security.yours')
  return risk ? `${risk} ${t('ui.security.managedBy')}` : t('ui.security.managedBy')
}

/** 有多少项是 IT 锁住的。摆在标题旁边，一眼看出这台机器被管得多紧 */
const lockedCount = computed(() => items.value.filter((i) => i.locked).length)

async function load() {
  loading.value = true
  items.value = await bridge.securitySettings().catch(() => [])
  events.value = await bridge.securityEvents('', 3).catch(() => [])
  network.value = await bridge.networkAllowlist().catch(() => null)
  loading.value = false
}

async function exportEvents() {
  exporting.value = true
  try {
    const r = await bridge.exportSecurityEvents().catch((e) => ({ ok: false, cancelled: false, message: String(e), path: '', count: 0 }))
    if (r.cancelled) return
    toast(r.ok ? t('ui.security.exported', { n: r.count, path: r.path }) : t('ui.security.exportFailed', { msg: r.message }))
  } finally {
    exporting.value = false
  }
}

function onToggle(item: SecurityItem, next: boolean) {
  // 关掉有后果的项时先把后果讲清楚，而不是让人事后才发现
  if (!next && item.risk) {
    confirming.value = item
    return
  }
  void apply(item.key, next)
}

async function apply(key: string, value: boolean | number) {
  const r = await bridge.setSecurityItem(key, value).catch(() => null)
  if (!r) return
  if (r.items) items.value = r.items
  if (!r.ok) toast(r.message)
  else events.value = await bridge.securityEvents('', 3).catch(() => events.value)
}

const confirmClear = ref(false)
async function clearEvents() {
  confirmClear.value = false
  await bridge.clearSecurityEvents().catch(() => {})
  events.value = []
}

async function confirmOff() {
  const item = confirming.value
  confirming.value = null
  if (item) await apply(item.key, false)
}

function onNumber(item: SecurityItem, raw: string) {
  const n = Number(raw)
  if (Number.isFinite(n)) void apply(item.key, n)
}

onMounted(load)
</script>

<template>
  <div class="panel">
    <header>
      <span class="titles">
        <h2>{{ t('ui.security.title') }}</h2>
        <small>{{ t('ui.security.subtitle') }}</small>
      </span>
      <span v-if="lockedCount" class="managed">
        <Lock :size="12" /> {{ t('ui.security.managedCount', { n: lockedCount }) }}
      </span>
    </header>

    <p v-if="loading" class="empty">{{ t('ui.security.loading') }}</p>
    <p v-else-if="items.length === 0" class="empty">{{ t('ui.security.unavailable') }}</p>

    <section v-for="g in grouped" :key="g.id" class="card">
      <h3>{{ t(`ui.security.groups.${g.id}`) }}</h3>

      <template v-for="item in g.items" :key="item.key">
        <div class="item" :class="{ locked: item.locked }">
          <span class="label">
            <strong>
              {{ titleOf(item) }}
              <Lock v-if="item.locked" :size="12" class="lock" />
            </strong>
            <small>{{ sub(item) }}</small>
          </span>

          <template v-if="item.kind === 'int'">
            <input
              class="num"
              type="number"
              :value="item.value"
              :min="item.min ?? undefined"
              :max="item.max ?? undefined"
              :disabled="item.locked"
              @change="onNumber(item, ($event.target as HTMLInputElement).value)"
            />
          </template>
          <template v-else>
            <input
              type="checkbox"
              class="switch"
              :checked="item.value === true"
              :disabled="item.locked"
              @change="onToggle(item, ($event.target as HTMLInputElement).checked)"
            />
          </template>
        </div>
        <!-- 白名单具体放行哪些网站：员工被拦时能自己对照，而不是只看到一个开关 -->
        <div v-if="item.key === 'network_allowlist' && network" class="domains">
          <Globe :size="13" class="globe" />
          <template v-if="item.value === true">
            <span class="lead">{{ t('ui.security.networkDomains') }}</span>
            <span v-for="d in network.domains" :key="d" class="domain">{{ d === '*' ? t('ui.security.networkAny') : d }}</span>
            <span v-if="network.allowPrivate" class="domain soft">{{ t('ui.security.networkPrivate') }}</span>
            <span v-if="network.domains.length === 0 && !network.allowPrivate" class="lead">{{ t('ui.security.networkNone') }}</span>
          </template>
          <span v-else class="lead">{{ t('ui.security.networkOff') }}</span>
        </div>
      </template>

      <div v-if="g.id === 'data'" class="card-foot">
        <button type="button" class="btn" @click="bridge.openBackupFolder()">
          <FolderOpen :size="14" /> {{ t('ui.security.openBackups') }}
        </button>
      </div>
    </section>

    <section v-if="!loading" class="card">
      <div class="card-head">
        <span class="titles">
          <h3>{{ t('ui.audit.title') }}</h3>
          <small class="sub">{{ t('ui.audit.subtitle') }}</small>
        </span>
        <button type="button" class="btn" :disabled="exporting || events.length === 0" @click="exportEvents">
          <Download :size="14" /> {{ t('ui.audit.export') }}
        </button>
        <button type="button" class="btn" :disabled="events.length === 0" @click="confirmClear = true">
          <Trash2 :size="14" /> {{ t('ui.audit.clear') }}
        </button>
      </div>
      <p v-if="events.length === 0" class="hint">{{ t('ui.audit.empty') }}</p>
      <div v-for="e in events.slice(0, 3)" :key="e.id" class="event">
        <span class="what">
          <span class="type">[{{ t(`ui.audit.types.${auditType(e)}`) }}]</span>
          {{ e.detail }}
        </span>
        <time>{{ new Date(e.createdAt).toLocaleString() }}</time>
      </div>
      <div class="card-foot">
        <button type="button" class="more" @click="emit('openAudit')">
          {{ t('ui.audit.viewAll') }} <ChevronRight :size="14" />
        </button>
      </div>
    </section>

    <ConfirmDialog
      v-if="confirmClear"
      :title="t('ui.usagePanel.clearTitle')"
      :body="t('ui.usagePanel.clearBody')"
      :ok="t('settings.clearAll')"
      danger
      @ok="clearEvents"
      @cancel="confirmClear = false"
    />

    <p v-if="items.length" class="hint foot">{{ t('ui.security.footer') }}</p>

    <!-- 关掉有后果的项之前，把后果原样摆出来 -->
    <div v-if="confirming" class="ask" role="alertdialog" @mousedown.self="confirming = null">
      <div class="ask-box">
        <TriangleAlert :size="18" class="warn" />
        <h4>{{ t('ui.security.confirmTitle', { name: titleOf(confirming) }) }}</h4>
        <p>{{ riskOf(confirming) }}</p>
        <p class="logged">{{ t('ui.security.willBeLogged') }}</p>
        <div class="ask-row">
          <button type="button" class="btn" @click="confirming = null">{{ t('ui.security.keepOn') }}</button>
          <button type="button" class="btn danger" @click="confirmOff">{{ t('ui.security.turnOff') }}</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
header {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  margin-bottom: 14px;
}
.titles {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.titles h2 {
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 600;
}
.titles small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.managed {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  padding: 3px 9px;
  border-radius: 999px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.card {
  margin-bottom: 14px;
  padding: 6px 16px;
  border-radius: var(--r-lg);
  background: var(--chip);
}
h3 {
  margin: 0;
  padding: 10px 0 8px;
  color: var(--ink);
  font-size: var(--t-sm);
  font-weight: 600;
}
.item {
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 11px 0;
  border-top: 1px solid var(--line);
}
.label {
  flex: 1;
  display: flex;
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
.lock {
  color: var(--ink-faint);
}
.label small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.item input:disabled {
  cursor: not-allowed;
}
.num {
  width: 92px;
  padding: 5px 8px;
  border: 1px solid var(--line);
  border-radius: 7px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
  text-align: right;
}
.num:disabled {
  color: var(--ink-faint);
}
.card-head {
  display: flex;
  gap: 10px;
  align-items: center;
  justify-content: space-between;
}
.card-head {
  padding: 10px 0;
}
.card-head h3 {
  padding: 0;
}
.card-head .sub {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.card-head .btn {
  height: 30px;
}
.event .type {
  color: var(--ink-soft);
  font-family: inherit;
}
.more {
  display: inline-flex;
  gap: 2px;
  align-items: center;
  padding: 2px 4px;
  border: 0;
  border-radius: var(--r-sm);
  background: none;
  color: var(--ink-soft);
  font: inherit;
  font-size: var(--t-xs);
  cursor: pointer;
}
.more:hover {
  color: var(--ink);
  background: var(--cloth-sunk);
}
.domains {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
  margin: -4px 0 0;
  padding: 0 0 11px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.globe {
  flex: none;
}
.domain {
  padding: 1px 8px;
  border: 1px solid var(--line-strong);
  border-radius: 999px;
  background: var(--cloth);
  color: var(--ink-soft);
  font-family: var(--font-code);
}
.domain.soft {
  font-family: inherit;
  border-style: dashed;
}
.card-foot {
  display: flex;
  justify-content: flex-end;
  padding: 4px 0 10px;
}
.hint {
  margin: 0 0 8px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.foot {
  margin: 4px 0 0;
}
.event {
  display: flex;
  gap: 8px;
  align-items: center;
  padding: 7px 0;
  border-top: 1px solid var(--line);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.event .what {
  flex: 1;
  overflow: hidden;
  font-family: var(--font-code);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.event time {
  color: var(--ink-faint);
  white-space: nowrap;
}
.empty {
  padding: 28px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
  text-align: center;
}
/* 关掉某一项之前的后果提示，盖住整个设置弹窗而不是只盖住滚动区 */
.ask {
  position: fixed;
  inset: 0;
  z-index: 80;
  display: grid;
  place-items: center;
  padding: 24px;
  background: color-mix(in srgb, var(--ink) 34%, transparent);
}
.ask-box {
  max-width: 380px;
  padding: 20px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  text-align: center;
}
.warn {
  color: var(--red);
}
.ask-box h4 {
  margin: 8px 0 6px;
  font-size: calc(15px * var(--font-scale));
}
.ask-box p {
  margin: 0 0 6px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  text-align: left;
}
.logged {
  color: var(--ink-faint) !important;
  font-size: var(--t-xs) !important;
}
.ask-row {
  display: flex;
  gap: 8px;
  justify-content: center;
  margin-top: 14px;
}
</style>
