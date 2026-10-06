<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FolderOpen, Lock, ShieldCheck, TriangleAlert, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { state, toast } from '../store'
import type { SecurityEvent, SecurityItem } from '../types'

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
const loading = ref(true)
/** 正在等用户确认后果的那一项 */
const confirming = ref<SecurityItem | null>(null)

/** 界面按这三组排，和用户的心智对得上：能做什么 / 数据会怎样 / 发生过什么 */
const groups = [
  { id: 'sandbox', keys: ['sandbox', 'command_policy', 'network_allowlist', 'system_tools'] },
  { id: 'data', keys: ['delete_protection', 'auto_backup', 'backup_quota_mb', 'batch_delete_threshold'] },
  { id: 'notify', keys: ['notifications', 'notification_sound'] },
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
  events.value = await bridge.securityEvents('blocked').catch(() => [])
  loading.value = false
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
  <div class="scrim" @mousedown.self="state.securityOpen = false">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.security.title')">
      <header>
        <ShieldCheck :size="18" class="shield" />
        <span class="titles">
          <h2>{{ t('ui.security.title') }}</h2>
          <small>{{ t('ui.security.subtitle') }}</small>
        </span>
        <span v-if="lockedCount" class="managed">
          <Lock :size="12" /> {{ t('ui.security.managedCount', { n: lockedCount }) }}
        </span>
        <button type="button" class="ico" :aria-label="t('settings.close')" @click="state.securityOpen = false">
          <X :size="16" />
        </button>
      </header>

      <div class="body">
        <p v-if="loading" class="empty">{{ t('ui.security.loading') }}</p>
        <p v-else-if="items.length === 0" class="empty">{{ t('ui.security.unavailable') }}</p>

        <section v-for="g in grouped" :key="g.id">
          <h3>{{ t(`ui.security.groups.${g.id}`) }}</h3>

          <div v-for="item in g.items" :key="item.key" class="item" :class="{ locked: item.locked }">
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
                :checked="item.value === true"
                :disabled="item.locked"
                @change="onToggle(item, ($event.target as HTMLInputElement).checked)"
              />
            </template>
          </div>

          <button v-if="g.id === 'data'" type="button" class="link" @click="bridge.openBackupFolder()">
            <FolderOpen :size="14" /> {{ t('ui.security.openBackups') }}
          </button>
        </section>

        <section v-if="events.length">
          <h3>{{ t('ui.security.groups.audit') }}</h3>
          <p class="hint">{{ t('ui.security.auditHint') }}</p>
          <div v-for="e in events.slice(0, 8)" :key="e.id" class="event">
            <TriangleAlert :size="14" />
            <span class="what">{{ e.detail }}</span>
            <time>{{ new Date(e.createdAt).toLocaleString() }}</time>
          </div>
        </section>
      </div>

      <!-- 关掉有后果的项之前，把后果原样摆出来 -->
      <div v-if="confirming" class="ask" role="alertdialog">
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

      <footer>
        <span class="hint">{{ t('ui.security.footer') }}</span>
        <button type="button" class="btn primary" @click="state.securityOpen = false">{{ t('settings.close') }}</button>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 70;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
}
.dialog {
  position: relative;
  display: flex;
  flex-direction: column;
  width: min(680px, 100%);
  max-height: calc(100vh - 32px);
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
header {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 18px 22px 12px;
}
.shield {
  color: var(--indigo);
}
.titles {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 1px;
}
.titles h2 {
  margin: 0;
  font-size: calc(16px * var(--font-scale));
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
.ico {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.ico:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.body {
  flex: 1;
  overflow-y: auto;
  padding: 0 22px 8px;
}
section {
  margin-bottom: 18px;
}
h3 {
  margin: 0 0 8px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}
.item {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-bottom: 6px;
  padding: 10px 12px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
}
/* 锁住的不置灰到看不清——要让人读得到自己被什么管着 */
.item.locked {
  background: color-mix(in srgb, var(--ink) 4%, transparent);
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
.item input[type='checkbox'] {
  width: 18px;
  height: 18px;
  accent-color: var(--indigo);
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
.link {
  display: inline-flex;
  gap: 5px;
  align-items: center;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--indigo);
  font: inherit;
  font-size: var(--t-xs);
  font-weight: 500;
  cursor: pointer;
}
.hint {
  margin: 0 0 8px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.event {
  display: flex;
  gap: 8px;
  align-items: center;
  padding: 7px 12px;
  border-radius: var(--r-md);
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
/* 关掉某一项之前的后果提示 */
.ask {
  position: absolute;
  inset: 0;
  z-index: 2;
  display: grid;
  place-items: center;
  padding: 24px;
  border-radius: var(--r-lg);
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
footer {
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 12px 22px 18px;
  border-top: 1px solid var(--line);
}
footer .hint {
  flex: 1;
  margin: 0;
}
</style>
