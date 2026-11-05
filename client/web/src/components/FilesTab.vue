<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, File, FileText, Folder, FolderOpen, FolderTree, Paperclip, PenLine, RefreshCw, Search, SquareArrowOutUpRight, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { current, fail, launchFile, openDocument, openPath, openPreview, state, toast } from '../store'
import type { OutputFile, WorkspaceEntry } from '../types'

/**
 * 右侧「文件」标签：浏览、搜索这个对话的工作区（只读）。
 * 点文件在右侧预览（预览不了的用默认程序打开）；悬停时可以加进输入框、Markdown 转到文档画布里改、在文件夹中显示。
 * 列表和路径都由宿主按工作区校验，跳不出工作区。
 */
const { t, locale } = useI18n()

const conv = computed(() => current.value?.id ?? '')
const rootName = computed(() => (current.value?.workspace ?? '').split(/[\\/]/).filter(Boolean).pop() ?? '')

// 搜索框（下面的 watch 一换对话就清空它，要先声明）
const query = ref('')
const results = ref<WorkspaceEntry[] | null>(null)
const searching = ref(false)
const searchTruncated = ref(false)

/** 每个展开过的文件夹（按相对路径）：子项、是否展开、是否在读 */
const dirs = ref<Record<string, { entries: WorkspaceEntry[]; open: boolean; loading: boolean; truncated: boolean }>>({})
const error = ref('')

async function load(path: string) {
  const id = conv.value
  if (!id) return
  dirs.value[path] = { entries: dirs.value[path]?.entries ?? [], open: true, loading: true, truncated: false }
  try {
    const r = await bridge.listWorkspace(id, path)
    if (id !== conv.value) return
    if (!r.exists && path === '') error.value = t('ui.files.noWorkspace')
    else error.value = r.error
    dirs.value[path] = { entries: r.entries, open: true, loading: false, truncated: r.truncated }
  } catch (e) {
    dirs.value[path] = { entries: [], open: true, loading: false, truncated: false }
    error.value = String(e)
  }
}

function toggle(e: WorkspaceEntry) {
  const d = dirs.value[e.path]
  if (!d) void load(e.path)
  else d.open = !d.open
}

/** 刷新：根目录和展开着的文件夹重新读 */
function refresh() {
  const open = Object.entries(dirs.value).filter(([, d]) => d.open).map(([p]) => p)
  dirs.value = {}
  for (const p of open.length ? open : ['']) void load(p)
}

watch(
  () => [conv.value, current.value?.workspace],
  () => {
    dirs.value = {}
    query.value = ''
    error.value = ''
    void load('')
  },
  { immediate: true },
)

/** 树拍平成一行一行（展开的文件夹把子项接在下面） */
const rows = computed(() => {
  const out: { entry: WorkspaceEntry; depth: number }[] = []
  const walk = (path: string, depth: number) => {
    for (const entry of dirs.value[path]?.entries ?? []) {
      out.push({ entry, depth })
      if (entry.isDir && dirs.value[entry.path]?.open) walk(entry.path, depth + 1)
    }
  }
  walk('', 0)
  return out
})

// ---------- 搜索 ----------
let searchTimer: ReturnType<typeof setTimeout> | undefined
watch(query, (q) => {
  clearTimeout(searchTimer)
  if (!q.trim()) {
    results.value = null
    return
  }
  searchTimer = setTimeout(async () => {
    searching.value = true
    try {
      const r = await bridge.searchWorkspace(conv.value, q)
      if (q === query.value) {
        results.value = r.entries
        searchTruncated.value = r.truncated
      }
    } catch (e) {
      fail(e)
    } finally {
      searching.value = false
    }
  }, 300)
})

// ---------- 文件操作 ----------
function asOutput(e: WorkspaceEntry): OutputFile {
  const dot = e.name.lastIndexOf('.')
  return {
    path: e.fullPath,
    name: e.name,
    extension: dot > 0 ? e.name.slice(dot).toLowerCase() : '',
    size: e.size,
    modifiedAt: e.modifiedAt,
    previewable: e.previewable,
    exists: true,
  }
}

function openEntry(e: WorkspaceEntry) {
  if (e.isDir) toggle(e)
  else if (e.previewable) void openPreview(asOutput(e))
  else void launchFile(asOutput(e))
}

async function attach(e: WorkspaceEntry) {
  try {
    const a = await bridge.attachWorkspaceFile(conv.value, e.path)
    if (!a) {
      toast(t('ui.files.gone'))
      return
    }
    if (!state.pending.some((p) => p.localPath === a.localPath)) state.pending.push(a)
    toast(t('ui.files.attached', { name: e.name }))
    window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
  } catch (err) {
    fail(err)
  }
}

const isMd = (e: WorkspaceEntry) => !e.isDir && /\.(md|markdown)$/i.test(e.name)

function size(n: number) {
  if (n >= 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`
  if (n >= 1024) return `${Math.round(n / 1024)} KB`
  return `${n} B`
}
function when(at: string) {
  const d = new Date(at)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleString(locale.value, { year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}
function tip(e: WorkspaceEntry) {
  return e.isDir ? e.path : `${e.path}\n${size(e.size)} · ${when(e.modifiedAt)}`
}
</script>

<template>
  <aside class="files" :aria-label="t('ui.files.title')">
    <header class="head">
      <FolderTree :size="15" />
      <strong :title="current?.workspace ?? ''">{{ rootName || t('ui.files.title') }}</strong>
      <span class="spacer" />
      <button type="button" class="icon" :title="t('ui.files.refresh')" @click="refresh"><RefreshCw :size="14" /></button>
      <button type="button" class="icon" :title="t('ui.files.reveal')" @click="current?.workspace && openPath(current.workspace)"><FolderOpen :size="14" /></button>
    </header>
    <label class="search">
      <Search :size="14" />
      <input v-model="query" type="search" :placeholder="t('ui.files.search')" :aria-label="t('ui.files.search')" />
      <button v-if="query" type="button" class="icon" :title="t('ui.files.clear')" @click="query = ''"><X :size="13" /></button>
    </label>

    <div class="list" role="tree">
      <p v-if="error" class="empty">{{ error }}</p>

      <!-- 搜索结果：平铺，显示相对路径 -->
      <template v-else-if="results !== null">
        <p v-if="searching && !results.length" class="empty">{{ t('ui.files.searching') }}</p>
        <p v-else-if="!results.length" class="empty">{{ t('ui.files.noResults') }}</p>
        <div v-for="e in results" :key="e.path" class="row" role="treeitem" :title="tip(e)">
          <button type="button" class="main" @click="openEntry(e)">
            <Folder v-if="e.isDir" :size="14" class="ic dir" />
            <FileText v-else-if="e.previewable" :size="14" class="ic" />
            <File v-else :size="14" class="ic" />
            <span class="name">{{ e.name }}</span>
            <span class="path">{{ e.path }}</span>
          </button>
          <span class="acts">
            <button v-if="isMd(e)" type="button" class="icon" :title="t('ui.doc.openInCanvas')" @click="openDocument(conv, e.fullPath)"><PenLine :size="13" /></button>
            <button v-if="!e.isDir" type="button" class="icon" :title="t('ui.files.attach')" @click="attach(e)"><Paperclip :size="13" /></button>
            <button type="button" class="icon" :title="t('ui.files.reveal')" @click="openPath(e.fullPath)"><SquareArrowOutUpRight :size="13" /></button>
          </span>
        </div>
        <p v-if="searchTruncated" class="empty small">{{ t('ui.files.tooMany') }}</p>
      </template>

      <!-- 树 -->
      <template v-else>
        <p v-if="dirs['']?.loading && !rows.length" class="empty">{{ t('ui.files.loading') }}</p>
        <p v-else-if="!rows.length" class="empty">{{ t('ui.files.empty') }}</p>
        <div
          v-for="r in rows"
          :key="r.entry.path"
          class="row"
          :class="{ noise: r.entry.noise }"
          role="treeitem"
          :aria-expanded="r.entry.isDir ? !!dirs[r.entry.path]?.open : undefined"
          :title="tip(r.entry)"
        >
          <button type="button" class="main" :style="{ paddingLeft: 6 + r.depth * 14 + 'px' }" @click="openEntry(r.entry)">
            <ChevronRight v-if="r.entry.isDir" :size="12" class="chev" :class="{ open: dirs[r.entry.path]?.open }" />
            <span v-else class="chev" />
            <FolderOpen v-if="r.entry.isDir && dirs[r.entry.path]?.open" :size="14" class="ic dir" />
            <Folder v-else-if="r.entry.isDir" :size="14" class="ic dir" />
            <FileText v-else-if="r.entry.previewable" :size="14" class="ic" />
            <File v-else :size="14" class="ic" />
            <span class="name">{{ r.entry.name }}</span>
          </button>
          <span class="acts">
            <button v-if="isMd(r.entry)" type="button" class="icon" :title="t('ui.doc.openInCanvas')" @click="openDocument(conv, r.entry.fullPath)"><PenLine :size="13" /></button>
            <button v-if="!r.entry.isDir" type="button" class="icon" :title="t('ui.files.attach')" @click="attach(r.entry)"><Paperclip :size="13" /></button>
            <button type="button" class="icon" :title="t('ui.files.reveal')" @click="openPath(r.entry.fullPath)"><SquareArrowOutUpRight :size="13" /></button>
          </span>
        </div>
        <p v-if="dirs['']?.truncated" class="empty small">{{ t('ui.files.truncated') }}</p>
      </template>
    </div>
    <p class="hint">{{ t('ui.files.hint') }}</p>
  </aside>
</template>

<style scoped>
.files {
  display: flex;
  flex-direction: column;
  width: 320px;
  min-height: 0;
  border-left: 1px solid var(--line);
  background: var(--loom);
  font-size: var(--t-sm);
}
.head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 10px 10px 6px 12px;
  color: var(--ink-soft);
}
.head strong {
  overflow: hidden;
  color: var(--ink);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.spacer {
  flex: 1;
}
.icon {
  display: inline-grid;
  flex: none;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.icon:hover {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.search {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0 10px 6px;
  padding: 0 4px 0 8px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink-faint);
}
.search input {
  flex: 1;
  min-width: 0;
  height: 30px;
  border: 0;
  background: transparent;
  color: var(--ink);
  font-size: var(--t-sm);
  outline: none;
}
.search input::-webkit-search-cancel-button {
  display: none;
}
.list {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 2px 6px 10px;
}
.empty {
  padding: 20px 10px;
  color: var(--ink-faint);
  text-align: center;
}
.empty.small {
  padding: 8px;
  font-size: var(--t-xs);
}
.row {
  position: relative;
  display: flex;
  align-items: center;
  border-radius: 6px;
}
.row:hover {
  background: var(--cloth-sunk);
}
.row.noise {
  opacity: 0.55;
}
.main {
  display: flex;
  flex: 1;
  align-items: center;
  gap: 5px;
  min-width: 0;
  height: 28px;
  padding-right: 6px;
  color: var(--ink);
  text-align: left;
}
.chev {
  flex: none;
  width: 12px;
  color: var(--ink-faint);
  transition: transform 0.12s;
}
.chev.open {
  transform: rotate(90deg);
}
.ic {
  flex: none;
  color: var(--ink-faint);
}
.ic.dir {
  color: var(--amber, #d97706);
}
.name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.acts {
  display: none;
  flex: none;
  padding-right: 2px;
}
.row:hover .acts,
.row:focus-within .acts {
  display: inline-flex;
}
.hint {
  padding: 6px 12px;
  border-top: 1px solid var(--line);
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
</style>
