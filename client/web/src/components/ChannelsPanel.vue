<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Copy, Loader, MessagesSquare, Unlink } from '@lucide/vue'
import { bridge } from '../bridge'
import { fail, toast } from '../store'
import type { ChannelsInfo } from '../types'

// 设置 → 钉钉 / 企业微信：在电脑上生成绑定码，员工在钉钉或企业微信里私聊机器人发「绑定 123456」，以后那边发的话就交给这台电脑办
const { t } = useI18n()
const info = ref<ChannelsInfo | null>(null)
const loading = ref(true)
const code = ref<{ code: string; expiresAt: string } | null>(null)
const now = ref(Date.now())
let ticker: number | undefined
let watcher: number | undefined

async function load() {
  try {
    const before = info.value?.bindings.length ?? 0
    info.value = await bridge.channels()
    // 等绑定期间：钉钉那边绑上了就收起绑定码
    if (code.value && info.value.bindings.length > before) {
      code.value = null
      stopWatching()
      toast(t('ui.channels.bound'))
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

async function newCode() {
  try {
    code.value = await bridge.channelBindCode()
    stopWatching()
    ticker = window.setInterval(() => {
      now.value = Date.now()
      if (code.value && new Date(code.value.expiresAt).getTime() <= now.value) {
        code.value = null
        stopWatching()
      }
    }, 1000)
    watcher = window.setInterval(load, 3000)
  } catch (e) {
    fail(e)
  }
}

const left = computed(() => {
  if (!code.value) return ''
  const s = Math.max(0, Math.round((new Date(code.value.expiresAt).getTime() - now.value) / 1000))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
})

async function copyCode() {
  if (!code.value) return
  await navigator.clipboard.writeText(t('ui.channels.command', { code: code.value.code }))
  toast(t('ui.channels.copied'))
}

async function unbind(id: number) {
  try {
    await bridge.channelUnbind(id)
    await load()
  } catch (e) {
    fail(e)
  }
}

const fmt = (s: string | null) => (s ? new Date(s).toLocaleString() : '—')
/** 后台开了哪些通道：钉钉、企业微信 */
const enabled = computed(() => [info.value?.dingtalk && 'dingtalk', info.value?.wecom && 'wecom'].filter(Boolean) as string[])
const channelLabel = (c: string) => t(c === 'wecom' ? 'ui.channels.wecom' : 'ui.channels.dingtalk')
const where = computed(() => enabled.value.map(channelLabel).join(t('ui.channels.or')))

onMounted(load)
onBeforeUnmount(stopWatching)
</script>

<template>
  <h2>{{ t('settings.nav.dingtalk') }}</h2>
  <section class="card">
    <p class="intro"><MessagesSquare :size="16" /> {{ t('ui.channels.intro') }}</p>
    <div v-if="loading" class="state"><Loader :size="15" class="spin" /></div>
    <p v-else-if="!info?.online" class="hint">{{ t('ui.channels.offline') }}</p>
    <p v-else-if="!enabled.length" class="hint">{{ t('ui.channels.disabled') }}</p>
    <template v-else>
      <div v-for="b in info.bindings" :key="b.id" class="row">
        <span class="label">
          <strong><span class="tag">{{ channelLabel(b.channel) }}</span>{{ b.name || t('ui.channels.someone') }}</strong>
          <small>{{ t('ui.channels.since', { time: fmt(b.createdAt) }) }} · {{ t('ui.channels.lastUsed', { time: fmt(b.lastUsed) }) }}</small>
        </span>
        <button type="button" class="btn" @click="unbind(b.id)"><Unlink :size="14" /> {{ t('ui.channels.unbind') }}</button>
      </div>

      <div v-if="code" class="code">
        <div class="digits">{{ code.code }}</div>
        <p>{{ t('ui.channels.howto', { where }) }}</p>
        <button type="button" class="send" @click="copyCode">
          <code>{{ t('ui.channels.command', { code: code.code }) }}</code>
          <Copy :size="13" />
        </button>
        <small>{{ t('ui.channels.expires', { left }) }} · {{ t('ui.channels.waiting') }}</small>
      </div>
      <div v-else class="row">
        <span class="label">
          <strong>{{ info.bindings.length ? t('ui.channels.rebind') : t('ui.channels.bind') }}</strong>
          <small>{{ t('ui.channels.bindHint', { where }) }}</small>
        </span>
        <button type="button" class="btn primary" @click="newCode">{{ t('ui.channels.newCode') }}</button>
      </div>
    </template>
  </section>
  <section v-if="enabled.length" class="card">
    <h3>{{ t('ui.channels.rulesTitle') }}</h3>
    <ul class="rules">
      <li>{{ t('ui.channels.rule1') }}</li>
      <li>{{ t('ui.channels.rule2') }}</li>
      <li>{{ t('ui.channels.rule3') }}</li>
    </ul>
  </section>
</template>

<style scoped>
/* 设置窗口的样式是 scoped 的，子组件里要自己带一份（和 PersonalizePanel 一样） */
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
.row {
  display: flex;
  gap: 16px;
  align-items: center;
  justify-content: space-between;
  padding: 12px 0;
  border-top: 1px solid var(--line);
}
.label {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.label small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.btn {
  display: inline-flex;
  flex: none;
  gap: 6px;
  align-items: center;
}
.tag {
  margin-right: 6px;
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--cloth);
  font-size: var(--t-xs);
  font-weight: 500;
  color: var(--ink-soft);
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
.state {
  padding: 12px 0;
}
.code {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  margin: 8px 0 14px;
  padding: 18px 12px;
  border-radius: var(--r-md);
  background: var(--cloth);
  text-align: center;
}
.digits {
  font-family: var(--mono, ui-monospace, Consolas, monospace);
  font-size: 32px;
  font-weight: 700;
  letter-spacing: 0.3em;
  color: var(--ink);
}
.code p {
  margin: 0;
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.send {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border: 1px dashed var(--line-strong);
  border-radius: var(--r-sm);
  color: var(--ink);
}
.send:hover {
  border-color: var(--indigo);
}
.code small {
  color: var(--ink-faint);
}
.rules {
  margin: 0 0 12px;
  padding-left: 18px;
  font-size: var(--t-sm);
  line-height: 1.8;
  color: var(--ink-soft);
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
