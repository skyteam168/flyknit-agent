<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Ban,
  ChartColumn,
  Check,
  CircleAlert,
  Languages,
  Mail,
  MessageSquare,
  Phone,
  RefreshCw,
  ShieldAlert,
  ShieldCheck,
  Trash2,
  Wrench,
  X,
} from '@lucide/vue'
import { bridge } from '../bridge'
import { state, toast } from '../store'
import type { SecurityDecision, SecurityEvent, UsageStats } from '../types'

const { t, te } = useI18n()
type Tab = 'usage' | 'security'
const tab = ref<Tab>('usage')
const usage = ref<UsageStats | null>(null)
const events = ref<SecurityEvent[]>([])
const filter = ref<'' | SecurityDecision>('')
const loading = ref(false)
const error = ref('')

const fmt = new Intl.NumberFormat()
const sceneIcons: Record<string, unknown> = { agent: Wrench, chat: MessageSquare, translate: Languages }
const decisionIcons: Record<SecurityDecision, unknown> = {
  blocked: ShieldAlert,
  approved: ShieldCheck,
  remembered: Check,
  rejected: Ban,
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    usage.value = await bridge.usageStats()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
  events.value = await bridge.securityEvents(filter.value).catch(() => [])
  loading.value = false
}
onMounted(load)

async function setFilter(f: '' | SecurityDecision) {
  filter.value = f
  events.value = await bridge.securityEvents(f).catch(() => [])
}

async function clearEvents() {
  events.value = []
  await bridge.clearSecurityEvents().catch(() => {})
}

/** 柱状图：最近 7 天的用量，按最大值归一 */
const maxDay = computed(() => Math.max(1, ...(usage.value?.byDay ?? []).map((d) => d.tokens)))
const percent = computed(() => {
  const u = usage.value
  if (!u || !u.dailyLimit) return 0
  return Math.min(100, Math.round((u.todayTokens / u.dailyLimit) * 100))
})
const blockedCount = computed(() => events.value.filter((e) => e.decision === 'blocked').length)

const sceneName = (s: string) => (te(`mode.${s}`) ? t(`mode.${s}`) : s)
const shortDay = (d: string) => d.slice(5)
const time = (iso: string) => new Date(iso).toLocaleString([], { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
const toolName = (n: string) => (te(`tool.names.${n}`) ? t(`tool.names.${n}`) : n)

function copyContact() {
  const u = usage.value
  if (!u) return
  void navigator.clipboard.writeText(`${u.contactName} ${u.contactEmail} ${u.contactPhone}`)
  toast(t('message.copied'))
}

const close = () => (state.usageOpen = false)
</script>

<template>
  <div class="scrim" @mousedown.self="close" @keydown.esc="close">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.usagePanel.title')">
      <header>
        <div class="badge"><ChartColumn :size="20" /></div>
        <div class="head-text">
          <h2>{{ t('ui.usagePanel.title') }}</h2>
          <p>{{ t('ui.usagePanel.subtitle') }}</p>
        </div>
        <button type="button" class="icon-btn" :title="t('ui.model.title')" @click="load"><RefreshCw :size="15" /></button>
        <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="close"><X :size="18" /></button>
      </header>

      <nav class="tabs" role="tablist">
        <button type="button" role="tab" :aria-selected="tab === 'usage'" :class="{ on: tab === 'usage' }" @click="tab = 'usage'">
          <ChartColumn :size="15" /><span>{{ t('ui.usagePanel.tabs.usage') }}</span>
        </button>
        <button type="button" role="tab" :aria-selected="tab === 'security'" :class="{ on: tab === 'security' }" @click="tab = 'security'">
          <ShieldAlert :size="15" /><span>{{ t('ui.usagePanel.tabs.security') }}</span>
          <em v-if="blockedCount">{{ blockedCount }}</em>
        </button>
      </nav>

      <div class="body">
        <!-- 用量 -->
        <template v-if="tab === 'usage'">
          <p v-if="error" class="empty">{{ t('ui.usagePanel.error', { msg: error }) }}</p>
          <template v-else-if="usage">
            <div class="today" :class="{ warn: usage.exceeded }">
              <div class="today-head">
                <span class="label">{{ t('ui.usagePanel.today') }}</span>
                <strong>{{ fmt.format(usage.todayTokens) }}</strong>
                <small v-if="usage.dailyLimit">/ {{ fmt.format(usage.dailyLimit) }} tokens</small>
                <small v-else>tokens · {{ t('ui.usagePanel.noLimit') }}</small>
              </div>
              <div v-if="usage.dailyLimit" class="meter" :aria-valuenow="percent" role="progressbar">
                <i :style="{ width: `${percent}%` }" :class="{ warn: percent >= 80 }" />
              </div>
              <p v-if="usage.dailyLimit && !usage.exceeded" class="left">{{ t('ui.usagePanel.remaining', { n: fmt.format(usage.remaining) }) }}</p>
            </div>

            <div v-if="usage.exceeded" class="contact">
              <CircleAlert :size="18" />
              <div>
                <strong>{{ t('ui.usagePanel.exceeded') }}</strong>
                <p>{{ t('ui.usagePanel.contactHint', { name: usage.contactName }) }}</p>
                <div class="links">
                  <a :href="`mailto:${usage.contactEmail}`"><Mail :size="14" /> {{ usage.contactEmail }}</a>
                  <span><Phone :size="14" /> {{ usage.contactPhone }}</span>
                  <button type="button" class="link" @click="copyContact">{{ t('message.copy') }}</button>
                </div>
              </div>
            </div>

            <h3>{{ t('ui.usagePanel.byMode') }}</h3>
            <p v-if="usage.byScene.length === 0" class="empty small">{{ t('ui.usagePanel.noneToday') }}</p>
            <ul v-else class="scenes">
              <li v-for="s in usage.byScene" :key="s.scene">
                <span class="ico"><component :is="sceneIcons[s.scene] ?? MessageSquare" :size="15" /></span>
                <span class="name">{{ sceneName(s.scene) }}</span>
                <span class="bar"><i :style="{ width: `${Math.round((s.total / Math.max(1, usage.todayTokens)) * 100)}%` }" /></span>
                <span class="num">{{ fmt.format(s.total) }}</span>
                <span class="sub">{{ t('ui.usagePanel.requests', { n: s.requests }) }}</span>
              </li>
            </ul>

            <h3>{{ t('ui.usagePanel.last7') }}</h3>
            <div class="days">
              <div v-for="d in usage.byDay" :key="d.day" class="day" :title="`${d.day}: ${fmt.format(d.tokens)} tokens`">
                <div class="col"><i :style="{ height: `${Math.max(3, Math.round((d.tokens / maxDay) * 100))}%` }" /></div>
                <small>{{ shortDay(d.day) }}</small>
              </div>
              <p v-if="usage.byDay.length === 0" class="empty small">{{ t('ui.usagePanel.noneToday') }}</p>
            </div>
          </template>
          <p v-else class="empty">{{ t('ui.skills.loading') }}</p>
        </template>

        <!-- 安全记录 -->
        <template v-else>
          <div class="bar-row">
            <p class="tab-hint">{{ t('ui.usagePanel.securityHint') }}</p>
            <button v-if="events.length" type="button" class="link" @click="clearEvents">{{ t('settings.clearAll') }}</button>
          </div>
          <div class="filters">
            <button type="button" :class="{ on: filter === '' }" @click="setFilter('')">{{ t('ui.filter.all') }}</button>
            <button type="button" :class="{ on: filter === 'blocked' }" @click="setFilter('blocked')">{{ t('ui.usagePanel.decisions.blocked') }}</button>
            <button type="button" :class="{ on: filter === 'approved' }" @click="setFilter('approved')">{{ t('ui.usagePanel.decisions.approved') }}</button>
            <button type="button" :class="{ on: filter === 'remembered' }" @click="setFilter('remembered')">{{ t('ui.usagePanel.decisions.remembered') }}</button>
            <button type="button" :class="{ on: filter === 'rejected' }" @click="setFilter('rejected')">{{ t('ui.usagePanel.decisions.rejected') }}</button>
          </div>
          <p v-if="events.length === 0" class="empty">{{ t('ui.usagePanel.noEvents') }}</p>
          <ul v-else class="events">
            <li v-for="e in events" :key="e.id" :class="e.decision">
              <span class="ico"><component :is="decisionIcons[e.decision]" :size="15" /></span>
              <span class="info">
                <span class="line">
                  <b>{{ t(`ui.usagePanel.decisions.${e.decision}`) }}</b>
                  <span class="chip"><component :is="sceneIcons[e.scene] ?? MessageSquare" :size="11" />{{ sceneName(e.scene) }}</span>
                  <span class="tool">{{ toolName(e.tool) }}</span>
                  <span class="when">{{ time(e.createdAt) }}</span>
                </span>
                <code>{{ e.detail }}</code>
                <small v-if="e.reason" class="reason">{{ e.reason }}</small>
                <small v-if="e.title" class="from">{{ t('ui.usagePanel.fromTask', { title: e.title }) }}</small>
              </span>
            </li>
          </ul>
        </template>
      </div>

      <footer>
        <span v-if="usage" class="admin">{{ t('ui.usagePanel.admin', { name: usage.contactName, email: usage.contactEmail, phone: usage.contactPhone }) }}</span>
        <span class="spacer" />
        <button type="button" class="btn primary" @click="close">{{ t('settings.close') }}</button>
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
  display: flex;
  flex-direction: column;
  width: min(720px, 100%);
  height: min(640px, calc(100vh - 32px));
  padding: 22px 24px 16px;
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
h3 {
  margin: 18px 0 8px;
  font-size: var(--t-sm);
  font-weight: 600;
  color: var(--ink-soft);
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
}
.tabs button.on {
  border-bottom-color: var(--indigo);
  color: var(--ink);
  font-weight: 500;
}
.tabs em {
  padding: 0 6px;
  border-radius: 8px;
  background: var(--red-wash);
  color: var(--red);
  font-style: normal;
  font-size: 11px;
}
.body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 14px 2px;
}
.today {
  padding: 14px 16px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
}
.today.warn {
  background: var(--red-wash);
}
.today-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
}
.today-head .label {
  flex: 1;
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.today-head strong {
  font-size: var(--t-xl);
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.today-head small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.meter {
  height: 7px;
  margin-top: 10px;
  border-radius: 4px;
  background: var(--line);
  overflow: hidden;
}
.meter i {
  display: block;
  height: 100%;
  border-radius: 4px;
  background: var(--indigo);
  transition: width 220ms;
}
.meter i.warn {
  background: var(--amber);
}
.left {
  margin: 8px 0 0;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.contact {
  display: flex;
  gap: 10px;
  margin-top: 12px;
  padding: 12px 14px;
  border-radius: var(--r-md);
  background: var(--red-wash);
  color: var(--red);
}
.contact strong {
  font-size: var(--t-sm);
}
.contact p {
  margin: 4px 0 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.links {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 14px;
  font-size: var(--t-sm);
}
.links a,
.links span {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: var(--ink-soft);
}
.links a {
  color: var(--indigo);
}
.link {
  color: var(--indigo);
  font-size: var(--t-xs);
  font-weight: 500;
}
.scenes {
  margin: 0;
  padding: 0;
  list-style: none;
}
.scenes li {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 7px 0;
}
.scenes .ico {
  display: grid;
  place-items: center;
  flex: none;
  width: 26px;
  height: 26px;
  border-radius: 7px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
}
.scenes .name {
  flex: none;
  width: 56px;
  font-size: var(--t-sm);
}
.scenes .bar {
  flex: 1;
  height: 7px;
  border-radius: 4px;
  background: var(--cloth-sunk);
  overflow: hidden;
}
.scenes .bar i {
  display: block;
  height: 100%;
  border-radius: 4px;
  background: var(--indigo);
}
.scenes .num {
  flex: none;
  min-width: 70px;
  text-align: right;
  font-size: var(--t-sm);
  font-variant-numeric: tabular-nums;
}
.scenes .sub {
  flex: none;
  min-width: 56px;
  text-align: right;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.days {
  display: flex;
  align-items: flex-end;
  gap: 10px;
  height: 110px;
}
.day {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  height: 100%;
}
.day .col {
  flex: 1;
  display: flex;
  align-items: flex-end;
  width: 100%;
  max-width: 44px;
}
.day .col i {
  display: block;
  width: 100%;
  border-radius: 5px 5px 2px 2px;
  background: var(--indigo);
  opacity: 0.85;
}
.day small {
  font-size: 11px;
  color: var(--ink-faint);
  font-variant-numeric: tabular-nums;
}
.bar-row {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}
.tab-hint {
  flex: 1;
  margin: 0 0 10px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.5;
}
.filters {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 10px;
}
.filters button {
  height: 28px;
  padding: 0 11px;
  border-radius: 999px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.filters button.on {
  background: var(--indigo-wash);
  color: var(--indigo);
  font-weight: 500;
}
.events {
  margin: 0;
  padding: 0;
  list-style: none;
}
.events li {
  display: flex;
  gap: 10px;
  padding: 10px 0;
  border-bottom: 1px solid var(--line);
}
.events .ico {
  display: grid;
  place-items: center;
  flex: none;
  width: 26px;
  height: 26px;
  border-radius: 7px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
}
.events .blocked .ico,
.events li.blocked .ico {
  background: var(--red-wash);
  color: var(--red);
}
.events li.approved .ico,
.events li.remembered .ico {
  background: var(--thread-wash);
  color: var(--thread);
}
.info {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  font-size: var(--t-sm);
}
.line b {
  font-weight: 600;
}
.chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 7px;
  border-radius: 9px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: 11px;
}
.tool {
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.when {
  margin-left: auto;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-variant-numeric: tabular-nums;
}
.info code {
  padding: 5px 8px;
  border-radius: var(--r-sm);
  background: var(--cloth-sunk);
  font-family: var(--font-code);
  font-size: var(--t-xs);
  word-break: break-all;
}
.reason {
  color: var(--red);
  font-size: var(--t-xs);
}
.from {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.empty {
  margin: 30px 0;
  text-align: center;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.empty.small {
  margin: 10px 0;
  text-align: left;
}
footer {
  display: flex;
  align-items: center;
  gap: 10px;
  padding-top: 12px;
  border-top: 1px solid var(--line);
}
.admin {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.spacer {
  flex: 1;
}
@keyframes pop {
  from {
    opacity: 0;
    transform: translateY(6px) scale(0.98);
  }
}
</style>
