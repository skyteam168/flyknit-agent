<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, ChevronDown, Eye, KeyRound, Lightbulb, Link2, Loader2, Plug, Plus, RefreshCw, Search, TriangleAlert, Unplug, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { loadMcp, state, toast } from '../store'
import { renderMarkdown } from '../markdown'
import type { McpVendor } from '../types'
import KnitMark from './KnitMark.vue'
import VendorIcon from './VendorIcon.vue'

/**
 * MCP 连接器：管理员在后台上架的厂商，一张张卡片排开；点卡片看介绍，点「连接」就能用。
 * 和「技能」是两个入口、两套数据——技能是装在本机的说明书，连接器是在线服务。
 */
const emit = defineEmits<{ use: [text: string] }>()
const { t } = useI18n()

const keyword = ref('')
const filter = ref<'all' | 'connected'>('all')
const openId = ref<string | null>(null)
const refreshing = ref(false)
const values = reactive<Record<string, string>>({})
const reveal = reactive<Record<string, boolean>>({})
const message = ref('')
const working = ref(false)
const toolsOpen = ref(false)

const busy = (v: McpVendor) => v.status === 'connecting' || v.status === 'authorizing'
const connected = (v: McpVendor) => v.status === 'connected'

const list = computed(() => {
  const q = keyword.value.trim().toLowerCase()
  return state.mcp.filter(
    (v) =>
      (filter.value === 'all' || v.enabled) &&
      (!q || `${v.name} ${v.description} ${v.publisher} ${v.category}`.toLowerCase().includes(q)),
  )
})
const connectedCount = computed(() => state.mcp.filter((v) => v.enabled).length)
const current = computed(() => state.mcp.find((v) => v.id === openId.value) ?? null)
/** 员工要自己填的项：管理员预填了的不显示 */
const inputs = computed(() => current.value?.fields.filter((f) => !f.preset) ?? [])

async function refresh() {
  refreshing.value = true
  await loadMcp(true)
  refreshing.value = false
}
onMounted(() => void loadMcp(true))

function open(v: McpVendor) {
  openId.value = v.id
  message.value = v.status === 'failed' || v.status === 'needsauth' ? v.error : ''
  toolsOpen.value = false
  for (const k of Object.keys(values)) delete values[k]
  for (const f of v.fields) values[f.key] = f.secret ? '' : f.value
}
function closeDetail() {
  openId.value = null
}
const close = () => (state.mcpOpen = false)

/** 卡片上的「+」：不用填东西的直接连，要填的先打开详情 */
function quickConnect(v: McpVendor) {
  if (connected(v) || busy(v)) return open(v)
  const missing = v.fields.some((f) => !f.preset && f.required && !f.hasValue)
  if (missing || v.auth === 'oauth') return open(v)
  void connect(v)
}

async function connect(v: McpVendor) {
  working.value = true
  message.value = ''
  try {
    const r = await bridge.connectMcp(v.id, { ...values })
    if (r.vendor) replace(r.vendor)
    if (r.ok) {
      toast(t('ui.mcp.connectedToast', { name: v.name, n: r.vendor?.tools.length ?? 0 }))
      for (const f of v.fields) if (f.secret) values[f.key] = ''
    } else {
      if (openId.value !== v.id) open(v) // 卡片上直接点「+」失败的，打开详情把原因摆出来
      message.value = r.message
    }
  } catch (e) {
    message.value = e instanceof Error ? e.message : String(e)
  } finally {
    working.value = false
  }
}

async function cancel(v: McpVendor) {
  await bridge.cancelMcp(v.id).catch(() => {})
}

async function disconnect(v: McpVendor, forget: boolean) {
  const r = await bridge.disconnectMcp(v.id, forget).catch((e) => {
    toast(e instanceof Error ? e.message : String(e))
    return null
  })
  if (r) replace(r)
  message.value = ''
}

function replace(v: McpVendor) {
  const i = state.mcp.findIndex((x) => x.id === v.id)
  if (i >= 0) state.mcp[i] = v
}

function useExample(text: string) {
  emit('use', text)
}

const detailHtml = computed(() => (current.value?.detail ? renderMarkdown(current.value.detail) : ''))
const statusText = (v: McpVendor) => t(`ui.mcp.status.${v.status}`)
</script>

<template>
  <div class="scrim" @mousedown.self="close" @keydown.esc="openId ? closeDetail() : close()">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.mcp.title')">
      <header>
        <div class="badge"><Plug :size="20" /></div>
        <div class="head-text">
          <h2>{{ t('ui.mcp.title') }}</h2>
          <p>{{ t('ui.mcp.hint') }}</p>
        </div>
        <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="close"><X :size="18" /></button>
      </header>

      <div class="toolbar">
        <div class="seg" role="tablist">
          <button type="button" role="tab" :class="{ on: filter === 'all' }" :aria-selected="filter === 'all'" @click="filter = 'all'">
            {{ t('ui.mcp.all') }} <em>{{ state.mcp.length }}</em>
          </button>
          <button type="button" role="tab" :class="{ on: filter === 'connected' }" :aria-selected="filter === 'connected'" @click="filter = 'connected'">
            {{ t('ui.mcp.mine') }} <em>{{ connectedCount }}</em>
          </button>
        </div>
        <label class="search">
          <Search :size="15" />
          <input v-model="keyword" type="search" :placeholder="t('ui.mcp.search')" />
        </label>
        <button type="button" class="icon-btn" :title="t('ui.mcp.refresh')" :aria-label="t('ui.mcp.refresh')" @click="refresh">
          <RefreshCw :size="16" :class="{ spin: refreshing }" />
        </button>
      </div>

      <div class="body">
        <p v-if="state.mcp.length === 0" class="empty">{{ t('ui.mcp.empty') }}</p>
        <p v-else-if="list.length === 0" class="empty">{{ t('ui.mcp.noMatch') }}</p>
        <div class="grid">
          <div
            v-for="v in list"
            :key="v.id"
            class="card"
            :class="{ on: connected(v), bad: v.status === 'failed' || v.status === 'needsauth' }"
            role="button"
            tabindex="0"
            @click="open(v)"
            @keydown.enter="open(v)"
          >
            <div class="top">
              <VendorIcon :icon="v.icon" :name="v.name" :id="v.id" :size="36" />
              <strong>{{ v.name }}</strong>
              <button
                type="button"
                class="add"
                :class="{ ok: connected(v), warn: v.status === 'failed' || v.status === 'needsauth' }"
                :title="connected(v) ? t('ui.mcp.status.connected') : t('ui.mcp.connect')"
                :aria-label="connected(v) ? t('ui.mcp.status.connected') : t('ui.mcp.connect')"
                @click.stop="quickConnect(v)"
              >
                <Loader2 v-if="busy(v)" :size="16" class="spin" />
                <Check v-else-if="connected(v)" :size="16" />
                <TriangleAlert v-else-if="v.status === 'failed' || v.status === 'needsauth'" :size="15" />
                <Plus v-else :size="17" />
              </button>
            </div>
            <p>{{ v.description || t('ui.mcp.noDescription') }}</p>
          </div>
        </div>
      </div>
    </div>

    <!-- 详情 -->
    <div v-if="current" class="scrim inner" @mousedown.self="closeDetail">
      <div class="detail" role="dialog" aria-modal="true" :aria-label="current.name">
        <!-- 关闭按钮固定在顶栏，内容在下面单独滚动：内容再长也不会顶到按钮上面去 -->
        <div class="detail-head">
          <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="closeDetail"><X :size="18" /></button>
        </div>
        <div class="detail-body">

        <div class="pair">
          <span class="big"><KnitMark :size="56" /></span>
          <span class="dots"><i /><i /><i /></span>
          <VendorIcon :icon="current.icon" :name="current.name" :id="current.id" :size="64" round class="ring" />
        </div>

        <h2>{{ connected(current) ? current.name : t('ui.mcp.connectTo', { name: current.name }) }}</h2>
        <p class="lead">{{ current.description }}</p>
        <p v-if="current.publisher || current.category" class="meta">
          <span v-if="current.publisher">{{ t('ui.mcp.publisher') }}：{{ current.publisher }}</span>
          <span v-if="current.category">{{ current.category }}</span>
          <a v-if="current.homepage" :href="current.homepage" target="_blank" rel="noopener"><Link2 :size="12" /> {{ t('ui.mcp.homepage') }}</a>
        </p>

        <!-- 要填的项 -->
        <form v-if="!connected(current) && inputs.length > 0" class="fields" @submit.prevent="connect(current)">
          <label v-for="f in inputs" :key="f.key">
            <span>
              {{ f.label }}<em v-if="!f.required">{{ t('ui.mcp.optional') }}</em>
              <small v-if="f.hasValue && f.secret" class="saved"><KeyRound :size="11" /> {{ t('ui.mcp.saved') }}</small>
            </span>
            <span class="input">
              <input
                v-model="values[f.key]"
                :type="f.secret && !reveal[f.key] ? 'password' : 'text'"
                :placeholder="f.hasValue && f.secret ? t('ui.mcp.keepSaved') : f.placeholder"
                autocomplete="off"
                spellcheck="false"
              />
              <button v-if="f.secret" type="button" class="eye" :aria-label="t('ui.mcp.show')" @click="reveal[f.key] = !reveal[f.key]"><Eye :size="14" /></button>
            </span>
            <small v-if="f.help" class="help">{{ f.help }}</small>
          </label>
        </form>

        <div class="actions">
          <template v-if="busy(current)">
            <button type="button" class="btn primary" disabled><Loader2 :size="15" class="spin" /> {{ statusText(current) }}</button>
            <button type="button" class="btn" @click="cancel(current)">{{ t('ui.mcp.cancel') }}</button>
          </template>
          <template v-else-if="connected(current)">
            <span class="ok-pill"><Check :size="14" /> {{ t('ui.mcp.connectedN', { n: current.tools.length }) }}</span>
            <button type="button" class="btn" @click="disconnect(current, false)"><Unplug :size="14" /> {{ t('ui.mcp.disconnect') }}</button>
          </template>
          <template v-else>
            <button type="button" class="btn primary" :disabled="working" @click="connect(current)">
              <Link2 :size="15" /> {{ current.auth === 'oauth' ? t('ui.mcp.connectOauth') : t('ui.mcp.connect') }}
            </button>
            <button v-if="current.enabled || current.fields.some((f) => f.hasValue)" type="button" class="btn ghost" @click="disconnect(current, true)">
              {{ t('ui.mcp.forget') }}
            </button>
          </template>
        </div>
        <p v-if="current.auth === 'oauth' && !connected(current) && !busy(current)" class="note">{{ t('ui.mcp.oauthNote') }}</p>
        <p v-if="message || (current.error && !connected(current))" class="error"><TriangleAlert :size="14" /> {{ message || current.error }}</p>

        <!-- 工具 -->
        <div v-if="connected(current) && current.tools.length" class="tools">
          <button type="button" class="tools-head" :aria-expanded="toolsOpen" @click="toolsOpen = !toolsOpen">
            {{ t('ui.mcp.tools', { n: current.tools.length }) }}
            <ChevronDown :size="15" :class="{ up: toolsOpen }" />
          </button>
          <ul v-if="toolsOpen">
            <li v-for="tool in current.tools" :key="tool.name">
              <code>{{ tool.title || tool.name }}</code>
              <em v-if="tool.readOnly">{{ t('ui.mcp.readOnly') }}</em>
              <small>{{ tool.description }}</small>
            </li>
          </ul>
        </div>

        <div v-if="detailHtml" class="intro md" v-html="detailHtml" />

        <!-- 试试这样用 -->
        <section v-if="current.examples.length" class="examples">
          <h3><Lightbulb :size="16" /> {{ t('ui.mcp.tryThis') }}</h3>
          <button v-for="ex in current.examples" :key="ex" type="button" class="example" :title="t('ui.mcp.useExample')" @click="useExample(ex)">
            “{{ ex }}”
          </button>
        </section>
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
  background: color-mix(in srgb, var(--ink) 20%, transparent);
}
.dialog {
  display: flex;
  flex-direction: column;
  width: min(980px, 100%);
  height: min(680px, calc(100vh - 32px));
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
.toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--line);
}
.seg {
  display: inline-flex;
  padding: 3px;
  border-radius: 9px;
  background: var(--cloth-sunk);
}
.seg button {
  padding: 4px 12px;
  border-radius: 7px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.seg button.on {
  background: var(--cloth);
  color: var(--ink);
  font-weight: 500;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.08);
}
.seg em {
  margin-left: 4px;
  color: var(--ink-faint);
  font-style: normal;
  font-size: calc(11px * var(--font-scale));
}
.search {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  color: var(--ink-faint);
}
.search input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: 0;
  background: none;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
}
.body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 14px 2px 4px;
}
.empty {
  margin: 40px 0;
  text-align: center;
  color: var(--ink-faint);
  font-size: var(--t-sm);
  line-height: 1.6;
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 12px;
}
.card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 16px 16px 14px;
  border: 1px solid var(--line);
  border-radius: 14px;
  background: var(--cloth-sunk);
  cursor: pointer;
  transition: border-color 120ms, background 120ms, transform 120ms;
}
.card:hover,
.card:focus-visible {
  border-color: var(--line-strong);
  background: var(--cloth);
  outline: none;
}
.card.on {
  border-color: color-mix(in srgb, var(--thread) 45%, var(--line));
}
.top {
  display: flex;
  align-items: center;
  gap: 10px;
}
.top strong {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--t-md);
  font-weight: 600;
}
.add {
  display: grid;
  place-items: center;
  flex: none;
  width: 30px;
  height: 30px;
  border-radius: 8px;
  background: var(--chip);
  color: var(--ink-soft);
}
.add:hover {
  background: var(--line-strong);
  color: var(--ink);
}
.add.ok {
  background: var(--thread-wash);
  color: var(--thread);
}
.add.warn {
  background: var(--amber-wash);
  color: var(--amber);
}
.card p {
  margin: 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  line-height: 1.6;
  overflow: hidden;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}

/* ---- 详情 ---- */
.detail {
  display: flex;
  flex-direction: column;
  width: min(600px, 100%);
  max-height: calc(100vh - 48px);
  overflow: hidden;
  border-radius: 22px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  animation: pop 160ms ease-out;
  text-align: center;
}
.detail-head {
  display: flex;
  justify-content: flex-end;
  flex: none;
  padding: 12px 12px 0;
}
.detail-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 0 28px 22px;
}
.pair {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 14px;
  margin: 0 0 16px;
}
.big {
  display: grid;
  place-items: center;
  width: 64px;
  height: 64px;
  border-radius: 50%;
  overflow: hidden;
  background: var(--cloth-sunk);
}
.ring {
  box-shadow: 0 0 0 1px var(--line), 0 2px 8px rgba(0, 0, 0, 0.12);
}
.dots {
  display: inline-flex;
  gap: 5px;
}
.dots i {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--ink-faint);
  opacity: 0.6;
}
.detail h2 {
  font-size: calc(20px * var(--font-scale));
}
.lead {
  margin: 10px auto 0;
  max-width: 460px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.7;
}
.meta {
  display: flex;
  justify-content: center;
  flex-wrap: wrap;
  gap: 4px 14px;
  margin: 8px 0 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.meta a {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  color: var(--indigo);
}
.fields {
  display: flex;
  flex-direction: column;
  gap: 12px;
  margin: 18px auto 0;
  max-width: 420px;
  text-align: left;
}
.fields label {
  display: flex;
  flex-direction: column;
  gap: 5px;
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.fields em {
  margin-left: 6px;
  color: var(--ink-faint);
  font-style: normal;
  font-size: var(--t-xs);
}
.saved {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  margin-left: 8px;
  color: var(--thread);
  font-size: var(--t-xs);
}
.input {
  display: flex;
  align-items: center;
  height: 36px;
  padding: 0 4px 0 10px;
  border: 1px solid var(--line-strong);
  border-radius: 9px;
  background: var(--cloth);
}
.input:focus-within {
  border-color: var(--indigo);
}
.input input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: 0;
  background: none;
  color: var(--ink);
  font: inherit;
  font-family: var(--font-code);
  font-size: var(--t-sm);
}
.eye {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.help {
  color: var(--ink-faint);
  font-size: var(--t-xs);
  line-height: 1.5;
}
.actions {
  display: flex;
  justify-content: center;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
  margin-top: 20px;
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 36px;
  padding: 0 18px;
  border-radius: 10px;
  background: var(--chip);
  color: var(--ink);
  font-size: var(--t-sm);
  font-weight: 500;
}
.btn.primary {
  background: var(--action);
  color: var(--action-ink);
  transition: background 120ms;
}
.btn.primary:hover:not(:disabled) {
  background: var(--action-hover);
}
.btn.ghost {
  background: none;
  color: var(--ink-faint);
}
.btn:disabled {
  opacity: 0.7;
  cursor: default;
}
.ok-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 32px;
  padding: 0 14px;
  border-radius: 16px;
  background: var(--thread-wash);
  color: var(--thread);
  font-size: var(--t-sm);
}
.note {
  margin: 10px 0 0;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.error {
  display: flex;
  align-items: flex-start;
  justify-content: center;
  gap: 6px;
  margin: 12px auto 0;
  max-width: 460px;
  padding: 8px 12px;
  border-radius: 9px;
  background: var(--red-wash, color-mix(in srgb, var(--red) 10%, transparent));
  color: var(--red);
  font-size: var(--t-xs);
  line-height: 1.5;
  text-align: left;
  white-space: pre-wrap;
  word-break: break-word;
}
.error svg {
  flex: none;
  margin-top: 2px;
}
.tools {
  margin: 18px 0 0;
  border: 1px solid var(--line);
  border-radius: 12px;
  text-align: left;
}
.tools-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  width: 100%;
  padding: 10px 14px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.tools-head svg {
  transition: transform 150ms;
}
.tools-head .up {
  transform: rotate(180deg);
}
.tools ul {
  margin: 0;
  padding: 0 14px 10px;
  list-style: none;
  max-height: 220px;
  overflow-y: auto;
}
.tools li {
  padding: 7px 0;
  border-top: 1px solid var(--line);
  font-size: var(--t-xs);
}
.tools code {
  font-family: var(--font-code);
  color: var(--ink);
}
.tools li em {
  margin-left: 6px;
  padding: 0 6px;
  border-radius: 7px;
  background: var(--thread-wash);
  color: var(--thread);
  font-style: normal;
}
.tools small {
  display: block;
  margin-top: 2px;
  color: var(--ink-faint);
  line-height: 1.5;
}
.intro {
  margin: 18px 0 0;
  padding: 12px 16px;
  border-radius: 12px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.7;
  text-align: left;
}
.intro :deep(p) {
  margin: 0 0 8px;
}
.intro :deep(p:last-child) {
  margin-bottom: 0;
}
.examples {
  margin: 22px 0 0;
  text-align: left;
}
.examples h3 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 10px;
  font-size: var(--t-sm);
  font-weight: 600;
}
.example {
  display: block;
  width: 100%;
  margin-bottom: 8px;
  padding: 12px 16px;
  border-radius: 12px;
  background: var(--cloth-sunk);
  color: var(--ink);
  font-size: var(--t-sm);
  font-weight: 500;
  line-height: 1.6;
  text-align: left;
}
.example:hover {
  background: var(--chip);
}
.spin {
  animation: spin 900ms linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
@media (max-width: 640px) {
  .grid {
    grid-template-columns: 1fr;
  }
  .detail-body {
    padding: 0 18px 18px;
  }
}
</style>
