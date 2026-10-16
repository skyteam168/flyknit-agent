<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Copy, ExternalLink, Loader2, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { openPath, state } from '../store'
import { formatSize } from '../library'
import { lineCount } from '../pasted'

// 在客户端里看文本附件的全文：粘贴的文本、txt / md 等。不经过右侧分屏预览
const { t } = useI18n()
const file = computed(() => state.textView!)
const text = ref<string | null>(null)
const notice = ref('')
const error = ref('')
const loading = ref(false)
const copied = ref(false)
const root = ref<HTMLElement>()

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') {
    e.stopPropagation()
    close()
  }
}

onBeforeUnmount(() => window.removeEventListener('keydown', onKey, true))

onMounted(async () => {
  window.addEventListener('keydown', onKey, true) // 焦点在哪都能按 Esc 关
  root.value?.focus()
  const a = file.value
  if (a.pasteText !== undefined) {
    text.value = a.pasteText
    return
  }
  loading.value = true
  try {
    const doc = await bridge.previewFile(a.localPath)
    if (state.textView !== a) return // 已经关掉或换了一个
    if (typeof doc.text === 'string' && !doc.error) {
      text.value = doc.text
      notice.value = doc.notice ?? ''
    } else {
      error.value = doc.error || t('ui.paste.failed')
    }
  } catch (e) {
    error.value = String(e)
  } finally {
    loading.value = false
  }
})

const meta = computed(() => {
  const parts = [formatSize(file.value.size)]
  if (text.value !== null) parts.push(t('ui.paste.lines', { n: lineCount(text.value) }))
  return parts.join(' · ')
})

function close() {
  state.textView = null
}

async function copy() {
  if (text.value === null) return
  await navigator.clipboard.writeText(text.value)
  copied.value = true
  setTimeout(() => (copied.value = false), 1600)
}

function external() {
  void openPath(file.value.localPath)
  close()
}
</script>

<template>
  <div class="scrim" @mousedown.self="close">
    <section ref="root" class="dialog" role="dialog" aria-modal="true" :aria-label="file.fileName" tabindex="-1">
      <header>
        <div class="titles">
          <h2 :title="file.fileName">{{ file.fileName }}</h2>
          <p>
            {{ meta }}<template v-if="file.pasted"> · <span class="badge">{{ t('ui.paste.badge') }}</span></template>
          </p>
        </div>
        <button v-if="text !== null" type="button" class="icon" :title="copied ? t('ui.paste.copied') : t('ui.paste.copy')" @click="copy">
          <Check v-if="copied" :size="16" />
          <Copy v-else :size="16" />
        </button>
        <button type="button" class="icon" :title="t('ui.paste.close')" :aria-label="t('ui.paste.close')" @click="close"><X :size="17" /></button>
      </header>
      <div v-if="loading" class="state"><Loader2 :size="16" class="spin" /> {{ t('ui.paste.loading') }}</div>
      <div v-else-if="error" class="state">
        <span>{{ error }}</span>
        <button type="button" class="btn" @click="external"><ExternalLink :size="14" /> {{ t('ui.paste.openExternal') }}</button>
      </div>
      <template v-else>
        <p v-if="notice" class="notice">{{ notice }}</p>
        <pre class="body">{{ text }}</pre>
      </template>
    </section>
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
  display: flex;
  flex-direction: column;
  width: min(760px, 100%);
  max-height: 84vh;
  padding: 18px 18px 16px 22px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  outline: 0;
}
header {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin-bottom: 12px;
}
.titles {
  flex: 1;
  min-width: 0;
}
h2 {
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 600;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.titles p {
  margin: 4px 0 0;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
.badge {
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--chip);
  font-size: 11px;
  font-weight: 600;
  color: var(--ink-soft);
}
.icon {
  display: grid;
  place-items: center;
  flex: none;
  width: 30px;
  height: 30px;
  border-radius: var(--r-sm);
  color: var(--ink-faint);
}
.icon:hover {
  background: var(--chip);
  color: var(--ink);
}
.notice {
  margin: 0 0 8px;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
.body {
  flex: 1;
  min-height: 120px;
  margin: 0;
  padding: 14px 16px;
  overflow: auto;
  border: 1px solid var(--line);
  border-radius: var(--r-md, 10px);
  background: var(--paper, var(--chip));
  font-family: var(--mono, ui-monospace, Consolas, monospace);
  font-size: 13px;
  line-height: 1.65;
  white-space: pre-wrap;
  word-break: break-word;
  color: var(--ink);
  user-select: text;
}
.state {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 28px 4px;
  color: var(--ink-soft);
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
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
