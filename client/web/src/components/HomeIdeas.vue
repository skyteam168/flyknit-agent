<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  CalendarClock,
  ClipboardCheck,
  FileSearch,
  FolderInput,
  GraduationCap,
  Mail,
  NotebookPen,
  PieChart,
  Presentation,
  Receipt,
  Shuffle,
  Sparkles,
  Target,
  TriangleAlert,
} from '@lucide/vue'
import type { Component } from 'vue'

/**
 * 首页「试试这样说」：几张示例任务卡。
 * - 鼠标移上去（或键盘移到上面）：整段任务先灰着显示在输入框里，看清楚再决定；移开就恢复；
 * - 点一下：真的填进输入框，可以改了再发；
 * - 「换一批」：一次显示 4 张，轮着换。
 * 示例只放办公室里常见的事，按当前模式（办事 / 对话）给不同的一组。文案三语，在 i18n 的 ui.ideas 里。
 */
const props = defineProps<{ mode: 'agent' | 'chat' }>()
const emit = defineEmits<{ preview: [text: string | null]; pick: [text: string] }>()
const { t } = useI18n()

interface Idea {
  key: string
  icon: Component
  tone: 'indigo' | 'thread' | 'amber' | 'red'
}

const IDEAS: Record<'agent' | 'chat', Idea[]> = {
  agent: [
    { key: 'attendance', icon: CalendarClock, tone: 'indigo' },
    { key: 'quotes', icon: FileSearch, tone: 'thread' },
    { key: 'downloads', icon: FolderInput, tone: 'amber' },
    { key: 'defects', icon: PieChart, tone: 'red' },
    { key: 'minutes', icon: NotebookPen, tone: 'indigo' },
    { key: 'invoices', icon: Receipt, tone: 'thread' },
    { key: 'sop', icon: Presentation, tone: 'amber' },
    { key: 'inspection', icon: ClipboardCheck, tone: 'red' },
  ],
  chat: [
    { key: 'supplier', icon: Mail, tone: 'indigo' },
    { key: 'oee', icon: Target, tone: 'thread' },
    { key: 'eightd', icon: TriangleAlert, tone: 'red' },
    { key: 'quiz', icon: GraduationCap, tone: 'amber' },
    { key: 'weekly', icon: NotebookPen, tone: 'indigo' },
    { key: 'ontime', icon: Sparkles, tone: 'thread' },
  ],
}

const PER_PAGE = 4
const page = ref(0)
watch(
  () => props.mode,
  () => (page.value = 0),
)
const all = computed(() => IDEAS[props.mode])
const pages = computed(() => Math.ceil(all.value.length / PER_PAGE))
const shown = computed(() => {
  const start = (page.value % pages.value) * PER_PAGE
  return all.value.slice(start, start + PER_PAGE)
})

const title = (i: Idea) => t(`ui.ideas.${props.mode}.${i.key}.title`)
const prompt = (i: Idea) => t(`ui.ideas.${props.mode}.${i.key}.prompt`)

function next() {
  emit('preview', null)
  page.value = (page.value + 1) % pages.value
}
</script>

<template>
  <section class="ideas" :aria-label="t('ui.ideas.heading')">
    <header>
      <span class="heading"><Sparkles :size="14" /> {{ t('ui.ideas.heading') }}</span>
      <button v-if="pages > 1" type="button" class="more" @click="next"><Shuffle :size="13" /> {{ t('ui.ideas.shuffle') }}</button>
    </header>
    <div class="grid">
      <button
        v-for="i in shown"
        :key="i.key"
        type="button"
        class="idea"
        :class="i.tone"
        :title="t('ui.ideas.useHint')"
        @mouseenter="emit('preview', prompt(i))"
        @mouseleave="emit('preview', null)"
        @focus="emit('preview', prompt(i))"
        @blur="emit('preview', null)"
        @click="emit('pick', prompt(i))"
      >
        <span class="ico"><component :is="i.icon" :size="17" /></span>
        <span class="text">
          <strong>{{ title(i) }}</strong>
          <small>{{ prompt(i) }}</small>
        </span>
        <span class="use">{{ t('ui.ideas.use') }}</span>
      </button>
    </div>
  </section>
</template>

<style scoped>
.ideas {
  width: 100%;
  margin-top: 22px;
}
header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 0 4px 8px;
}
.heading {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 600;
}
.more {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 10px;
  border-radius: 12px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.more:hover {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
}
.idea {
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: 12px;
  min-width: 0;
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: 14px;
  background: var(--cloth);
  text-align: left;
  transition:
    border-color 120ms ease,
    transform 120ms ease,
    box-shadow 120ms ease;
}
.idea:hover,
.idea:focus-visible {
  border-color: var(--line-strong);
  box-shadow: var(--shadow-card);
  transform: translateY(-1px);
  outline: none;
}
.ico {
  display: grid;
  flex: none;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 10px;
}
.indigo .ico {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.thread .ico {
  background: var(--thread-wash);
  color: var(--thread);
}
.amber .ico {
  background: var(--amber-wash);
  color: var(--amber);
}
.red .ico {
  background: var(--red-wash);
  color: var(--red);
}
.text {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}
.text strong {
  font-size: var(--t-sm);
  font-weight: 600;
}
.text small {
  display: -webkit-box;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  line-height: 1.45;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}
.use {
  position: absolute;
  top: 10px;
  right: 12px;
  padding: 1px 8px;
  border-radius: 9px;
  background: var(--indigo);
  color: #fff;
  font-size: 11px;
  opacity: 0;
  transition: opacity 120ms ease;
}
.idea:hover .use,
.idea:focus-visible .use {
  opacity: 1;
}
@media (max-width: 640px) {
  .grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
