<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowUp, MessageCircleQuestion, Square, Trash2, X } from '@lucide/vue'
import { renderMarkdown } from '../markdown'
import { askSide, state, stopSide, toggleSideChat } from '../store'

const { t } = useI18n()
const text = ref('')
const box = ref<HTMLTextAreaElement>()
const list = ref<HTMLElement>()

const items = computed(() => (state.currentId ? state.sideChat.byConv[state.currentId] ?? [] : []))
const streaming = computed(() => items.value.some((x) => x.status === 'streaming'))

onMounted(() => box.value?.focus())

// 有新内容就滚到底
watch(
  () => items.value.map((x) => x.content.length).join(),
  async () => {
    await nextTick()
    list.value?.scrollTo({ top: list.value.scrollHeight })
  },
)

function submit() {
  if (!text.value.trim() || streaming.value) return
  void askSide(text.value)
  text.value = ''
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault()
    submit()
  } else if (e.key === 'Escape') {
    e.stopPropagation()
    toggleSideChat(false)
  }
}

function clear() {
  if (state.currentId) state.sideChat.byConv[state.currentId] = []
}
</script>

<template>
  <aside class="sidechat" role="complementary" :aria-label="t('ui.side.title')">
    <header>
      <MessageCircleQuestion :size="15" />
      <strong>{{ t('ui.side.title') }}</strong>
      <span class="hint">{{ t('ui.side.hint') }}</span>
      <button v-if="items.length && !streaming" type="button" class="icon" :title="t('ui.side.clear')" @click="clear"><Trash2 :size="14" /></button>
      <button type="button" class="icon" :title="t('ui.side.close')" @click="toggleSideChat(false)"><X :size="15" /></button>
    </header>
    <div ref="list" class="list">
      <p v-if="!items.length" class="empty">{{ t('ui.side.empty') }}</p>
      <template v-for="(m, i) in items" :key="i">
        <div v-if="m.role === 'user'" class="q">{{ m.content }}</div>
        <div v-else class="a" :class="m.status">
          <span v-if="m.status === 'streaming' && !m.content" class="dots"><i /><i /><i /></span>
          <div v-else class="md" v-html="renderMarkdown(m.content, { streaming: m.status === 'streaming' })" />
        </div>
      </template>
    </div>
    <div class="input">
      <textarea ref="box" v-model="text" rows="1" :placeholder="t('ui.side.placeholder')" @keydown="onKey" />
      <button v-if="streaming" type="button" class="send stop" :title="t('input.stop')" @click="stopSide"><Square :size="11" fill="currentColor" /></button>
      <button v-else type="button" class="send" :disabled="!text.trim()" :title="t('ui.side.send')" @click="submit"><ArrowUp :size="15" /></button>
    </div>
  </aside>
</template>

<style scoped>
.sidechat {
  display: flex;
  flex-direction: column;
  align-self: center;
  width: calc(100% - 32px);
  max-width: 840px;
  box-sizing: border-box;
  max-height: 42%;
  min-height: 160px;
  margin: 0 auto 8px;
  border: 1px solid var(--indigo);
  border-radius: var(--r-md);
  background: var(--cloth);
  box-shadow: var(--shadow-card);
  overflow: hidden;
}
header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 8px 7px 12px;
  border-bottom: 1px solid var(--line);
  background: var(--indigo-wash);
  color: var(--indigo);
  font-size: var(--t-sm);
}
.hint {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.icon {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-soft);
}
.icon:hover {
  background: var(--cloth);
}
.list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 10px 12px;
  font-size: var(--t-sm);
}
.empty {
  margin: 8px 0;
  color: var(--ink-faint);
}
.q {
  width: fit-content;
  max-width: 85%;
  margin: 0 0 8px auto;
  padding: 6px 10px;
  border-radius: 10px;
  background: var(--chip);
  white-space: pre-wrap;
}
.a {
  margin-bottom: 10px;
}
.a.error {
  color: var(--red);
}
.a .md :deep(p) {
  margin: 0 0 6px;
}
.input {
  display: flex;
  align-items: flex-end;
  gap: 6px;
  padding: 8px;
  border-top: 1px solid var(--line);
}
textarea {
  flex: 1;
  min-height: 30px;
  max-height: 90px;
  padding: 5px 8px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--loom);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
  resize: none;
}
.send {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 50%;
  background: var(--pill);
  color: var(--pill-ink);
}
.send:disabled {
  background: var(--line-strong);
  color: var(--cloth);
}
.dots {
  display: inline-flex;
  gap: 4px;
}
.dots i {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--ink-faint);
  animation: blink 1.2s infinite;
}
.dots i:nth-child(2) {
  animation-delay: 0.2s;
}
.dots i:nth-child(3) {
  animation-delay: 0.4s;
}
@keyframes blink {
  0%,
  80%,
  100% {
    opacity: 0.25;
  }
  40% {
    opacity: 1;
  }
}
</style>
