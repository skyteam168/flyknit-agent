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
  Loader2,
  MessageSquare,
  Mic,
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
import {
  addPastedImage,
  cancelSpeech,
  current,
  currentState,
  draftMode,
  finishSpeech,
  onSpeechText,
  pickFiles,
  send,
  setMode,
  setTranslate,
  startSpeech,
  state,
  stop,
} from '../store'
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
  // 录音时键盘先归录音条：回车结束并识别，Esc 放弃
  if (speech.value.phase !== 'idle') {
    if (e.key === 'Escape') {
      e.preventDefault()
      cancelSpeech()
    } else if (e.key === 'Enter' && !e.isComposing) {
      e.preventDefault()
      if (speech.value.phase === 'recording') void finishSpeech()
    }
    return
  }
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

// ---- 语音输入 ----
const speech = computed(() => state.speech)
const micReady = computed(() => state.app?.micAvailable !== false)

/** 00:00 / 01:23 */
const spoken = computed(() => {
  const s = Math.floor(speech.value.elapsedMs / 1000)
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`
})

/** 快到上限时把计时变红，提醒用户收一下 */
const nearLimit = computed(() => speech.value.maxMs > 0 && speech.value.elapsedMs > speech.value.maxMs - 20000)

/** 一排竖条，中间高两头低，再乘上当前响度 */
const bars = Array.from({ length: 13 }, (_, i) => 0.45 + 0.55 * Math.sin((Math.PI * (i + 1)) / 14))

function barHeight(shape: number) {
  const level = speech.value.phase === 'recording' ? speech.value.level : 0
  return `${Math.round(3 + shape * level * 15)}px`
}

/** 识别出来的文字插到光标处，而不是覆盖已经打了一半的内容 */
function insert(said: string) {
  const el = box.value
  const at = el ? (el.selectionStart ?? text.value.length) : text.value.length
  const before = text.value.slice(0, at)
  const after = text.value.slice(at)
  const glue = before && !/\s$/.test(before) ? ' ' : ''
  text.value = before + glue + said + after
  const caret = (before + glue + said).length
  void nextTick(() => {
    resize()
    el?.focus()
    el?.setSelectionRange(caret, caret)
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
let offSpeech: (() => void) | null = null
onMounted(() => {
  window.addEventListener('flyknit:focus-input', focus)
  offSpeech = onSpeechText(insert)
  focus()
})
onBeforeUnmount(() => {
  window.removeEventListener('flyknit:focus-input', focus)
  offSpeech?.()
  // 组件卸载时还在录，就别留着麦克风开着
  if (state.speech.phase !== 'idle') cancelSpeech()
})
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

      <!-- 录音条：录音中盖住输入区，和打字互不干扰 -->
      <div v-if="speech.phase !== 'idle'" class="recorder" role="status" aria-live="polite">
        <span class="rec-ico" :class="{ live: speech.phase === 'recording' }">
          <Mic v-if="speech.phase === 'recording'" :size="15" />
          <Loader2 v-else :size="15" class="spin" />
        </span>
        <span class="wave" aria-hidden="true">
          <i v-for="(shape, i) in bars" :key="i" :style="{ height: barHeight(shape) }" />
        </span>
        <span class="elapsed" :class="{ warn: nearLimit }">{{ spoken }}</span>
        <span class="rec-hint">{{ t(speech.phase === 'recording' ? 'ui.speech.listening' : 'ui.speech.working') }}</span>
        <button type="button" class="rec-btn" :title="t('ui.speech.cancel')" @click="cancelSpeech">
          <X :size="15" />
        </button>
        <button
          type="button"
          class="rec-btn done"
          :disabled="speech.phase !== 'recording'"
          :title="t('ui.speech.finish')"
          @click="finishSpeech"
        >
          <Check :size="15" />
        </button>
      </div>

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
        <button
          v-if="micReady"
          type="button"
          class="tool-btn icon-only"
          :class="{ on: speech.phase !== 'idle' }"
          :disabled="offline || speech.phase === 'working'"
          :title="t(speech.phase === 'idle' ? 'ui.speech.start' : 'ui.speech.cancel')"
          @click="speech.phase === 'idle' ? startSpeech() : cancelSpeech()"
        >
          <Mic :size="17" />
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
/* 录音条：跟着输入框的圆角走，像是从卡片里长出来的 */
.recorder {
  display: flex;
  gap: 10px;
  align-items: center;
  margin: 10px 12px 2px;
  padding: 7px 8px 7px 14px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth-sunk);
}
.rec-ico {
  display: grid;
  flex: none;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  color: var(--ink-soft);
}
.rec-ico.live {
  background: color-mix(in srgb, var(--red) 14%, transparent);
  color: var(--red);
  animation: pulse 1.6s ease-in-out infinite;
}
@keyframes pulse {
  50% {
    background: color-mix(in srgb, var(--red) 30%, transparent);
  }
}
.spin {
  animation: spin 900ms linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
/* 说话时跳动的竖条。高度由实际响度算出来，不是固定动画 */
.wave {
  display: flex;
  flex: none;
  gap: 3px;
  align-items: center;
  height: 20px;
}
.wave i {
  width: 3px;
  min-height: 3px;
  border-radius: 999px;
  background: var(--indigo);
  transition: height 110ms ease-out;
}
.elapsed {
  flex: none;
  color: var(--ink);
  font-size: calc(13px * var(--font-scale));
  font-variant-numeric: tabular-nums;
}
.elapsed.warn {
  color: var(--red);
}
.rec-hint {
  flex: 1;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.rec-btn {
  display: grid;
  flex: none;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 1px solid var(--line);
  border-radius: 50%;
  background: var(--cloth);
  color: var(--ink-soft);
  cursor: pointer;
}
.rec-btn:hover:not(:disabled) {
  border-color: var(--ink-soft);
  color: var(--ink);
}
.rec-btn.done:hover:not(:disabled) {
  border-color: var(--indigo);
  color: var(--indigo);
}
.rec-btn:disabled {
  opacity: 0.45;
  cursor: default;
}
@media (prefers-reduced-motion: reduce) {
  .rec-ico.live,
  .spin {
    animation: none;
  }
  .wave i {
    transition: none;
  }
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
