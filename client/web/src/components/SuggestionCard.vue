<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowRight, Check, Lightbulb, X } from '@lucide/vue'
import { openConversation, startSuggestedTask } from '../store'

/** AI 发现的范围外的事：一张卡片，员工点「新开任务」才在新对话里开始 */
const props = defineProps<{ callId: string; args: string; conversationId: string }>()
const { t } = useI18n()

const parsed = computed(() => {
  try {
    const o = JSON.parse(props.args || '{}') as { title?: string; prompt?: string; reason?: string }
    return { title: o.title ?? '', prompt: o.prompt ?? '', reason: o.reason ?? '' }
  } catch {
    return { title: '', prompt: '', reason: '' }
  }
})

// 点过的、忽略过的记在本机（按调用 ID），重开对话还在
const KEY = 'flyknit-suggestions'
function readAll(): Record<string, string> {
  try {
    return JSON.parse(localStorage.getItem(KEY) ?? '{}') as Record<string, string>
  } catch {
    return {}
  }
}
const status = ref<string | undefined>(readAll()[props.callId])
function remember(value: string) {
  status.value = value
  try {
    localStorage.setItem(KEY, JSON.stringify({ ...readAll(), [props.callId]: value }))
  } catch {
    // 存不下来就只在这次显示
  }
}

const started = computed(() => status.value?.startsWith('started:') ? status.value.slice(8) : null)
const busy = ref(false)
const showPrompt = ref(false)

async function start() {
  busy.value = true
  const id = await startSuggestedTask(props.conversationId, parsed.value.title, parsed.value.prompt)
  busy.value = false
  if (id) remember(`started:${id}`)
}
</script>

<template>
  <div v-if="parsed.title && status !== 'dismissed'" class="suggest">
    <div class="head">
      <Lightbulb :size="15" class="bulb" />
      <span class="label">{{ t('ui.suggest.label') }}</span>
      <strong class="title">{{ parsed.title }}</strong>
    </div>
    <p v-if="parsed.reason" class="reason">{{ parsed.reason }}</p>
    <button type="button" class="peek" @click="showPrompt = !showPrompt">{{ t(showPrompt ? 'ui.suggest.hidePrompt' : 'ui.suggest.showPrompt') }}</button>
    <pre v-if="showPrompt" class="prompt">{{ parsed.prompt }}</pre>
    <div class="actions">
      <template v-if="started">
        <span class="done"><Check :size="14" /> {{ t('ui.suggest.started') }}</span>
        <button type="button" class="btn" @click="openConversation(started)">{{ t('ui.suggest.open') }} <ArrowRight :size="13" /></button>
      </template>
      <template v-else>
        <button type="button" class="btn ghost" @click="remember('dismissed')"><X :size="13" /> {{ t('ui.suggest.dismiss') }}</button>
        <button type="button" class="btn primary" :disabled="busy" @click="start">{{ t('ui.suggest.start') }} <ArrowRight :size="13" /></button>
      </template>
    </div>
  </div>
</template>

<style scoped>
.suggest {
  margin: 6px 0 10px;
  padding: 10px 12px;
  border: 1px solid var(--amber);
  border-radius: var(--r-md);
  background: var(--amber-wash);
}
.head {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.bulb {
  flex: none;
  color: var(--amber);
}
.label {
  flex: none;
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.title {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  font-size: var(--t-sm);
}
.reason {
  margin: 6px 0 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.peek {
  margin-top: 4px;
  color: var(--indigo);
  font-size: var(--t-xs);
}
.prompt {
  margin: 6px 0 0;
  padding: 8px;
  border-radius: var(--r-sm);
  background: var(--cloth);
  white-space: pre-wrap;
  font-family: var(--font);
  font-size: var(--t-xs);
}
.actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 8px;
}
.done {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-right: auto;
  color: var(--thread);
  font-size: var(--t-sm);
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-sm);
}
.btn.ghost {
  border-color: transparent;
  background: transparent;
  color: var(--ink-soft);
}
.btn.primary {
  border-color: var(--action);
  background: var(--action);
  color: var(--action-ink);
}
.btn:disabled {
  opacity: 0.5;
}
</style>
