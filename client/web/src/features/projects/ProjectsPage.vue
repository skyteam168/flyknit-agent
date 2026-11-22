<script setup lang="ts">
/**
 * 左侧「项目」页（参照 Claude 的 Projects）：列表（最近打开的在前，可以搜索、按名字排）、项目详情（目标、说明、项目里的任务）、新建项目。
 * 项目 = 一个工作区文件夹 + 名字 + 目标 + 说明；目标和说明在项目里的任务第一次发消息时交给 AI。
 */
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDownUp, ArrowLeft, Code2, FolderKanban, FolderOpen, MessageSquare, Plus, Search, Trash2, TriangleAlert } from '@lucide/vue'
import { state } from '../../store'
import NewProjectDialog from './NewProjectDialog.vue'
import {
  loadProjects,
  newTaskInProject,
  openProjectInIde,
  openProjectTask,
  projects,
  removeProject,
  saveProject,
  selectedProject,
  selectProject,
  tasksOf,
} from './store'

const { t } = useI18n()
if (!projects.loaded) void loadProjects()

const query = ref('')
const searching = ref(false)
const byName = ref(false)
const list = computed(() => {
  const q = query.value.trim().toLowerCase()
  const rows = projects.list.filter((p) => !q || p.name.toLowerCase().includes(q) || p.goal.toLowerCase().includes(q) || p.path.toLowerCase().includes(q))
  return byName.value ? rows.slice().sort((a, b) => a.name.localeCompare(b.name)) : rows
})
const devTools = computed(() => !!state.app?.developerTools)

function when(at: string) {
  const d = new Date(at)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleDateString(undefined, { month: 'long', day: 'numeric' })
}
function whenTime(at: string) {
  const d = new Date(at)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

// ---------- 详情：目标、说明可以改 ----------
const p = selectedProject
const form = ref({ name: '', goal: '', notes: '' })
watch(
  p,
  (v) => {
    form.value = { name: v?.name ?? '', goal: v?.goal ?? '', notes: v?.notes ?? '' }
  },
  { immediate: true },
)
const dirty = computed(() => !!p.value && (form.value.name !== p.value.name || form.value.goal !== p.value.goal || form.value.notes !== p.value.notes))
async function save() {
  if (p.value) await saveProject(p.value.id, { ...form.value })
}
const tasks = computed(() => (p.value ? tasksOf(p.value) : []))
function remove() {
  if (p.value && window.confirm(t('ui.projects.removeConfirm', { name: p.value.name }))) void removeProject(p.value.id)
}
</script>

<template>
  <div class="page-root">
    <!-- 列表 -->
    <div v-if="!p" class="page">
      <header>
        <h2>{{ t('ui.projects.title') }}</h2>
        <span class="spacer" />
        <div v-if="searching" class="search">
          <Search :size="14" />
          <input v-model="query" :placeholder="t('ui.projects.search')" autofocus @keydown.esc="(searching = false), (query = '')" />
        </div>
        <button v-else type="button" class="icon" :title="t('ui.projects.search')" @click="searching = true"><Search :size="16" /></button>
        <button type="button" class="icon" :class="{ on: byName }" :title="byName ? t('ui.projects.sortName') : t('ui.projects.sortRecent')" @click="byName = !byName">
          <ArrowDownUp :size="16" />
        </button>
        <button type="button" class="btn primary" @click="projects.creating = true"><Plus :size="14" /> {{ t('ui.projects.new') }}</button>
      </header>
      <p class="sub">{{ t('ui.projects.subtitle') }}</p>

      <div v-if="list.length" class="grid">
        <button v-for="x in list" :key="x.id" type="button" class="card" @click="selectProject(x.id)">
          <span class="card-icon"><FolderKanban :size="18" /></span>
          <span class="card-body">
            <span class="name">{{ x.name }}</span>
            <span v-if="x.goal" class="goal">{{ x.goal }}</span>
            <span class="meta">
              <TriangleAlert v-if="!x.exists" :size="12" class="warn" />
              {{ x.exists ? when(x.openedAt) : t('ui.projects.missing') }} · {{ t('ui.projects.taskCount', { n: tasksOf(x).length }) }}
            </span>
          </span>
        </button>
      </div>
      <div v-else-if="projects.loaded" class="empty">
        <FolderKanban :size="36" />
        <p>{{ query ? t('ui.projects.noMatch') : t('ui.projects.empty') }}</p>
        <button v-if="!query" type="button" class="btn primary" @click="projects.creating = true"><Plus :size="14" /> {{ t('ui.projects.new') }}</button>
      </div>
    </div>

    <!-- 详情 -->
    <div v-else class="page detail">
      <button type="button" class="back" @click="selectProject(null)"><ArrowLeft :size="14" /> {{ t('ui.projects.title') }}</button>
      <div class="detail-head">
        <span class="card-icon big"><FolderKanban :size="22" /></span>
        <div class="title">
          <input v-model="form.name" class="name-input" maxlength="60" />
          <span class="path" :title="p.path"><FolderOpen :size="12" /> {{ p.path }}<template v-if="!p.exists"> · {{ t('ui.projects.missing') }}</template></span>
        </div>
      </div>
      <div class="actions">
        <button v-if="devTools" type="button" class="btn primary" :disabled="!p.exists" @click="openProjectInIde(p)"><Code2 :size="14" /> {{ t('ui.projects.openIde') }}</button>
        <button type="button" class="btn" :disabled="!p.exists" @click="newTaskInProject(p)"><Plus :size="14" /> {{ t('ui.projects.newTask') }}</button>
      </div>

      <div class="cols">
        <section class="tasks">
          <h3>{{ t('ui.projects.tasks') }}</h3>
          <button v-for="c in tasks" :key="c.id" type="button" class="task" @click="openProjectTask(c.id)">
            <MessageSquare :size="14" />
            <span class="task-title">{{ c.title || t('ui.tasks.untitled') }}</span>
            <span class="task-time">{{ whenTime(c.updatedAt) }}</span>
          </button>
          <p v-if="!tasks.length" class="none">{{ t('ui.projects.noTasks') }}</p>
        </section>
        <section class="about">
          <label>
            <span>{{ t('ui.projects.goal') }}</span>
            <textarea v-model="form.goal" rows="3" maxlength="2000" :placeholder="t('ui.projects.goalPlaceholder')" />
          </label>
          <label>
            <span>{{ t('ui.projects.notes') }}</span>
            <textarea v-model="form.notes" rows="6" maxlength="8000" :placeholder="t('ui.projects.notesPlaceholder')" />
          </label>
          <small>{{ t('ui.projects.aboutHint') }}</small>
          <div class="about-actions">
            <button type="button" class="link danger" @click="remove"><Trash2 :size="13" /> {{ t('ui.projects.remove') }}</button>
            <span class="spacer" />
            <button type="button" class="btn primary" :disabled="!dirty || !form.name.trim()" @click="save">{{ t('ui.projects.save') }}</button>
          </div>
        </section>
      </div>
    </div>

    <NewProjectDialog v-if="projects.creating" @close="projects.creating = false" />
  </div>
</template>

<style scoped>
.page-root {
  position: relative;
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
  overflow-y: auto;
  background: var(--loom);
}
.page {
  width: 100%;
  max-width: 980px;
  margin: 0 auto;
  padding: 22px 28px 28px;
}
header {
  display: flex;
  align-items: center;
  gap: 6px;
}
h2 {
  margin: 0;
  font-size: calc(22px * var(--font-scale));
  font-weight: 600;
}
.sub {
  margin: 4px 0 18px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.spacer {
  flex: 1;
}
.icon {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.icon:hover,
.icon.on {
  background: color-mix(in srgb, var(--ink) 7%, transparent);
  color: var(--ink);
}
.search {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 10px;
  height: 30px;
  border: 1px solid var(--line);
  border-radius: 7px;
  background: var(--cloth);
  color: var(--ink-faint);
}
.search input {
  width: 180px;
  border: 0;
  outline: none;
  background: transparent;
  color: var(--ink);
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 6px 12px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-sm);
  cursor: pointer;
}
.btn:hover:not(:disabled) {
  border-color: var(--indigo);
}
.btn:disabled {
  opacity: 0.5;
  cursor: default;
}
.btn.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 10px;
}
.card {
  display: flex;
  gap: 12px;
  padding: 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
  color: var(--ink);
  text-align: left;
  cursor: pointer;
}
.card:hover {
  border-color: color-mix(in srgb, var(--indigo) 60%, var(--line));
}
.card-icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 9px;
  background: var(--indigo-wash);
  color: var(--indigo);
}
.card-icon.big {
  width: 44px;
  height: 44px;
  border-radius: 12px;
}
.card-body {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}
.name {
  font-weight: 600;
  font-size: var(--t-md);
}
.goal {
  display: -webkit-box;
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}
.meta {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
}
.warn {
  color: var(--amber);
}
.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 60px 0;
  color: var(--ink-faint);
}
.back {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-bottom: 14px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--ink-faint);
  font-size: var(--t-sm);
  cursor: pointer;
}
.back:hover {
  color: var(--ink);
}
.detail-head {
  display: flex;
  align-items: center;
  gap: 12px;
}
.title {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}
.name-input {
  padding: 2px 4px;
  margin-left: -4px;
  border: 1px solid transparent;
  border-radius: 6px;
  background: transparent;
  color: var(--ink);
  font-size: calc(20px * var(--font-scale));
  font-weight: 600;
}
.name-input:hover,
.name-input:focus {
  border-color: var(--line);
  outline: none;
}
.path {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
  white-space: nowrap;
  text-overflow: ellipsis;
}
.actions {
  display: flex;
  gap: 8px;
  margin: 14px 0 18px;
}
.cols {
  display: grid;
  grid-template-columns: minmax(0, 1.4fr) minmax(260px, 1fr);
  gap: 16px;
}
@media (max-width: 820px) {
  .cols {
    grid-template-columns: 1fr;
  }
}
h3 {
  margin: 0 0 8px;
  font-size: var(--t-sm);
  font-weight: 600;
  color: var(--ink-soft);
}
.tasks,
.about {
  padding: 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
}
.task {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 6px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--ink);
  font-size: var(--t-sm);
  text-align: left;
  cursor: pointer;
}
.task:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.task svg {
  flex: none;
  color: var(--ink-faint);
}
.task-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.task-time {
  flex: none;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
}
.none {
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.about label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-bottom: 10px;
  font-size: var(--t-sm);
  color: var(--ink-soft);
}
.about textarea {
  padding: 8px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--loom);
  color: var(--ink);
  font: inherit;
  resize: vertical;
}
.about small {
  display: block;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
  line-height: 1.5;
}
.about-actions {
  display: flex;
  align-items: center;
  margin-top: 10px;
}
.link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--ink-faint);
  font-size: var(--t-sm);
  cursor: pointer;
}
.link.danger:hover {
  color: var(--red, #e5534b);
}
</style>
