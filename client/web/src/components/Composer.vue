<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowLeftRight,
  ArrowUp,
  Check,
  ChevronDown,
  FileText,
  ImageIcon,
  Languages,
  MessageSquare,
  Paperclip,
  Puzzle,
  Square,
  Wrench,
  X,
} from '@lucide/vue'
import Popover from './Popover.vue'
import ModelPicker from './ModelPicker.vue'
import WorkspacePicker from './WorkspacePicker.vue'
import PermissionPicker from './PermissionPicker.vue'
import ConfirmBar from './ConfirmBar.vue'
import CompactingCard from './CompactingCard.vue'
import UsageWarning from './UsageWarning.vue'
import { addPastedImage, current, currentState, draftMode, pickFiles, send, setMode, setTranslate, state, stop } from '../store'
import type { Mode } from '../types'

const props = defineProps<{ home?: boolean }>()
const { t } = useI18n()
const text = ref('')
const box = ref<HTMLTextAreaElement>()

const mode = computed<Mode>(() => current.value?.mode ?? draftMode.mode)
const from = computed(() => current.value?.translateFrom ?? draftMode.translateFrom)
const to = computed(() => current.value?.translateTo ?? draftMode.translateTo)
const busy = computed(() => currentState.value?.busy ?? false)
const offline = computed(() => state.app !== null && !state.app.connected)
const canSend = computed(() => !busy.value && !offline.value && (text.value.trim().length > 0 || state.pending.length > 0))

const langs = ['zh-CN', 'vi', 'en', 'km', 'th', 'id', 'ja', 'ko']
const modes: { key: Mode; icon: unknown }[] = [
  { key: 'agent', icon: Wrench },
  { key: 'chat', icon: MessageSquare },
  { key: 'translate', icon: Languages },
]
const modeIcon = computed(() => modes.find((m) => m.key === mode.value)!.icon)

function resize() {
  const el = box.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 260)}px`
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

async function chooseMode(m: Mode, close: () => void) {
  close()
  await setMode(m)
  focus()
}

function useSkill(name: string, close?: () => void) {
  close?.()
  const prefix = t('ui.skills.use', { name })
  if (!text.value.startsWith(prefix)) text.value = prefix + text.value
  void nextTick(() => {
    resize()
    focus()
  })
}

/** 由首页快捷入口或外部调用填入文字 */
function fill(value: string) {
  text.value = value
  void nextTick(() => {
    resize()
    box.value?.focus()
    box.value?.setSelectionRange(value.length, value.length)
  })
}
function focusInput() {
  box.value?.focus()
}

defineExpose({ fill, useSkill, focusInput })

const focus = () => box.value?.focus()
onMounted(() => {
  window.addEventListener('flyknit:focus-input', focus)
  focus()
})
onBeforeUnmount(() => window.removeEventListener('flyknit:focus-input', focus))
</script>

<template>
  <div class="composer-wrap" :class="{ home: props.home }">
    <CompactingCard v-if="!props.home" />
    <ConfirmBar v-if="!props.home" />
    <UsageWarning />
    <div class="card" :class="{ busy }">
      <div v-if="state.pending.length" class="pending">
        <div v-for="(a, i) in state.pending" :key="a.localPath + i" class="chip" :title="a.localPath">
          <img v-if="a.preview" :src="a.preview" alt="" />
          <component :is="a.mime.startsWith('image/') ? ImageIcon : FileText" v-else :size="15" />
          <span>{{ a.fileName }}</span>
          <button type="button" :aria-label="t('menu.delete')" @click="state.pending.splice(i, 1)"><X :size="13" /></button>
        </div>
      </div>

      <textarea
        ref="box"
        v-model="text"
        :rows="props.home ? 3 : 1"
        :placeholder="offline ? t('input.disconnected') : mode === 'translate' ? t('input.placeholderTranslate') : t('input.placeholder')"
        @input="resize"
        @keydown="onKey"
        @paste="onPaste"
      />

      <div class="bar">
        <!-- 模式 -->
        <Popover :width="260">
          <template #trigger="{ toggle, open }">
            <button type="button" class="tool-btn mode-btn" :class="{ open }" @click="toggle">
              <component :is="modeIcon" :size="15" />
              <span>{{ t(`mode.${mode}`) }}</span>
              <ChevronDown :size="14" class="chev" />
            </button>
          </template>
          <template #default="{ close }">
            <p v-if="current && current.messageCount > 0" class="pop-note">{{ t('ui.modeLocked') }}</p>
            <button v-for="m in modes" :key="m.key" type="button" class="pop-opt" @click="chooseMode(m.key, close)">
              <component :is="m.icon" :size="16" class="ico" :class="{ on: mode === m.key }" />
              <span class="pop-text">
                <strong>{{ t(`mode.${m.key}`) }}</strong>
                <small>{{ t(`mode.${m.key}Hint`) }}</small>
              </span>
              <Check v-if="mode === m.key" :size="15" class="tick" />
            </button>
          </template>
        </Popover>

        <!-- 模型 -->
        <ModelPicker :mode="mode" />

        <!-- 技能（办事模式） -->
        <Popover v-if="mode === 'agent'" :width="300">
          <template #trigger="{ toggle, open }">
            <button type="button" class="tool-btn" :class="{ open }" :title="t('ui.skills.button')" @click="toggle">
              <Puzzle :size="15" />
              <span class="hide-narrow">{{ t('ui.skills.button') }}</span>
            </button>
          </template>
          <template #default="{ close }">
            <p v-if="state.skills.length === 0" class="pop-note">{{ t('ui.skills.empty') }}</p>
            <button v-for="s in state.skills" :key="s.name" type="button" class="pop-opt" @click="useSkill(s.name, close)">
              <Puzzle :size="15" class="ico" />
              <span class="pop-text">
                <strong>{{ s.name }}</strong>
                <small>{{ s.description }}</small>
              </span>
            </button>
          </template>
        </Popover>

        <!-- 翻译语言 -->
        <div v-if="mode === 'translate'" class="langs">
          <select :value="from" :aria-label="t('translate.from')" @change="setTranslate(($event.target as HTMLSelectElement).value, to)">
            <option value="auto">{{ t('translate.auto') }}</option>
            <option v-for="l in langs" :key="l" :value="l">{{ t(`translate.langs.${l}`) }}</option>
          </select>
          <button type="button" class="swap" :disabled="from === 'auto'" :title="t('translate.swap')" @click="swap">
            <ArrowLeftRight :size="14" />
          </button>
          <select :value="to" :aria-label="t('translate.to')" @change="setTranslate(from, ($event.target as HTMLSelectElement).value)">
            <option v-for="l in langs" :key="l" :value="l">{{ t(`translate.langs.${l}`) }}</option>
          </select>
        </div>

        <span class="spacer" />
        <button type="button" class="tool-btn icon-only" :title="t('input.attach')" @click="pickFiles">
          <Paperclip :size="17" />
        </button>
        <button v-if="busy" type="button" class="send stop" :title="t('input.stop')" @click="stop">
          <Square :size="13" fill="currentColor" />
        </button>
        <button v-else type="button" class="send" :disabled="!canSend" :title="t('input.send')" @click="submit">
          <ArrowUp :size="18" />
        </button>
      </div>
    </div>
    <div class="foot">
      <template v-if="mode === 'agent'">
        <WorkspacePicker />
        <PermissionPicker />
      </template>
      <span class="spacer" />
      <span class="ai-note">{{ t('ui.safety.other') }}</span>
    </div>
  </div>
</template>

<style scoped>
.composer-wrap {
  padding: 0 32px 14px;
}
.composer-wrap.home {
  padding: 0;
}
.card {
  max-width: var(--column);
  margin: 0 auto;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-xl);
  background: var(--cloth);
  box-shadow: var(--shadow-card);
  transition:
    border-color 120ms,
    box-shadow 120ms;
}
.home .card {
  max-width: none;
}
.card:focus-within {
  border-color: color-mix(in srgb, var(--indigo) 55%, var(--line-strong));
  box-shadow:
    0 0 0 4px var(--indigo-wash),
    var(--shadow-card);
}
.pending {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 12px 14px 0;
}
.chip {
  display: flex;
  align-items: center;
  gap: 6px;
  max-width: 240px;
  height: 32px;
  padding: 0 4px 0 8px;
  border-radius: 8px;
  background: var(--chip);
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
  min-height: 54px;
  max-height: 260px;
  padding: 16px 18px 6px;
  border: 0;
  outline: 0;
  resize: none;
  background: transparent;
  line-height: 1.6;
}
.home textarea {
  min-height: 96px;
}
textarea::placeholder {
  color: var(--ink-faint);
}
.bar {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
  padding: 6px 10px 10px;
}
.tool-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 10px;
  border-radius: 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 500;
}
.tool-btn:hover,
.tool-btn.open {
  background: var(--chip);
  color: var(--ink);
}
.mode-btn {
  background: var(--chip);
  color: var(--ink);
}
.icon-only {
  width: 34px;
  padding: 0;
  justify-content: center;
}
.chev {
  color: var(--ink-faint);
}
.langs {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-left: 4px;
}
.langs select {
  height: 30px;
  padding: 0 6px;
  border-radius: 8px;
  border: 1px solid var(--line);
  background: var(--cloth);
  font-size: var(--t-sm);
}
.swap {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  color: var(--ink-soft);
}
.swap:disabled {
  opacity: 0.35;
  cursor: default;
}
.spacer {
  flex: 1;
}
.send {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  margin-left: 4px;
  border-radius: 50%;
  background: var(--pill);
  color: var(--pill-ink);
}
.send:hover:not(:disabled) {
  background: var(--indigo);
  color: #fff;
}
.send:disabled {
  background: var(--line-strong);
  color: var(--cloth);
  cursor: default;
}
.send.stop {
  background: var(--pill);
}
.foot {
  display: flex;
  align-items: center;
  gap: 4px;
  max-width: var(--column);
  min-height: 26px;
  margin: 6px auto 0;
  padding: 0 2px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.home .foot {
  max-width: none;
}
.safety {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}
.pop-note {
  margin: 4px 8px 6px;
  font-size: var(--t-xs);
  color: var(--amber);
}
.pop-opt {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 8px;
  border-radius: 8px;
  text-align: left;
}
.pop-opt:hover {
  background: var(--chip);
}
.pop-opt .ico {
  flex: none;
  color: var(--ink-faint);
}
.pop-opt .ico.on {
  color: var(--indigo);
}
.pop-text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.pop-text strong {
  font-weight: 500;
  font-size: var(--t-sm);
}
.pop-text small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.tick {
  flex: none;
  color: var(--indigo);
}
@media (max-width: 720px) {
  .composer-wrap {
    padding: 0 12px 10px;
  }
  .hide-narrow,
  .ai-note {
    display: none;
  }
}
</style>
