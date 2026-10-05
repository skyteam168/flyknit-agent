<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowLeftRight, ArrowUp, FileText, ImageIcon, Paperclip, Square, X } from '@lucide/vue'
import { addPastedImage, current, currentState, draftMode, pickFiles, send, setTranslate, state, stop } from '../store'

const { t } = useI18n()
const text = ref('')
const box = ref<HTMLTextAreaElement>()

const mode = computed(() => current.value?.mode ?? draftMode.mode)
const from = computed(() => current.value?.translateFrom ?? draftMode.translateFrom)
const to = computed(() => current.value?.translateTo ?? draftMode.translateTo)
const busy = computed(() => currentState.value?.busy ?? false)
const offline = computed(() => state.app !== null && !state.app.connected)
const canSend = computed(() => !busy.value && !offline.value && (text.value.trim().length > 0 || state.pending.length > 0))

const langs = ['zh-CN', 'vi', 'en', 'km', 'th', 'id', 'ja', 'ko']

function resize() {
  const el = box.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 240)}px`
}

async function submit() {
  if (!canSend.value) return
  const value = text.value
  text.value = ''
  await nextTick()
  resize()
  await send(value)
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault()
    void submit()
  }
}

function onPaste(e: ClipboardEvent) {
  const items = [...(e.clipboardData?.items ?? [])].filter((i) => i.kind === 'file' && i.type.startsWith('image/'))
  if (items.length === 0) return
  e.preventDefault()
  for (const i of items) {
    const blob = i.getAsFile()
    if (blob) void addPastedImage(blob)
  }
}

function swap() {
  if (from.value === 'auto') return
  void setTranslate(to.value, from.value)
}

function remove(i: number) {
  state.pending.splice(i, 1)
}

/** 由欢迎页示例或外部调用填入文字 */
function fill(value: string) {
  text.value = value
  void nextTick(() => {
    resize()
    box.value?.focus()
  })
}
defineExpose({ fill })

const focus = () => box.value?.focus()
onMounted(() => {
  window.addEventListener('flyknit:focus-input', focus)
  focus()
})
onBeforeUnmount(() => window.removeEventListener('flyknit:focus-input', focus))
</script>

<template>
  <div class="composer-wrap">
    <div class="composer" :class="{ translate: mode === 'translate' }">
      <div v-if="mode === 'translate'" class="lang-bar">
        <select :value="from" :aria-label="t('translate.from')" @change="setTranslate(($event.target as HTMLSelectElement).value, to)">
          <option value="auto">{{ t('translate.auto') }}</option>
          <option v-for="l in langs" :key="l" :value="l">{{ t(`translate.langs.${l}`) }}</option>
        </select>
        <button type="button" class="icon-btn" :disabled="from === 'auto'" :title="t('translate.swap')" @click="swap">
          <ArrowLeftRight :size="15" />
        </button>
        <select :value="to" :aria-label="t('translate.to')" @change="setTranslate(from, ($event.target as HTMLSelectElement).value)">
          <option v-for="l in langs" :key="l" :value="l">{{ t(`translate.langs.${l}`) }}</option>
        </select>
      </div>

      <div v-if="state.pending.length" class="pending">
        <div v-for="(a, i) in state.pending" :key="a.localPath + i" class="chip" :title="a.localPath">
          <img v-if="a.preview" :src="a.preview" alt="" />
          <component :is="a.mime.startsWith('image/') ? ImageIcon : FileText" v-else :size="15" />
          <span>{{ a.fileName }}</span>
          <button type="button" :aria-label="t('menu.delete')" @click="remove(i)"><X :size="13" /></button>
        </div>
      </div>

      <textarea
        ref="box"
        v-model="text"
        rows="1"
        :placeholder="offline ? t('input.disconnected') : mode === 'translate' ? t('input.placeholderTranslate') : t('input.placeholder')"
        @input="resize"
        @keydown="onKey"
        @paste="onPaste"
      />

      <div class="bar">
        <button type="button" class="icon-btn" :title="t('input.attach')" @click="pickFiles">
          <Paperclip :size="18" />
        </button>
        <span class="spacer" />
        <button v-if="busy" type="button" class="send stop" :title="t('input.stop')" @click="stop">
          <Square :size="14" fill="currentColor" />
        </button>
        <button v-else type="button" class="send" :disabled="!canSend" :title="t('input.send')" @click="submit">
          <ArrowUp :size="18" />
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.composer-wrap {
  padding: 0 32px 20px;
}
.composer {
  max-width: var(--column);
  margin: 0 auto;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: 0 1px 2px rgba(26, 36, 51, 0.05);
  transition: border-color 120ms;
}
.composer:focus-within {
  border-color: var(--indigo);
  box-shadow: 0 0 0 3px var(--indigo-wash);
}
.lang-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px 0;
}
.lang-bar select {
  height: 30px;
  padding: 0 8px;
  border-radius: var(--r-sm);
  border: 1px solid var(--line);
  background: var(--cloth-sunk);
  font-size: var(--t-sm);
  font-weight: 500;
}
.lang-bar .icon-btn:disabled {
  opacity: 0.4;
  cursor: default;
}
.pending {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 10px 12px 0;
}
.chip {
  display: flex;
  align-items: center;
  gap: 6px;
  max-width: 240px;
  height: 32px;
  padding: 0 4px 0 8px;
  border-radius: var(--r-sm);
  background: var(--cloth-sunk);
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.chip img {
  width: 22px;
  height: 22px;
  object-fit: cover;
  border-radius: 4px;
}
.chip span {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.chip button {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: 4px;
  color: var(--ink-faint);
}
.chip button:hover {
  background: var(--line);
  color: var(--ink);
}
textarea {
  display: block;
  width: 100%;
  min-height: 52px;
  max-height: 240px;
  padding: 14px 16px 4px;
  border: 0;
  outline: 0;
  resize: none;
  background: transparent;
  line-height: 1.55;
}
textarea::placeholder {
  color: var(--ink-faint);
}
.bar {
  display: flex;
  align-items: center;
  padding: 4px 8px 8px;
}
.spacer {
  flex: 1;
}
.send {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 50%;
  background: var(--indigo);
  color: #fff;
}
.send:hover:not(:disabled) {
  background: var(--indigo-hover);
}
.send:disabled {
  background: var(--line-strong);
  cursor: default;
}
.send.stop {
  background: var(--ink);
  color: var(--loom);
}
@media (max-width: 720px) {
  .composer-wrap {
    padding: 0 12px 12px;
  }
}
</style>
