<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { BookOpenCheck, Brain, FolderOpen, Heart, History, Info, Lightbulb, Plus, Sparkles, ThumbsDown, ThumbsUp, Trash2, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { setLearning, state, toast } from '../store'
import type { MemoryKind, MemoryOverview } from '../types'

const { t } = useI18n()
type Tab = 'preference' | 'fact' | 'experience' | 'episodes' | 'skills'
const tab = ref<Tab>('preference')
const data = ref<MemoryOverview | null>(null)
const draft = ref('')
const openEpisode = ref<string | null>(null)

const tabs: { key: Tab; icon: unknown }[] = [
  { key: 'preference', icon: Heart },
  { key: 'fact', icon: Info },
  { key: 'experience', icon: Lightbulb },
  { key: 'episodes', icon: History },
  { key: 'skills', icon: Sparkles },
]

async function load() {
  try {
    data.value = await bridge.memoryOverview()
  } catch (e) {
    toast(String(e))
  }
}
onMounted(load)

const items = computed(() => {
  const all = data.value?.items ?? []
  if (tab.value === 'experience') return all.filter((i) => i.kind === 'success' || i.kind === 'lesson')
  return all.filter((i) => i.kind === tab.value)
})

function count(k: Tab) {
  const d = data.value
  if (!d) return 0
  if (k === 'episodes') return d.episodes.length
  if (k === 'skills') return d.skills.length
  if (k === 'experience') return d.items.filter((i) => i.kind === 'success' || i.kind === 'lesson').length
  return d.items.filter((i) => i.kind === k).length
}

async function removeItem(id: string) {
  if (!data.value) return
  data.value.items = data.value.items.filter((i) => i.id !== id)
  await bridge.deleteMemory(id).catch(() => {})
}

async function removeEpisode(id: string) {
  if (!data.value) return
  data.value.episodes = data.value.episodes.filter((e) => e.id !== id)
  await bridge.deleteEpisode(id).catch(() => {})
}

async function removeSkill(name: string) {
  if (!data.value) return
  data.value.skills = data.value.skills.filter((s) => s.name !== name)
  await bridge.deleteLearnedSkill(name).catch(() => {})
}

async function add() {
  const text = draft.value.trim()
  if (!text || (tab.value !== 'preference' && tab.value !== 'fact')) return
  const ok = await bridge.addMemory(tab.value as MemoryKind, text).catch(() => false)
  draft.value = ''
  if (!ok) toast(t('ui.memory.duplicate'))
  await load()
}

async function toggleLearning() {
  const next = !(state.app?.learning ?? true)
  await setLearning(next)
  if (data.value) data.value.learning = next
}

const fmtDate = (iso: string) => new Date(iso).toLocaleDateString()
const close = () => (state.memoryOpen = false)
</script>

<template>
  <div class="scrim" @mousedown.self="close" @keydown.esc="close">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.memory.title')">
      <header>
        <div class="badge"><Brain :size="20" /></div>
        <div class="head-text">
          <h2>{{ t('ui.memory.title') }}</h2>
          <p>{{ t('ui.memory.subtitle') }}</p>
        </div>
        <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="close"><X :size="18" /></button>
      </header>

      <div class="learning">
        <div>
          <strong>{{ t('ui.memory.learning') }}</strong>
          <small>{{ t('ui.memory.learningHint') }}</small>
        </div>
        <button
          type="button"
          class="switch"
          role="switch"
          :aria-checked="state.app?.learning ?? true"
          :class="{ on: state.app?.learning ?? true }"
          @click="toggleLearning"
        >
          <i />
        </button>
      </div>

      <nav class="tabs" role="tablist">
        <button
          v-for="x in tabs"
          :key="x.key"
          type="button"
          role="tab"
          :aria-selected="tab === x.key"
          :class="{ on: tab === x.key }"
          @click="tab = x.key"
        >
          <component :is="x.icon" :size="15" />
          <span>{{ t(`ui.memory.tabs.${x.key}`) }}</span>
          <em>{{ count(x.key) }}</em>
        </button>
      </nav>

      <p class="tab-hint">{{ t(`ui.memory.hints.${tab}`) }}</p>

      <div class="body">
        <!-- 偏好、信息、经验教训 -->
        <template v-if="tab === 'preference' || tab === 'fact' || tab === 'experience'">
          <form v-if="tab !== 'experience'" class="add" @submit.prevent="add">
            <input v-model="draft" :placeholder="t(`ui.memory.addPlaceholder.${tab}`)" maxlength="300" />
            <button type="submit" class="btn primary" :disabled="!draft.trim()"><Plus :size="15" /> {{ t('ui.memory.add') }}</button>
          </form>
          <p v-if="items.length === 0" class="empty">{{ t('ui.memory.empty') }}</p>
          <ul v-else class="list">
            <li v-for="i in items" :key="i.id">
              <span v-if="tab === 'experience'" class="kind" :class="i.kind">{{ t(`ui.memory.kinds.${i.kind}`) }}</span>
              <span class="text">{{ i.text }}</span>
              <span v-if="i.date" class="date">{{ i.date }}</span>
              <button type="button" class="mini" :title="t('ui.memory.delete')" @click="removeItem(i.id)"><Trash2 :size="14" /></button>
            </li>
          </ul>
        </template>

        <!-- 历史任务 -->
        <template v-else-if="tab === 'episodes'">
          <p v-if="!data?.episodes.length" class="empty">{{ t('ui.memory.empty') }}</p>
          <ul v-else class="list episodes">
            <li v-for="e in data.episodes" :key="e.id" :class="{ open: openEpisode === e.id }">
              <button type="button" class="ep-head" @click="openEpisode = openEpisode === e.id ? null : e.id">
                <span class="outcome" :class="e.outcome">{{ t(`ui.memory.outcome.${e.outcome}`) }}</span>
                <span class="text">{{ e.title }}</span>
                <ThumbsUp v-if="e.feedback > 0" :size="13" class="fb up" />
                <ThumbsDown v-if="e.feedback < 0" :size="13" class="fb down" />
                <span v-if="e.uses" class="uses">{{ t('ui.memory.uses', { n: e.uses }) }}</span>
                <span class="date">{{ fmtDate(e.createdAt) }}</span>
              </button>
              <div v-if="openEpisode === e.id" class="ep-body">
                <p v-if="e.task"><b>{{ t('ui.memory.task') }}</b>{{ e.task }}</p>
                <p v-if="e.summary"><b>{{ t('ui.memory.summary') }}</b>{{ e.summary }}</p>
                <div v-if="e.procedure"><b>{{ t('ui.memory.procedure') }}</b><pre>{{ e.procedure }}</pre></div>
                <p v-for="l in e.lessons" :key="l"><b>{{ t('ui.memory.kinds.lesson') }}</b>{{ l }}</p>
                <button type="button" class="btn" @click="removeEpisode(e.id)"><Trash2 :size="14" /> {{ t('ui.memory.delete') }}</button>
              </div>
            </li>
          </ul>
        </template>

        <!-- 学到的技能 -->
        <template v-else>
          <p v-if="!data?.skills.length" class="empty">{{ t('ui.memory.noSkills') }}</p>
          <ul v-else class="list">
            <li v-for="k in data.skills" :key="k.name">
              <BookOpenCheck :size="16" class="skill-ico" />
              <span class="text"><b>{{ k.name }}</b><small>{{ k.description }}</small></span>
              <button type="button" class="mini" :title="t('ui.workspace.open')" @click="bridge.openPath(k.path)"><FolderOpen :size="14" /></button>
              <button type="button" class="mini" :title="t('ui.memory.delete')" @click="removeSkill(k.name)"><Trash2 :size="14" /></button>
            </li>
          </ul>
        </template>
      </div>

      <footer>
        <button type="button" class="btn" @click="bridge.openMemoryFolder()"><FolderOpen :size="15" /> {{ t('settings.openMemory') }}</button>
        <span class="spacer" />
        <button type="button" class="btn primary" @click="close">{{ t('settings.close') }}</button>
      </footer>
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
.dialog {
  display: flex;
  flex-direction: column;
  width: min(720px, 100%);
  height: min(640px, calc(100vh - 32px));
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
.learning {
  display: flex;
  align-items: center;
  gap: 12px;
  margin: 16px 0 12px;
  padding: 10px 14px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
}
.learning div {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.learning strong {
  font-size: var(--t-sm);
  font-weight: 600;
}
.learning small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.switch {
  position: relative;
  flex: none;
  width: 40px;
  height: 22px;
  border-radius: 11px;
  background: var(--line-strong);
  transition: background 140ms;
}
.switch i {
  position: absolute;
  top: 3px;
  left: 3px;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.2);
  transition: transform 140ms;
}
.switch.on {
  background: var(--indigo);
}
.switch.on i {
  transform: translateX(18px);
}
.tabs {
  display: flex;
  gap: 2px;
  border-bottom: 1px solid var(--line);
  overflow-x: auto;
}
.tabs button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 38px;
  padding: 0 12px;
  border-bottom: 2px solid transparent;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  white-space: nowrap;
}
.tabs button.on {
  border-bottom-color: var(--indigo);
  color: var(--ink);
  font-weight: 500;
}
.tabs em {
  padding: 0 6px;
  border-radius: 8px;
  background: var(--chip);
  color: var(--ink-faint);
  font-style: normal;
  font-size: calc(11px * var(--font-scale));
}
.tab-hint {
  margin: 10px 2px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}
.add {
  display: flex;
  gap: 8px;
  margin-bottom: 10px;
}
.add input {
  flex: 1;
  min-width: 0;
  height: 36px;
  padding: 0 12px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--cloth);
  outline: 0;
}
.add input:focus {
  border-color: var(--indigo);
}
.empty {
  margin: 28px 0;
  text-align: center;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.list {
  margin: 0;
  padding: 0;
  list-style: none;
}
.list > li {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 8px;
  border-bottom: 1px solid var(--line);
}
.list > li:hover {
  background: var(--cloth-sunk);
}
.text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: var(--t-sm);
  line-height: 1.5;
}
.text small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.date,
.uses {
  flex: none;
  font-size: calc(11px * var(--font-scale));
  color: var(--ink-faint);
  font-variant-numeric: tabular-nums;
}
.kind,
.outcome {
  flex: none;
  padding: 1px 7px;
  border-radius: 9px;
  font-size: calc(11px * var(--font-scale));
}
.kind.success,
.outcome.success {
  background: var(--thread-wash);
  color: var(--thread);
}
.kind.lesson,
.outcome.failure {
  background: var(--red-wash);
  color: var(--red);
}
.outcome.partial {
  background: var(--amber-wash);
  color: var(--amber);
}
.fb.up {
  color: var(--indigo);
}
.fb.down {
  color: var(--red);
}
.mini {
  display: grid;
  place-items: center;
  flex: none;
  width: 26px;
  height: 26px;
  border-radius: 6px;
  color: var(--ink-faint);
  opacity: 0;
}
li:hover .mini,
.mini:focus-visible {
  opacity: 1;
}
.mini:hover {
  background: var(--line);
  color: var(--red);
}
.episodes > li {
  display: block;
  padding: 0;
}
.ep-head {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 10px 8px;
  text-align: left;
}
.ep-body {
  padding: 2px 12px 14px 12px;
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.ep-body p {
  margin: 0 0 6px;
}
.ep-body b {
  margin-right: 6px;
  color: var(--ink);
  font-weight: 500;
}
.ep-body pre {
  margin: 4px 0 10px;
  padding: 8px 10px;
  border-radius: var(--r-sm);
  background: var(--cloth-sunk);
  font-family: var(--font);
  font-size: var(--t-xs);
  white-space: pre-wrap;
}
.skill-ico {
  flex: none;
  color: var(--indigo);
}
footer {
  display: flex;
  align-items: center;
  gap: 8px;
  padding-top: 14px;
  border-top: 1px solid var(--line);
}
.spacer {
  flex: 1;
}
@keyframes pop {
  from {
    opacity: 0;
    transform: translateY(6px) scale(0.98);
  }
}
</style>
