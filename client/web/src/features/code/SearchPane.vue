<script setup lang="ts">
/**
 * 编码模式左边的「搜索」（参照 VS Code 的 Ctrl+Shift+F）：在这个任务的工作区里找文字。
 * 区分大小写（Aa）、全字匹配（ab）、正则（.*）；展开后可以填「包含的文件」「排除的文件」（如 *.ts, src）。
 * 结果按文件分组，点一行打开文件并跳到那里。只读：不做替换。
 */
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronRight, FileText, ListCollapse, MoreHorizontal } from '@lucide/vue'
import { bridge } from '../../bridge'
import { current } from '../../store'
import { openCodeAt } from './store'
import type { CodeSearchFile, CodeSearchHit, CodeSearchResult } from './types'

const { t } = useI18n()

const q = reactive({ query: '', regex: false, matchCase: false, wholeWord: false, include: '', exclude: '' })
const showFilters = ref(false)
const result = ref<CodeSearchResult | null>(null)
const busy = ref(false)
const collapsed = ref(new Set<string>())
const box = ref<HTMLInputElement>()

let timer = 0
let seq = 0
async function run() {
  clearTimeout(timer)
  const id = current.value?.id
  if (!id || !q.query.trim()) {
    result.value = null
    return
  }
  const mine = ++seq
  busy.value = true
  try {
    const r = await bridge.codeSearch(id, { ...q })
    if (mine === seq) {
      result.value = r
      collapsed.value = new Set()
    }
  } catch (e) {
    if (mine === seq) result.value = { files: [], total: 0, truncated: false, error: String((e as Error)?.message ?? e) }
  } finally {
    if (mine === seq) busy.value = false
  }
}
// 打字停一下就搜（和 VS Code 一样）；换了选项马上搜
watch(() => q.query, () => {
  clearTimeout(timer)
  timer = window.setTimeout(run, 350)
})
watch(() => [q.regex, q.matchCase, q.wholeWord], () => void run())
watch(() => current.value?.id, () => (result.value = null))

const summary = computed(() => {
  const r = result.value
  if (!r || r.error) return ''
  if (!r.total) return t('ui.code.search.none')
  return t(r.truncated ? 'ui.code.search.countTruncated' : 'ui.code.search.count', { n: r.total, files: r.files.length })
})

const name = (p: string) => p.split(/[\\/]/).pop() ?? p
const dir = (p: string) => p.slice(0, Math.max(0, p.length - name(p).length)).replace(/[\\/]+$/, '')

function toggle(path: string) {
  const next = new Set(collapsed.value)
  if (next.has(path)) next.delete(path)
  else next.add(path)
  collapsed.value = next
}
function collapseAll() {
  collapsed.value = new Set(result.value?.files.map((f) => f.path) ?? [])
}

/** 一行拆成「前 / 匹配 / 后」三段（高亮匹配的字） */
function parts(h: CodeSearchHit) {
  const start = h.column - 1 - h.textOffset
  const text = h.text
  const lead = text.slice(0, start)
  // 匹配前的部分太长时只留最后一小截（左栏很窄），匹配总在看得见的地方
  const trimmed = lead.trimStart()
  return { before: trimmed.length > 16 ? '…' + trimmed.slice(-14) : trimmed, match: text.slice(start, start + h.length), after: text.slice(start + h.length) }
}

function open(f: CodeSearchFile, h: CodeSearchHit) {
  const id = current.value?.id
  if (id) void openCodeAt(id, f.path, h.line, h.column, h.length)
}

/** Ctrl+Shift+F 进来时把焦点放到输入框（选中已有的词） */
function focus() {
  void nextTick(() => {
    box.value?.focus()
    box.value?.select()
  })
}
onMounted(() => {
  focus()
  window.addEventListener('flyknit:code-search', focus)
})
onBeforeUnmount(() => window.removeEventListener('flyknit:code-search', focus))
</script>

<template>
  <div class="search-pane">
    <div class="query">
      <input ref="box" v-model="q.query" :placeholder="t('ui.code.search.placeholder')" spellcheck="false" @keydown.enter="run" />
      <button type="button" class="opt" :class="{ on: q.matchCase }" :title="t('ui.code.search.matchCase')" @click="q.matchCase = !q.matchCase">Aa</button>
      <button type="button" class="opt word" :class="{ on: q.wholeWord }" :title="t('ui.code.search.wholeWord')" @click="q.wholeWord = !q.wholeWord">ab</button>
      <button type="button" class="opt" :class="{ on: q.regex }" :title="t('ui.code.search.regex')" @click="q.regex = !q.regex">.*</button>
    </div>
    <div class="row">
      <button type="button" class="link" :class="{ on: showFilters }" :title="t('ui.code.search.filters')" @click="showFilters = !showFilters"><MoreHorizontal :size="14" /></button>
      <span class="summary" :class="{ error: !!result?.error }">{{ busy ? t('ui.code.search.searching') : result?.error || summary }}</span>
      <button v-if="result?.files.length" type="button" class="link" :title="t('ui.code.search.collapseAll')" @click="collapseAll"><ListCollapse :size="14" /></button>
    </div>
    <template v-if="showFilters">
      <label class="filter">
        <span>{{ t('ui.code.search.include') }}</span>
        <input v-model="q.include" placeholder="*.ts, src" spellcheck="false" @keydown.enter="run" />
      </label>
      <label class="filter">
        <span>{{ t('ui.code.search.exclude') }}</span>
        <input v-model="q.exclude" placeholder="*.min.js, test" spellcheck="false" @keydown.enter="run" />
      </label>
    </template>

    <div class="results">
      <div v-for="f in result?.files ?? []" :key="f.path" class="file">
        <button type="button" class="file-head" :title="f.path" @click="toggle(f.path)">
          <component :is="collapsed.has(f.path) ? ChevronRight : ChevronDown" :size="13" />
          <FileText :size="13" class="ficon" />
          <span class="fname">{{ name(f.path) }}</span>
          <span class="fdir">{{ dir(f.path) }}</span>
          <span class="badge">{{ f.hits.length }}</span>
        </button>
        <template v-if="!collapsed.has(f.path)">
          <button v-for="(h, i) in f.hits" :key="i" type="button" class="hit" :title="`${f.path}:${h.line}`" @click="open(f, h)">
            <span class="ln">{{ h.line }}</span>
            <span class="txt">{{ parts(h).before }}<mark>{{ parts(h).match }}</mark>{{ parts(h).after }}</span>
          </button>
        </template>
      </div>
      <p v-if="!q.query.trim()" class="hint">{{ t('ui.code.search.hint') }}</p>
    </div>
  </div>
</template>

<style scoped>
/* 整个左栏一起滚动（左栏自己有滚动条） */
.search-pane {
  font-size: calc(12.5px * var(--font-scale));
}
.query {
  display: flex;
  align-items: center;
  gap: 1px;
  margin: 0 8px;
  padding: 0 3px 0 8px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
}
.query:focus-within {
  border-color: var(--indigo);
}
.query input {
  flex: 1;
  min-width: 0;
  height: 28px;
  border: 0;
  outline: none;
  background: transparent;
  color: var(--ink);
  font-size: inherit;
}
.opt {
  flex: none;
  width: 22px;
  height: 20px;
  padding: 0;
  border: 1px solid transparent;
  border-radius: 4px;
  background: transparent;
  color: var(--ink-faint);
  font-family: var(--mono, monospace);
  font-size: 11px;
  cursor: pointer;
}
.opt.word {
  text-decoration: underline;
}
.opt:hover {
  color: var(--ink);
}
.opt.on {
  border-color: var(--indigo);
  background: var(--indigo-wash);
  color: var(--indigo);
}
.row {
  display: flex;
  align-items: center;
  gap: 4px;
  margin: 4px 8px;
}
.summary {
  flex: 1;
  min-width: 0;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
}
.summary.error {
  color: var(--red, #e5534b);
}
.link {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--ink-faint);
  cursor: pointer;
}
.link:hover,
.link.on {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
  color: var(--ink);
}
.filter {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 2px 8px 4px;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
}
.filter input {
  height: 26px;
  padding: 0 8px;
  border: 1px solid var(--line);
  border-radius: 5px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12px * var(--font-scale));
}
.results {
  margin-top: 4px;
}
.file-head,
.hit {
  display: flex;
  align-items: center;
  gap: 5px;
  width: 100%;
  padding: 2px 8px;
  border: 0;
  background: transparent;
  color: var(--ink);
  text-align: left;
  cursor: pointer;
  white-space: nowrap;
}
.file-head:hover,
.hit:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.file-head svg {
  flex: none;
  color: var(--ink-faint);
}
.ficon {
  color: var(--indigo) !important;
}
.fname {
  flex: none;
  font-weight: 500;
}
.fdir {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  text-overflow: ellipsis;
}
.badge {
  flex: none;
  min-width: 18px;
  padding: 0 5px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
  color: var(--ink-soft);
  font-size: 10.5px;
  text-align: center;
}
.hit {
  padding-left: 26px;
}
.ln {
  flex: none;
  min-width: 26px;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  text-align: right;
}
.txt {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  font-family: var(--mono, monospace);
  font-size: calc(11.5px * var(--font-scale));
  text-overflow: ellipsis;
}
mark {
  border-radius: 2px;
  background: color-mix(in srgb, var(--amber) 40%, transparent);
  color: var(--ink);
}
.hint {
  margin: 12px;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
  line-height: 1.5;
}
</style>
