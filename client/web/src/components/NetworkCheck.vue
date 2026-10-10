<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, CircleAlert, Loader, Minus, RefreshCw, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { toast } from '../store'
import type { CheckStatus, NetworkCheck, NetworkReport } from '../types'

// 设置 → 网络 → 网络检测：代理、hosts、DNS、服务连通性、设备认证、TCP、丢包，一项项排查，
// 每次检测在本机日志目录下留一份文本报告，员工可以导出发给 IT。
const { t, te } = useI18n()

const report = ref<NetworkReport | null>(null)
const running = ref(false)

/** 检测中先把各项占位显示出来，结果回来再替换 */
const ORDER: NetworkCheck['id'][] = ['proxy', 'hosts', 'dns', 'http', 'auth', 'tcp', 'loss']

const rows = computed(() => {
  if (report.value && !running.value) return report.value.checks
  return ORDER.map((id) => report.value?.checks.find((c) => c.id === id) ?? null).map((c, i) => ({ ...(c ?? {}), id: ORDER[i], pending: true }))
})

const problems = computed(() => report.value?.checks.filter((c) => c.status === 'fail' || c.status === 'warn').length ?? 0)

const lastTime = computed(() => {
  if (!report.value) return ''
  return new Date(report.value.at).toLocaleString(undefined, {
    year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  })
})

const reportName = computed(() => report.value?.reportPath.split(/[\\/]/).pop() ?? '')

async function run() {
  if (running.value) return
  running.value = true
  try {
    report.value = await bridge.diagnoseNetwork()
  } catch (e) {
    toast(t('network.failed', { msg: e instanceof Error ? e.message : String(e) }))
  } finally {
    running.value = false
  }
}

onMounted(async () => {
  // 测过就先显示上次的结果；没测过直接跑一遍
  report.value = await bridge.lastNetworkReport().catch(() => null)
  if (!report.value) void run()
})

function detail(c: NetworkCheck) {
  const key = `network.code.${c.id}.${c.code}`
  const fallback = `network.code.${c.code}`
  const params = { v: c.value, err: c.error }
  if (te(key)) return t(key, params)
  if (te(fallback)) return t(fallback, params)
  return c.error || c.code
}

/** 右侧那一列的数据：地址、状态码、毫秒、丢包率 */
function figure(c: NetworkCheck) {
  const ms = c.ms != null ? `${c.ms} ms` : ''
  switch (c.id) {
    case 'http':
      return c.status === 'pass' ? `${t('network.httpOk')}  ${ms}` : c.value ? `HTTP ${c.value}  ${ms}` : ms
    case 'auth':
      return ms
    case 'tcp':
      return `${c.value}  ${ms}`.trim()
    case 'dns':
      return c.code === 'dnsOk' ? `${c.value}  ${ms}` : c.value
    case 'loss':
      return c.status === 'skip' ? '' : c.code === 'icmpBlocked' ? t('network.loss', { v: c.value }) : `${t('network.loss', { v: c.value })}  ${t('network.avg', { v: ms })}`
    default:
      return c.value
  }
}

const proxyMode = computed(() => {
  const r = report.value
  if (!r) return ''
  return r.proxyUrl || t('network.noProxy')
})

async function exportReport() {
  const r = await bridge.exportNetworkReport()
  if (r.ok) toast(t('network.exported', { path: r.path }))
}

const badge: Record<CheckStatus, string> = { pass: 'network.pass', warn: 'network.warn', fail: 'network.fail', skip: 'network.skip' }
</script>

<template>
  <section class="net">
    <header>
      <div>
        <h3>{{ t('network.title') }}</h3>
        <small v-if="lastTime">{{ t('network.lastTime', { time: lastTime }) }}</small>
      </div>
      <button type="button" class="btn" :disabled="running" @click="run">
        <Loader v-if="running" :size="14" class="spin" />
        <RefreshCw v-else :size="14" />
        {{ running ? t('network.running') : t('network.rerun') }}
      </button>
    </header>

    <h4>{{ t('network.overview') }}</h4>
    <div class="item">
      <span class="label">
        <strong>{{ t('network.health') }}</strong>
        <small v-if="running">{{ t('network.runningHint') }}</small>
        <small v-else-if="report">{{ report.overall === 'pass' ? t('network.allPass') : report.overall === 'warn' ? t('network.someWarn', { n: problems }) : t('network.someFail', { n: problems }) }}</small>
      </span>
      <span v-if="running" class="badge run"><Loader :size="12" class="spin" />{{ t('network.checking') }}</span>
      <span v-else-if="report" class="badge" :class="report.overall">
        <Check v-if="report.overall === 'pass'" :size="12" /><CircleAlert v-else-if="report.overall === 'warn'" :size="12" /><X v-else :size="12" />
        {{ t(badge[report.overall]) }}
      </span>
    </div>

    <template v-if="report">
      <h4>{{ t('network.target') }}</h4>
      <div class="item">
        <span class="label"><strong>{{ t('network.endpoint') }}</strong><small>{{ t('network.endpointHint') }}</small></span>
        <span class="figure strong">{{ report.endpoint }}</span>
      </div>
      <div class="item">
        <span class="label"><strong>{{ t('network.host') }}</strong><small>{{ report.scheme }} · {{ t('network.hostHint') }}</small></span>
        <span class="figure strong">{{ report.host }}:{{ report.port }}</span>
      </div>
      <div class="item">
        <span class="label">
          <strong>{{ t('network.proxyMode') }}</strong>
          <small>{{ t(`network.mode.${report.proxyMode || 'none'}`) }} · {{ report.proxyUrl ? t('network.proxyActive') : t('network.proxyNone') }}</small>
        </span>
        <span class="figure strong">{{ proxyMode }}</span>
      </div>
    </template>

    <h4>{{ t('network.checks') }}</h4>
    <div v-for="c in rows" :key="c.id" class="item">
      <span class="label">
        <strong>{{ t(`network.check.${c.id}`) }}</strong>
        <small v-if="'pending' in c">{{ t('network.checking') }}</small>
        <small v-else :class="{ err: c.status === 'fail' }">{{ detail(c as NetworkCheck) }}</small>
      </span>
      <template v-if="'pending' in c">
        <span class="badge run"><Loader :size="12" class="spin" />{{ t('network.checking') }}</span>
      </template>
      <template v-else>
        <span class="figure">{{ figure(c as NetworkCheck) }}</span>
        <span class="badge" :class="c.status">
          <Check v-if="c.status === 'pass'" :size="12" />
          <CircleAlert v-else-if="c.status === 'warn'" :size="12" />
          <X v-else-if="c.status === 'fail'" :size="12" />
          <Minus v-else :size="12" />
          {{ t(badge[(c as NetworkCheck).status]) }}
        </span>
      </template>
    </div>

    <template v-if="report">
      <h4>{{ t('network.support') }}</h4>
      <div class="item">
        <span class="label"><strong>{{ t('network.reportDir') }}</strong><small class="path">{{ report.reportDir }}</small></span>
        <button type="button" class="link" @click="bridge.openNetworkReports()">{{ t('network.open') }}</button>
      </div>
      <div v-if="report.reportPath" class="item">
        <span class="label"><strong>{{ t('network.latestReport') }}</strong><small class="path">{{ reportName }}</small></span>
        <span class="links">
          <button type="button" class="link" @click="exportReport">{{ t('network.export') }}</button>
          <button type="button" class="link" @click="bridge.revealNetworkReport()">{{ t('network.reveal') }}</button>
        </span>
      </div>
    </template>
  </section>
</template>

<style scoped>
.net {
  margin-top: 24px;
}
header {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  justify-content: space-between;
  padding-bottom: 14px;
  border-bottom: 1px solid var(--line);
}
header h3 {
  margin: 0 0 4px;
  font-size: var(--t-lg);
  font-weight: 600;
}
header small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
header .btn {
  display: inline-flex;
  flex: none;
  gap: 6px;
  align-items: center;
}
h4 {
  margin: 18px 0 8px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-weight: 500;
}
.item {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-bottom: 8px;
  padding: 12px 16px;
  border-radius: var(--r-md);
  background: var(--chip);
}
.label {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}
.label strong {
  font-size: var(--t-sm);
  font-weight: 600;
}
.label small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
  line-height: 1.5;
}
.label small.err {
  color: var(--red);
}
.label .path {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-code);
}
.figure {
  flex: none;
  max-width: 50%;
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  font-variant-numeric: tabular-nums;
  text-overflow: ellipsis;
  white-space: pre;
}
.figure.strong {
  color: var(--ink);
  font-size: var(--t-sm);
  font-weight: 500;
}
.badge {
  display: inline-flex;
  flex: none;
  gap: 4px;
  align-items: center;
  height: 24px;
  padding: 0 10px;
  border-radius: 12px;
  font-size: var(--t-xs);
  font-weight: 500;
  white-space: nowrap;
}
.badge.pass {
  background: var(--thread-wash);
  color: var(--thread);
}
.badge.warn {
  background: var(--amber-wash);
  color: var(--amber);
}
.badge.fail {
  background: var(--red-wash);
  color: var(--red);
}
.badge.skip,
.badge.run {
  background: var(--cloth);
  color: var(--ink-faint);
}
.links {
  display: flex;
  flex: none;
  gap: 16px;
}
.link {
  flex: none;
  padding: 0;
  color: var(--indigo);
  font-size: var(--t-sm);
}
.link:hover {
  text-decoration: underline;
}
.spin {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
