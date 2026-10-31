<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import qrcode from 'qrcode-generator'
import { Loader, Smartphone, Unlink } from '@lucide/vue'
import { bridge } from '../bridge'
import { fail, toast } from '../store'
import type { MobileInfo, MobilePairCode } from '../types'

// 设置 → 手机：显示二维码（服务器地址 + 一次性配对码），员工用 FlyknitBuddy 安卓 App 扫码绑定这台电脑
const { t } = useI18n()
const info = ref<MobileInfo | null>(null)
const loading = ref(true)
const pair = ref<MobilePairCode | null>(null)
const now = ref(Date.now())
let ticker: number | undefined
let watcher: number | undefined

async function load() {
  try {
    const before = info.value?.phones.length ?? 0
    info.value = await bridge.mobile()
    // 等扫码期间：手机绑上了就收起二维码
    if (pair.value && info.value.phones.length > before) {
      pair.value = null
      stopWatching()
      toast(t('ui.mobile.bound'))
    }
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

function stopWatching() {
  clearInterval(ticker)
  clearInterval(watcher)
}

async function showCode() {
  try {
    pair.value = await bridge.mobilePairCode()
    stopWatching()
    ticker = window.setInterval(() => {
      now.value = Date.now()
      if (pair.value && new Date(pair.value.expiresAt).getTime() <= now.value) {
        pair.value = null
        stopWatching()
      }
    }, 1000)
    watcher = window.setInterval(load, 3000)
  } catch (e) {
    fail(e)
  }
}

/** 二维码 SVG：深色模式下也用白底黑码，扫描枪才认得出 */
function qrSvg(text: string): string {
  const qr = qrcode(0, 'M')
  qr.addData(text)
  qr.make()
  return qr.createSvgTag({ cellSize: 5, margin: 3, scalable: true })
}
const svg = computed(() => (pair.value ? qrSvg(pair.value.payload) : ''))

/** 还没装 App：扫码下载 */
const showDownload = ref(false)
const downloadSvg = computed(() => (info.value?.app ? qrSvg(info.value.app.url) : ''))

const left = computed(() => {
  if (!pair.value) return ''
  const s = Math.max(0, Math.round((new Date(pair.value.expiresAt).getTime() - now.value) / 1000))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
})

async function remove(id: number) {
  try {
    await bridge.mobileUnpair(id)
    toast(t('ui.mobile.removed'))
    await load()
  } catch (e) {
    fail(e)
  }
}

const fmt = (s: string | null) => (s ? new Date(s).toLocaleString() : '—')

onMounted(load)
onBeforeUnmount(stopWatching)
</script>

<template>
  <h2>{{ t('settings.nav.mobile') }}</h2>
  <section class="card">
    <p class="intro"><Smartphone :size="16" /> {{ t('ui.mobile.intro') }}</p>
    <div v-if="loading" class="state"><Loader :size="15" class="spin" /></div>
    <p v-else-if="!info?.online" class="hint">{{ t('ui.mobile.offline') }}</p>
    <p v-else-if="!info.enabled" class="hint">{{ t('ui.mobile.disabled') }}</p>
    <template v-else>
      <div v-for="m in info.phones" :key="m.id" class="row">
        <span class="label">
          <strong>
            <span class="dot" :class="{ on: m.connected }" />{{ m.name }}
            <span v-if="m.connected" class="tag">{{ t('ui.mobile.online') }}</span>
          </strong>
          <small>{{ t('ui.channels.since', { time: fmt(m.createdAt) }) }} · {{ t('ui.mobile.lastSeen', { time: fmt(m.lastSeen) }) }}</small>
        </span>
        <button type="button" class="btn" @click="remove(m.id)"><Unlink :size="14" /> {{ t('ui.mobile.remove') }}</button>
      </div>

      <div v-if="pair" class="pair">
        <div class="qr" v-html="svg" />
        <div class="side">
          <p class="scan">{{ t('ui.mobile.scan') }}</p>
          <p class="manual">{{ t('ui.mobile.manual') }}</p>
          <dl>
            <dt>{{ t('ui.mobile.server') }}</dt>
            <dd><code>{{ pair.server }}</code></dd>
            <dt>{{ t('ui.mobile.code') }}</dt>
            <dd><code class="code">{{ pair.code }}</code></dd>
          </dl>
          <small>{{ t('ui.mobile.expires', { left }) }} · {{ t('ui.mobile.waiting') }}</small>
        </div>
      </div>
      <div v-else class="row">
        <span class="label">
          <strong>{{ t('ui.mobile.bind') }}</strong>
          <small>{{ t('ui.mobile.bindHint') }}</small>
        </span>
        <button type="button" class="btn primary" @click="showCode">{{ t('ui.mobile.newCode') }}</button>
      </div>
    </template>
  </section>
  <section v-if="info?.enabled && info.app" class="card">
    <div class="row first">
      <span class="label">
        <strong>{{ t('ui.mobile.noApp') }}</strong>
        <small>{{ t('ui.mobile.noAppHint', { v: info.app.versionName }) }}</small>
      </span>
      <button type="button" class="btn" @click="showDownload = !showDownload">{{ showDownload ? t('ui.mobile.hideDownload') : t('ui.mobile.download') }}</button>
    </div>
    <div v-if="showDownload" class="pair">
      <div class="qr" v-html="downloadSvg" />
      <div class="side">
        <p class="scan">{{ t('ui.mobile.downloadScan') }}</p>
        <p class="manual">{{ t('ui.mobile.downloadHint') }}</p>
        <p class="manual"><code class="url">{{ info.app.url }}</code></p>
      </div>
    </div>
  </section>
  <section v-if="info?.enabled" class="card">
    <h3>{{ t('ui.mobile.rulesTitle') }}</h3>
    <ul class="rules">
      <li>{{ t('ui.mobile.rule1') }}</li>
      <li>{{ t('ui.mobile.rule2') }}</li>
      <li>{{ t('ui.mobile.rule3') }}</li>
    </ul>
  </section>
</template>

<style scoped>
/* 设置窗口的样式是 scoped 的，子组件里要自己带一份（和 ChannelsPanel 一样） */
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
.hint {
  margin: 0 0 12px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.intro {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  margin: 10px 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.6;
}
.intro svg {
  flex: none;
  margin-top: 3px;
}
.row {
  display: flex;
  gap: 16px;
  align-items: center;
  justify-content: space-between;
  padding: 12px 0;
  border-top: 1px solid var(--line);
}
.row.first {
  border-top: none;
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
}
.label small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--ink-faint);
}
.dot.on {
  background: var(--thread);
}
.tag {
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--thread-wash);
  color: var(--thread);
  font-size: var(--t-xs);
  font-weight: 500;
}
.btn {
  display: inline-flex;
  flex: none;
  gap: 6px;
  align-items: center;
}
.pair {
  display: flex;
  gap: 20px;
  align-items: center;
  padding: 14px 0;
  border-top: 1px solid var(--line);
}
.qr {
  flex: none;
  width: 172px;
  height: 172px;
  padding: 6px;
  border-radius: var(--r-md);
  background: #ffffff;
}
.qr :deep(svg) {
  display: block;
  width: 100%;
  height: 100%;
}
.side {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}
.scan {
  margin: 0;
  font-weight: 600;
}
.manual {
  margin: 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
dl {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 4px 10px;
  margin: 0;
  font-size: var(--t-sm);
}
dt {
  color: var(--ink-faint);
}
dd {
  margin: 0;
  overflow-wrap: anywhere;
}
.code {
  font-size: var(--t-lg);
  letter-spacing: 0.12em;
}
.url {
  overflow-wrap: anywhere;
  font-size: var(--t-xs);
}
.side small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.rules {
  margin: 0 0 12px;
  padding-left: 18px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.7;
}
.state {
  padding: 16px 0;
  color: var(--ink-faint);
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
