<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Brain, Check, ChevronRight, Copy, FileText, ImageIcon, OctagonAlert } from '@lucide/vue'
import { bridge } from '../bridge'
import { renderMarkdown } from '../markdown'
import { current, currentState, state } from '../store'
import type { UiMessage } from '../types'
import ToolCard from './ToolCard.vue'

const { t } = useI18n()
const scroller = ref<HTMLElement>()
const copiedId = ref<string | null>(null)
const stick = ref(true)

const s = computed(() => currentState.value)
const isTranslate = computed(() => current.value?.mode === 'translate')

/** 隐藏 tool 消息（结果显示在工具卡片里）和空的 assistant 消息 */
const visible = computed(() =>
  (s.value?.messages ?? []).filter(
    (m) => m.role !== 'tool' && !(m.role === 'assistant' && !m.content.trim() && !(m.toolCalls?.length ?? 0)),
  ),
)

/** 尚未挂到任何消息上的工具（宿主先发 tool.started 时的兜底显示） */
const orphanTools = computed(() => {
  const st = s.value
  if (!st) return []
  const referenced = new Set(st.messages.flatMap((m) => (m.toolCalls ?? []).map((c) => c.id)))
  return Object.values(st.tools).filter((t) => !referenced.has(t.callId))
})

const waiting = computed(() => Object.values(s.value?.tools ?? {}).some((t) => t.state === 'waiting'))

function isContinuation(i: number) {
  return i > 0 && visible.value[i - 1].role === 'assistant' && visible.value[i].role === 'assistant'
}

function onScroll() {
  const el = scroller.value
  if (!el) return
  stick.value = el.scrollHeight - el.scrollTop - el.clientHeight < 80
}

watch(
  () => [visible.value.length, s.value?.draft?.content.length, s.value?.draft?.reasoning.length, state.currentId],
  async (_n, o) => {
    const switched = o && o[3] !== state.currentId
    if (!stick.value && !switched) return
    await nextTick()
    scroller.value?.scrollTo({ top: scroller.value.scrollHeight })
  },
)

async function copy(m: UiMessage | { id: string; content: string }) {
  await navigator.clipboard.writeText(m.content)
  copiedId.value = m.id
  setTimeout(() => (copiedId.value = null), 1500)
}

/** 代码块复制按钮（markdown 渲染出的 HTML 中） */
async function onContentClick(e: MouseEvent) {
  const btn = (e.target as HTMLElement).closest('[data-copy]') as HTMLElement | null
  if (!btn) return
  const code = btn.closest('pre')?.querySelector('code')?.textContent ?? ''
  await navigator.clipboard.writeText(code)
  btn.textContent = t('message.copied')
  setTimeout(() => (btn.textContent = t('message.copy')), 1500)
}

const isImage = (mime: string) => mime.startsWith('image/')
</script>

<template>
  <div ref="scroller" class="scroller" @scroll="onScroll">
    <div v-if="s" class="column" @click="onContentClick">
      <template v-for="(m, i) in visible" :key="m.id">
        <!-- 用户消息 -->
        <div v-if="m.role === 'user'" class="user">
          <div v-if="m.attachments?.length" class="files">
            <button v-for="a in m.attachments" :key="a.localPath" type="button" class="file" :title="a.localPath" @click="bridge.openPath(a.localPath)">
              <img v-if="a.preview" :src="a.preview" alt="" />
              <component :is="isImage(a.mime) ? ImageIcon : FileText" v-else :size="16" />
              <span>{{ a.fileName }}</span>
            </button>
          </div>
          <div v-if="m.content" class="bubble">{{ m.content }}</div>
        </div>

        <!-- 助手消息 -->
        <div v-else class="assistant" :class="{ cont: isContinuation(i), translation: isTranslate }">
          <details v-if="m.reasoning" class="reasoning">
            <summary><Brain :size="14" /> {{ t('message.thinking') }} <ChevronRight :size="14" class="chev" /></summary>
            <p>{{ m.reasoning }}</p>
          </details>
          <div v-if="m.content.trim()" class="md" v-html="renderMarkdown(m.content)" />
          <ToolCard
            v-for="c in m.toolCalls ?? []"
            v-show="s.tools[c.id]"
            :key="c.id"
            :tool="s.tools[c.id] ?? { callId: c.id, name: c.name, summary: '', risk: 'auto', state: 'done' }"
            :conversation-id="current!.id"
          />
          <div v-if="m.content.trim() && !(m.toolCalls?.length)" class="actions">
            <button type="button" class="icon-btn" :title="t('message.copy')" @click.stop="copy(m)">
              <component :is="copiedId === m.id ? Check : Copy" :size="15" />
            </button>
          </div>
        </div>
      </template>

      <!-- 流式输出中的回答 -->
      <div v-if="s.busy" class="assistant live" :class="{ cont: visible.length > 0 && visible[visible.length - 1].role === 'assistant' }">
        <details v-if="s.draft?.reasoning" class="reasoning" open>
          <summary><Brain :size="14" /> {{ t('message.thinkingLive') }} <ChevronRight :size="14" class="chev" /></summary>
          <p>{{ s.draft.reasoning }}</p>
        </details>
        <div v-if="s.draft?.content" class="md" v-html="renderMarkdown(s.draft.content)" />
        <div v-else-if="!s.draft?.reasoning && orphanTools.length === 0 && !waiting" class="dots" aria-label="…"><i /><i /><i /></div>
        <ToolCard v-for="tool in orphanTools" :key="tool.callId" :tool="tool" :conversation-id="current!.id" />
      </div>

      <div v-if="s.notice" class="notice" :class="s.notice.kind">
        <OctagonAlert v-if="s.notice.kind === 'error'" :size="16" />
        <span>{{ s.notice.kind === 'error' ? `${t('message.error')}: ${s.notice.text}` : t(`message.${s.notice.kind}`) }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.scroller {
  flex: 1;
  overflow-y: auto;
  scroll-behavior: auto;
}
.column {
  max-width: var(--column);
  margin: 0 auto;
  padding: 28px 32px 32px;
}
.user {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 6px;
  margin: 22px 0 18px;
}
.user:first-child {
  margin-top: 0;
}
.bubble {
  max-width: 82%;
  padding: 10px 14px;
  border-radius: 14px 14px 4px 14px;
  background: var(--indigo-wash);
  white-space: pre-wrap;
  word-break: break-word;
}
.files {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 6px;
  max-width: 82%;
}
.file {
  display: flex;
  align-items: center;
  gap: 8px;
  max-width: 260px;
  height: 40px;
  padding: 0 12px 0 6px;
  border-radius: var(--r-md);
  border: 1px solid var(--line);
  background: var(--cloth);
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.file svg {
  margin-left: 6px;
}
.file img {
  width: 30px;
  height: 30px;
  object-fit: cover;
  border-radius: 6px;
}
.file span {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.assistant {
  margin: 0 0 6px;
}
.assistant.cont {
  margin-top: -2px;
}
.translation .md {
  padding: 14px 16px;
  border-radius: var(--r-md);
  border: 1px solid var(--line);
  background: var(--cloth);
  font-size: var(--t-lg);
  line-height: 1.65;
}
.reasoning {
  margin: 0 0 8px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.reasoning summary {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
  list-style: none;
}
.reasoning summary::-webkit-details-marker {
  display: none;
}
.reasoning .chev {
  transition: transform 150ms;
}
.reasoning[open] .chev {
  transform: rotate(90deg);
}
.reasoning p {
  margin: 6px 0 0;
  padding-left: 12px;
  border-left: 2px solid var(--line);
  white-space: pre-wrap;
}
.actions {
  display: flex;
  gap: 2px;
  margin: 2px 0 12px -6px;
  opacity: 0;
}
.assistant:hover .actions,
.actions:focus-within {
  opacity: 1;
}
.dots {
  display: flex;
  gap: 5px;
  padding: 10px 0;
}
.dots i {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--indigo);
  animation: bounce 1s infinite ease-in-out;
}
.dots i:nth-child(2) {
  animation-delay: 0.15s;
  background: var(--thread);
}
.dots i:nth-child(3) {
  animation-delay: 0.3s;
}
.notice {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 14px 0;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
.notice.error {
  padding: 10px 14px;
  border-radius: var(--r-md);
  background: var(--red-wash);
  color: var(--red);
}
@keyframes bounce {
  0%,
  80%,
  100% {
    transform: translateY(0);
    opacity: 0.5;
  }
  40% {
    transform: translateY(-5px);
    opacity: 1;
  }
}
@media (max-width: 720px) {
  .column {
    padding: 20px 16px 24px;
  }
  .bubble {
    max-width: 92%;
  }
}

/* ---------- Markdown 内容 ---------- */
.md :deep(p) {
  margin: 0 0 10px;
}
.md :deep(p:last-child) {
  margin-bottom: 0;
}
.md :deep(h1),
.md :deep(h2),
.md :deep(h3) {
  margin: 18px 0 8px;
  font-size: var(--t-lg);
  font-weight: 600;
  line-height: 1.35;
}
.md :deep(ul),
.md :deep(ol) {
  margin: 0 0 10px;
  padding-left: 22px;
}
.md :deep(li) {
  margin: 3px 0;
}
.md :deep(a) {
  color: var(--indigo);
}
.md :deep(strong) {
  font-weight: 600;
}
.md :deep(code) {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--cloth-sunk);
  font-family: var(--font-code);
  font-size: 0.88em;
}
.md :deep(pre.code) {
  margin: 10px 0;
  border-radius: var(--r-md);
  border: 1px solid var(--line);
  background: var(--cloth-sunk);
  overflow: hidden;
}
.md :deep(.code-head) {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 4px 8px 4px 12px;
  border-bottom: 1px solid var(--line);
  font-size: var(--t-xs);
  color: var(--ink-faint);
  font-family: var(--font);
}
.md :deep(.code-copy) {
  padding: 2px 8px;
  border-radius: 4px;
  font-size: var(--t-xs);
  color: var(--ink-soft);
}
.md :deep(.code-copy:hover) {
  background: var(--cloth);
}
.md :deep(pre.code code) {
  display: block;
  padding: 12px 14px;
  overflow-x: auto;
  background: none;
  font-size: 12.5px;
  line-height: 1.6;
}
.md :deep(table) {
  border-collapse: collapse;
  margin: 10px 0;
  font-size: var(--t-sm);
  display: block;
  overflow-x: auto;
}
.md :deep(th),
.md :deep(td) {
  padding: 6px 12px;
  border: 1px solid var(--line);
  text-align: left;
}
.md :deep(th) {
  background: var(--cloth-sunk);
  font-weight: 600;
}
.md :deep(blockquote) {
  margin: 10px 0;
  padding-left: 12px;
  border-left: 3px solid var(--line-strong);
  color: var(--ink-soft);
}
.md :deep(.hljs-keyword),
.md :deep(.hljs-built_in) {
  color: var(--indigo);
}
.md :deep(.hljs-string) {
  color: var(--thread);
}
.md :deep(.hljs-comment) {
  color: var(--ink-faint);
  font-style: italic;
}
.md :deep(.hljs-number),
.md :deep(.hljs-variable) {
  color: var(--amber);
}
</style>
