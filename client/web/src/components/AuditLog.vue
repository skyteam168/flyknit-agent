<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronLeft, Download, Search, Trash2, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { toast } from '../store'
import { auditType, auditTypes, inRange, type AuditRange } from '../audit'
import type { SecurityDecision, SecurityEvent } from '../types'
import ConfirmDialog from './ConfirmDialog.vue'
import FilterSelect from './FilterSelect.vue'

const emit = defineEmits<{ back: [] }>()
const { t } = useI18n()

const events = ref<SecurityEvent[]>([])
const loading = ref(true)
const type = ref('')
const result = ref('')
const range = ref('')
const keyword = ref('')
const searching = ref(false)
const searchBox = ref<HTMLInputElement>()
const exporting = ref(false)
const confirmClear = ref(false)

const decisions: SecurityDecision[] = ['blocked', 'approved', 'remembered', 'allowed', 'rejected', 'changed']
const ranges: AuditRange[] = ['today', 'week', 'month']

onMounted(async () => {
  events.value = await bridge.securityEvents('', 500).catch(() => [])
  loading.value = false
})

// 下拉里只列出记录里真有的类型和结果，免得选了一项永远是空的
const typeOptions = computed(() => [
  { value: '', label: t('ui.audit.allTypes') },
  ...auditTypes
    .map((k) => ({ value: k, label: t(`ui.audit.types.${k}`), count: events.value.filter((e) => auditType(e) === k).length }))
    .filter((o) => o.count > 0),
])
const resultOptions = computed(() => [
  { value: '', label: t('ui.audit.allResults') },
  ...decisions
    .map((d) => ({ value: d, label: t(`ui.usagePanel.decisions.${d}`), count: events.value.filter((e) => e.decision === d).length }))
    .filter((o) => o.count > 0),
])
const rangeOptions = computed(() => [
  { value: '', label: t('ui.audit.allTime') },
  ...ranges.map((r) => ({ value: r, label: t(`ui.audit.ranges.${r}`) })),
])

const filtered = computed(() => {
  const q = keyword.value.trim().toLowerCase()
  return events.value.filter(
    (e) =>
      (!type.value || auditType(e) === type.value) &&
      (!result.value || e.decision === result.value) &&
      inRange(e, range.value as '' | AuditRange) &&
      (!q || [e.detail, e.reason, e.title].some((s) => s.toLowerCase().includes(q))),
  )
})
const narrowed = computed(() => !!(type.value || result.value || range.value || keyword.value.trim()))

async function openSearch() {
  searching.value = true
  await nextTick()
  searchBox.value?.focus()
}
function closeSearch() {
  keyword.value = ''
  searching.value = false
}

async function exportEvents() {
  exporting.value = true
  try {
    const ids = narrowed.value ? filtered.value.map((e) => e.id) : undefined
    const r = await bridge
      .exportSecurityEvents(ids)
      .catch((e) => ({ ok: false, cancelled: false, message: String(e), path: '', count: 0 }))
    if (r.cancelled) return
    toast(r.ok ? t('ui.security.exported', { n: r.count, path: r.path }) : t('ui.security.exportFailed', { msg: r.message }))
  } finally {
    exporting.value = false
  }
}

async function clearEvents() {
  confirmClear.value = false
  await bridge.clearSecurityEvents().catch(() => {})
  events.value = []
}

const fmtTime = (s: string) => new Date(s).toLocaleString()
</script>

<template>
  <div class="audit">
    <button type="button" class="back" @click="emit('back')">
      <ChevronLeft :size="18" />
      <h2>{{ t('ui.audit.title') }}</h2>
    </button>

    <div class="tools">
      <FilterSelect v-model="type" :options="typeOptions" />
      <FilterSelect v-model="result" :options="resultOptions" />
      <FilterSelect v-model="range" :options="rangeOptions" :width="148" />
      <div v-if="searching" class="search">
        <Search :size="14" />
        <input
          ref="searchBox"
          v-model="keyword"
          :placeholder="t('ui.audit.searchPlaceholder')"
          @keydown.esc.stop="closeSearch"
        />
        <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="closeSearch"><X :size="14" /></button>
      </div>
      <button v-else type="button" class="icon-btn boxed" :aria-label="t('ui.audit.search')" @click="openSearch">
        <Search :size="15" />
      </button>
      <span class="spacer" />
      <button type="button" class="btn" :disabled="exporting || filtered.length === 0" @click="exportEvents">
        <Download :size="14" /> {{ t('ui.audit.export') }}
      </button>
      <button type="button" class="btn" :disabled="events.length === 0" @click="confirmClear = true">
        <Trash2 :size="14" /> {{ t('ui.audit.clear') }}
      </button>
    </div>

    <div class="table">
      <div class="thead">
        <span>{{ t('ui.audit.logs', { n: filtered.length }) }}</span>
        <span class="time">{{ t('ui.audit.time') }}</span>
      </div>
      <p v-if="loading" class="empty">{{ t('ui.security.loading') }}</p>
      <p v-else-if="filtered.length === 0" class="empty">
        {{ events.length === 0 ? t('ui.audit.empty') : t('ui.audit.noMatch') }}
      </p>
      <div v-for="e in filtered" :key="e.id" class="tr">
        <span class="log">
          <span class="line">
            <span class="type">[{{ t(`ui.audit.types.${auditType(e)}`) }}]</span>
            <span class="detail" :title="e.detail">{{ e.detail }}</span>
          </span>
          <span class="meta">
            <span class="badge" :class="e.decision">{{ t(`ui.usagePanel.decisions.${e.decision}`) }}</span>
            <span v-if="e.reason" class="reason" :title="e.reason">{{ e.reason }}</span>
            <span v-else-if="e.title" class="reason">{{ t('ui.usagePanel.fromTask', { title: e.title }) }}</span>
          </span>
        </span>
        <time class="time">{{ fmtTime(e.createdAt) }}</time>
      </div>
    </div>

    <ConfirmDialog
      v-if="confirmClear"
      :title="t('ui.usagePanel.clearTitle')"
      :body="t('ui.usagePanel.clearBody')"
      :ok="t('settings.clearAll')"
      danger
      @ok="clearEvents"
      @cancel="confirmClear = false"
    />
  </div>
</template>

<style scoped>
.back {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  margin: 0 0 16px -4px;
  padding: 2px 4px;
  border: 0;
  border-radius: var(--r-sm);
  background: none;
  color: var(--ink);
  cursor: pointer;
}
.back:hover {
  background: var(--cloth-sunk);
}
.back h2 {
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 600;
}
.tools {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  margin-bottom: 14px;
}
.spacer {
  flex: 1;
}
.tools .btn {
  height: 32px;
}
.boxed {
  width: 32px;
  height: 32px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--cloth);
}
.search {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  height: 32px;
  padding: 0 4px 0 10px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink-faint);
}
.search input {
  width: 160px;
  border: 0;
  outline: none;
  background: none;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
}
.search .icon-btn {
  width: 24px;
  height: 24px;
}
.table {
  border-radius: var(--r-lg);
  background: var(--chip);
  overflow: hidden;
}
.thead,
.tr {
  display: flex;
  gap: 16px;
  align-items: center;
  padding: 0 16px;
}
.thead {
  height: 40px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 500;
}
.tr {
  padding-top: 10px;
  padding-bottom: 10px;
  border-top: 1px solid var(--line);
}
.time {
  flex: none;
  width: 150px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  text-align: right;
}
.thead .time {
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.log {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}
.line {
  display: flex;
  gap: 6px;
  min-width: 0;
  font-size: var(--t-sm);
}
.type {
  flex: none;
  color: var(--ink-soft);
}
.detail {
  overflow: hidden;
  color: var(--ink);
  font-family: var(--font-code);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.meta {
  display: flex;
  gap: 8px;
  align-items: center;
  min-width: 0;
  font-size: var(--t-xs);
}
.badge {
  flex: none;
  padding: 0 7px;
  border-radius: 999px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  line-height: 18px;
}
.badge.blocked {
  background: color-mix(in srgb, var(--red) 12%, transparent);
  color: var(--red);
}
.badge.approved,
.badge.remembered,
.badge.allowed {
  background: color-mix(in srgb, var(--thread) 14%, transparent);
  color: var(--thread);
}
.badge.changed {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.reason {
  overflow: hidden;
  color: var(--ink-faint);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.empty {
  margin: 0;
  padding: 32px 16px;
  border-top: 1px solid var(--line);
  color: var(--ink-faint);
  font-size: var(--t-sm);
  text-align: center;
}
</style>
