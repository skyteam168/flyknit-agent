<script setup lang="ts">
/**
 * 聊天里 PR / Issue 链接（markdown.ts 标成 a.ref-link）的悬停卡片：状态、仓库和编号、时间、标题、作者、改动行数、文件数。
 * 内容由宿主用员工电脑上的 gh 查（开发者工具开着时）；查不到就不弹，链接照常能点。
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { CircleDot, GitMerge, GitPullRequest, GitPullRequestClosed, GitPullRequestDraft, CircleCheck } from '@lucide/vue'
import { bridge } from '../bridge'
import type { RefCard } from '../types'

const { t } = useI18n()

const card = ref<RefCard | null>(null)
const loading = ref(false)
const visible = ref(false)
const pos = ref({ left: 0, top: 0, above: false })

// 同一个地址一会儿内只问一次宿主（宿主那边也有缓存，这里省掉来回）
const cache = new Map<string, { at: number; p: Promise<RefCard | null> }>()
const FRESH = 120_000

let anchor: HTMLAnchorElement | null = null
let showTimer: ReturnType<typeof setTimeout> | undefined
let hideTimer: ReturnType<typeof setTimeout> | undefined
let seq = 0

function fetchCard(url: string) {
  const hit = cache.get(url)
  if (hit && Date.now() - hit.at < FRESH) return hit.p
  const p = bridge.refCard(url).catch(() => null)
  cache.set(url, { at: Date.now(), p })
  return p
}

function place(a: HTMLElement) {
  const r = a.getBoundingClientRect()
  const width = 340
  const left = Math.max(8, Math.min(r.left, window.innerWidth - width - 8))
  const above = r.bottom + 150 > window.innerHeight && r.top > 160
  pos.value = { left, top: above ? r.top - 6 : r.bottom + 6, above }
}

async function open(a: HTMLAnchorElement) {
  const id = ++seq
  anchor = a
  place(a)
  card.value = null
  loading.value = true
  visible.value = true
  const result = await fetchCard(a.href)
  if (id !== seq) return
  loading.value = false
  if (!result) {
    visible.value = false
    return
  }
  card.value = result
  // 查到状态以后给链接图标上色（同一个 PR 在页面上出现多次都上色）
  document.querySelectorAll<HTMLAnchorElement>('a.ref-link').forEach((el) => {
    if (el.href === a.href) el.dataset.state = result.state
  })
}

function close() {
  seq++
  visible.value = false
  anchor = null
}

function onOver(e: MouseEvent) {
  const a = (e.target as HTMLElement | null)?.closest?.('a.ref-link') as HTMLAnchorElement | null
  if (a) {
    clearTimeout(hideTimer)
    if (a === anchor && visible.value) return
    clearTimeout(showTimer)
    showTimer = setTimeout(() => void open(a), 350)
  }
}

function keep() {
  clearTimeout(hideTimer)
}

function onOut(e: MouseEvent) {
  const a = (e.target as HTMLElement | null)?.closest?.('a.ref-link')
  if (!a) return
  clearTimeout(showTimer)
  // 留一点时间让鼠标移到卡片上
  hideTimer = setTimeout(close, 200)
}

onMounted(() => {
  document.addEventListener('mouseover', onOver)
  document.addEventListener('mouseout', onOut)
  window.addEventListener('scroll', close, true)
})
onBeforeUnmount(() => {
  document.removeEventListener('mouseover', onOver)
  document.removeEventListener('mouseout', onOut)
  window.removeEventListener('scroll', close, true)
  clearTimeout(showTimer)
  clearTimeout(hideTimer)
})

const stateIcon = computed(() => {
  const c = card.value
  if (!c) return GitPullRequest
  if (c.kind === 'issue') return c.state === 'closed' ? CircleCheck : CircleDot
  return { open: GitPullRequest, draft: GitPullRequestDraft, merged: GitMerge, closed: GitPullRequestClosed }[c.state]
})

const stateText = computed(() => (card.value ? t(`ui.refCard.state.${card.value.state}`) : ''))

const ago = computed(() => {
  const at = card.value?.at
  if (!at) return ''
  const diff = Math.max(0, Date.now() - new Date(at).getTime())
  if (Number.isNaN(diff)) return ''
  const m = Math.floor(diff / 60000)
  if (m < 1) return t('ui.time.now')
  if (m < 60) return t('ui.time.minutes', { n: m })
  const h = Math.floor(m / 60)
  if (h < 24) return t('ui.time.hours', { n: h })
  return t('ui.time.days', { n: Math.floor(h / 24) })
})

const initial = computed(() => (card.value?.author ?? '?').charAt(0).toUpperCase() || '?')
</script>

<template>
  <Teleport to="body">
    <div
      v-if="visible"
      class="ref-card"
      :class="{ above: pos.above }"
      :style="{ left: pos.left + 'px', top: pos.top + 'px' }"
      role="tooltip"
      @mouseenter="keep"
      @mouseleave="close"
    >
      <div v-if="loading" class="loading">
        <span class="skel line" style="width: 60%" />
        <span class="skel line" style="width: 90%" />
        <span class="skel line" style="width: 40%" />
      </div>
      <template v-else-if="card">
        <div class="head">
          <span class="badge" :data-state="card.state">
            <component :is="stateIcon" :size="13" />
            {{ stateText }}
          </span>
          <span class="where" :title="card.repo">{{ card.repo }}</span>
          <span class="num">#{{ card.number }}</span>
          <span v-if="ago" class="ago">{{ ago }}</span>
        </div>
        <div class="title">{{ card.title }}</div>
        <div class="foot">
          <span v-if="card.author" class="author"><span class="avatar">{{ initial }}</span>{{ card.author }}</span>
          <span class="stats">
            <template v-if="card.kind === 'pr'">
              <span class="add">+{{ card.additions.toLocaleString() }}</span>
              <span class="del">−{{ card.deletions.toLocaleString() }}</span>
              <span class="chip">{{ t('ui.refCard.files', { n: card.files }) }}</span>
            </template>
            <span v-else class="chip">{{ t('ui.refCard.comments', { n: card.comments }) }}</span>
          </span>
        </div>
      </template>
    </div>
  </Teleport>
</template>

<style scoped>
.ref-card {
  position: fixed;
  z-index: 3000;
  width: 340px;
  max-width: calc(100vw - 16px);
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  color: var(--ink);
  font-size: var(--t-sm);
}
.ref-card.above {
  transform: translateY(-100%);
}
.loading {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.head {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.badge {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: var(--t-xs);
  font-weight: 600;
  color: #fff;
  background: var(--ink-faint);
}
.badge[data-state='open'] {
  background: var(--ref-open);
}
.badge[data-state='merged'] {
  background: var(--ref-merged);
}
.badge[data-state='closed'] {
  background: var(--ref-closed);
}
.where {
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.num {
  flex: none;
  margin-left: -4px;
  color: var(--ink-soft);
}
.ago {
  flex: none;
  margin-left: auto;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.title {
  margin: 8px 0 10px;
  font-weight: 600;
  line-height: 1.45;
  display: -webkit-box;
  -webkit-line-clamp: 3;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.author {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.avatar {
  display: inline-grid;
  flex: none;
  place-items: center;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: 10px;
  font-weight: 600;
}
.stats {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 6px;
  font-size: var(--t-xs);
}
.add {
  color: var(--ref-open);
  font-weight: 600;
}
.del {
  color: var(--ref-closed);
  font-weight: 600;
}
.chip {
  padding: 1px 7px;
  border-radius: 999px;
  background: var(--chip);
  color: var(--ink-soft);
}
</style>
