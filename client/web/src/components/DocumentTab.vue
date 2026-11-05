<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Bold,
  Code2,
  FileDown,
  FileText,
  FolderOpen,
  Heading1,
  Heading2,
  Heading3,
  Italic,
  List,
  ListOrdered,
  Pilcrow,
  Printer,
  Quote,
  Redo2,
  Sparkles,
  Strikethrough,
  Undo2,
  X,
} from '@lucide/vue'
import { bridge } from '../bridge'
import { closeDocument, current, openPath, setPreviewWidth, state, toast } from '../store'
import { htmlToMarkdown, markdownToDocModel, printableHtml, renderDocument } from '../documents'

/**
 * 右侧「文档」标签：AI 写的 Markdown 文稿，员工像在 Word 里一样直接改。
 * - 改完停 1.5 秒自动保存；保存带版本号，AI 同时改了文件不会被覆盖，弹出提示让员工选；
 * - AI 改了文件（document.changed）而员工没在改：直接刷新；
 * - 选中一段点「让 AI 改」，把那段放进输入框，告诉 AI 怎么改；
 * - 导出 Word（宿主写 docx）、打印 / 另存 PDF（系统打印对话框）。
 */
const { t } = useI18n()

const doc = computed(() => (current.value ? state.docs[current.value.id] ?? null : null))
const editor = ref<HTMLElement>()
const source = ref('')
const mode = ref<'edit' | 'source'>('edit')
const loading = ref(false)
const error = ref('')
const version = ref('')
const dirty = ref(false)
const saving = ref(false)
const savedAt = ref<Date | null>(null)
/** 文件在员工编辑期间被改过（多半是 AI）：先不覆盖，让员工选 */
const conflict = ref<{ text: string; version: string } | null>(null)
const hasSelection = ref(false)

let saveTimer: ReturnType<typeof setTimeout> | undefined
let loadedFor = ''

async function load(keepScroll = false) {
  const d = doc.value
  const conv = current.value?.id
  if (!d || !conv) return
  loading.value = !keepScroll
  error.value = ''
  try {
    const r = await bridge.loadDocument(conv, d.path)
    if (doc.value?.path !== d.path) return
    if (!r.ok || !r.exists) {
      error.value = t(r.ok ? 'ui.doc.missing' : 'ui.doc.cannotOpen')
      return
    }
    if (r.tooLarge) {
      error.value = t('ui.doc.tooLarge')
      return
    }
    version.value = r.version
    show(r.text, keepScroll)
    dirty.value = false
    conflict.value = null
    loadedFor = d.path
  } catch (e) {
    error.value = String(e)
  } finally {
    loading.value = false
  }
}

/** 把 Markdown 放进编辑器（或源码框） */
async function show(text: string, keepScroll: boolean) {
  source.value = text
  await nextTick()
  const el = editor.value
  if (!el) return
  const top = el.scrollTop
  el.innerHTML = renderDocument(text)
  if (keepScroll) el.scrollTop = top
}

/** 当前内容（Markdown） */
function currentText(): string {
  if (mode.value === 'source') return source.value
  return editor.value ? htmlToMarkdown(editor.value) : source.value
}

function onInput() {
  dirty.value = true
  clearTimeout(saveTimer)
  saveTimer = setTimeout(() => void save(), 1500)
}

async function save(force = false) {
  clearTimeout(saveTimer)
  const d = doc.value
  const conv = current.value?.id
  if (!d || !conv || saving.value || (!dirty.value && !force)) return
  saving.value = true
  try {
    const text = currentText()
    const r = await bridge.saveDocument(conv, d.path, text, version.value)
    if (r.ok) {
      version.value = r.version
      source.value = text
      dirty.value = false
      savedAt.value = new Date()
    } else if (r.conflict) {
      conflict.value = { text: r.currentText, version: r.version }
    } else {
      toast(r.message || t('ui.doc.saveFailed'))
    }
  } catch (e) {
    toast(String(e))
  } finally {
    saving.value = false
  }
}

/** 冲突：用文件里现在的版本（放弃我刚才的改动） */
function takeTheirs() {
  const c = conflict.value
  if (!c) return
  version.value = c.version
  conflict.value = null
  dirty.value = false
  void show(c.text, true)
}

/** 冲突：用我的覆盖 */
function keepMine() {
  const c = conflict.value
  if (!c) return
  version.value = c.version
  conflict.value = null
  dirty.value = true
  void save(true)
}

// 换了文档、或者 AI 又改了这个文件
watch(
  () => [doc.value?.path, doc.value?.rev] as const,
  async ([path], old) => {
    if (!path) return
    const samePath = old?.[0] === path && loadedFor === path
    if (!samePath) {
      // 换文档前把手上没存的先存掉
      if (dirty.value && loadedFor) await save()
      mode.value = 'edit'
      void load()
      return
    }
    if (dirty.value || saving.value) {
      // 员工正在改：不直接刷新，取一下 AI 的版本作为冲突让员工选
      const conv = current.value?.id
      if (!conv) return
      const r = await bridge.loadDocument(conv, path).catch(() => null)
      if (r?.ok && r.version !== version.value) conflict.value = { text: r.text, version: r.version }
      return
    }
    void load(true)
  },
  { immediate: true },
)

// ---------- 编辑：工具栏 ----------

function exec(command: string, value?: string) {
  editor.value?.focus()
  document.execCommand(command, false, value)
  onInput()
}

const blocks = [
  { tag: 'p', icon: Pilcrow, key: 'paragraph' },
  { tag: 'h1', icon: Heading1, key: 'h1' },
  { tag: 'h2', icon: Heading2, key: 'h2' },
  { tag: 'h3', icon: Heading3, key: 'h3' },
  { tag: 'blockquote', icon: Quote, key: 'quote' },
] as const

function onKeydown(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
    e.preventDefault()
    void save(true)
  }
}

/** 粘贴只要纯文字：从 Word、网页粘过来的一堆样式存回 Markdown 时只会变成乱码 */
function onPaste(e: ClipboardEvent) {
  const text = e.clipboardData?.getData('text/plain')
  if (text == null) return
  e.preventDefault()
  document.execCommand('insertText', false, text)
}

function onSelectionChange() {
  const sel = document.getSelection()
  const el = editor.value
  hasSelection.value = !!sel && !sel.isCollapsed && !!el && el.contains(sel.anchorNode) && sel.toString().trim().length > 0
}

async function toggleSource() {
  if (mode.value === 'edit') {
    source.value = currentText()
    mode.value = 'source'
  } else {
    mode.value = 'edit'
    await show(source.value, false)
  }
}

// ---------- 让 AI 改选中的这段 ----------

function askAi() {
  const d = doc.value
  const sel = document.getSelection()?.toString().trim() ?? ''
  if (!d || !sel) return
  const quoted = sel.split('\n').map((l) => '> ' + l).join('\n')
  state.composerFill = t('ui.doc.askFill', { name: d.name, text: quoted })
  window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
}

// ---------- 导出 ----------

async function exportWord() {
  const d = doc.value
  if (!d) return
  await save()
  const title = d.name.replace(/\.(md|markdown)$/i, '')
  try {
    const r = await bridge.exportDocx(d.path, markdownToDocModel(currentText(), title))
    if (r.ok) toast(t('ui.doc.exported', { path: r.path }))
    else if (!r.cancelled) toast(r.message || t('ui.doc.exportFailed'))
  } catch (e) {
    toast(String(e))
  }
}

/** 打印：只打印文档本身。系统打印对话框里选「另存为 PDF」就是 PDF */
function print() {
  const d = doc.value
  if (!d) return
  const frame = document.createElement('iframe')
  frame.style.cssText = 'position:fixed;width:0;height:0;border:0;right:0;bottom:0'
  frame.srcdoc = printableHtml(d.name.replace(/\.(md|markdown)$/i, ''), currentText())
  frame.onload = () => {
    frame.contentWindow?.focus()
    frame.contentWindow?.print()
    setTimeout(() => frame.remove(), 1000)
  }
  document.body.appendChild(frame)
}

async function close() {
  if (dirty.value) await save()
  if (current.value) closeDocument(current.value.id)
}

// ---------- 拖拽调整宽度（和文件预览共用一个宽度） ----------
const dragging = ref(false)
function startDrag(e: PointerEvent) {
  dragging.value = true
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  e.preventDefault()
}
function onDrag(e: PointerEvent) {
  if (dragging.value) setPreviewWidth(window.innerWidth - e.clientX)
}
function endDrag(e: PointerEvent) {
  dragging.value = false
  ;(e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId)
}

const status = computed(() => {
  if (saving.value) return t('ui.doc.saving')
  if (dirty.value) return t('ui.doc.unsaved')
  if (savedAt.value) return t('ui.doc.saved')
  return ''
})

onMounted(() => {
  document.addEventListener('selectionchange', onSelectionChange)
  // 默认段落用 <p>（而不是 <div>），转回 Markdown 时段落才分得清
  document.execCommand('defaultParagraphSeparator', false, 'p')
})
onBeforeUnmount(() => {
  document.removeEventListener('selectionchange', onSelectionChange)
  if (dirty.value) void save()
})
</script>

<template>
  <aside class="doc" :style="{ width: state.previewWidth + 'px' }" :aria-label="t('ui.doc.title')">
    <div
      class="grip"
      :class="{ dragging }"
      role="separator"
      aria-orientation="vertical"
      @pointerdown="startDrag"
      @pointermove="onDrag"
      @pointerup="endDrag"
    />
    <header class="head">
      <FileText :size="15" />
      <strong class="name" :title="doc?.path">{{ doc?.name }}</strong>
      <span class="status">{{ status }}</span>
      <span class="spacer" />
      <button type="button" class="icon" :title="t('ui.doc.reveal')" @click="doc && openPath(doc.path)"><FolderOpen :size="15" /></button>
      <button type="button" class="icon" :title="t('ui.doc.print')" @click="print"><Printer :size="15" /></button>
      <button type="button" class="pill" :title="t('ui.doc.exportHint')" @click="exportWord"><FileDown :size="14" /> {{ t('ui.doc.exportWord') }}</button>
      <button type="button" class="icon" :title="t('ui.doc.close')" @click="close"><X :size="15" /></button>
    </header>

    <div class="toolbar" role="toolbar" :aria-label="t('ui.doc.toolbar')">
      <template v-if="mode === 'edit'">
        <button v-for="b in blocks" :key="b.tag" type="button" class="tool" :title="t(`ui.doc.${b.key}`)" @mousedown.prevent @click="exec('formatBlock', b.tag)">
          <component :is="b.icon" :size="15" />
        </button>
        <span class="sep" />
        <button type="button" class="tool" :title="t('ui.doc.bold')" @mousedown.prevent @click="exec('bold')"><Bold :size="15" /></button>
        <button type="button" class="tool" :title="t('ui.doc.italic')" @mousedown.prevent @click="exec('italic')"><Italic :size="15" /></button>
        <button type="button" class="tool" :title="t('ui.doc.strike')" @mousedown.prevent @click="exec('strikeThrough')"><Strikethrough :size="15" /></button>
        <span class="sep" />
        <button type="button" class="tool" :title="t('ui.doc.bullets')" @mousedown.prevent @click="exec('insertUnorderedList')"><List :size="15" /></button>
        <button type="button" class="tool" :title="t('ui.doc.numbers')" @mousedown.prevent @click="exec('insertOrderedList')"><ListOrdered :size="15" /></button>
        <span class="sep" />
        <button type="button" class="tool" :title="t('ui.doc.undo')" @mousedown.prevent @click="exec('undo')"><Undo2 :size="15" /></button>
        <button type="button" class="tool" :title="t('ui.doc.redo')" @mousedown.prevent @click="exec('redo')"><Redo2 :size="15" /></button>
      </template>
      <span class="spacer" />
      <button type="button" class="tool ai" :disabled="!hasSelection || mode !== 'edit'" :title="t('ui.doc.askAiHint')" @mousedown.prevent @click="askAi">
        <Sparkles :size="14" /> {{ t('ui.doc.askAi') }}
      </button>
      <button type="button" class="tool" :class="{ on: mode === 'source' }" :title="t('ui.doc.source')" @click="toggleSource"><Code2 :size="15" /></button>
    </div>

    <div v-if="conflict" class="conflict" role="alert">
      <span>{{ t('ui.doc.conflict') }}</span>
      <button type="button" class="pill" @click="takeTheirs">{{ t('ui.doc.takeTheirs') }}</button>
      <button type="button" class="pill ghost" @click="keepMine">{{ t('ui.doc.keepMine') }}</button>
    </div>

    <p v-if="error" class="empty">{{ error }}</p>
    <p v-else-if="loading" class="empty">{{ t('ui.doc.loading') }}</p>
    <div
      v-show="!error && !loading && mode === 'edit'"
      ref="editor"
      class="page"
      contenteditable="true"
      spellcheck="false"
      @input="onInput"
      @keydown="onKeydown"
      @paste="onPaste"
      @blur="save()"
    />
    <textarea
      v-if="!error && !loading && mode === 'source'"
      v-model="source"
      class="page source"
      spellcheck="false"
      @input="onInput"
      @keydown="onKeydown"
      @blur="save()"
    />
    <p class="hint">{{ t('ui.doc.hint') }}</p>
  </aside>
</template>

<style scoped>
.doc {
  position: relative;
  display: flex;
  flex-direction: column;
  min-width: 320px;
  max-width: 70vw;
  border-left: 1px solid var(--line);
  background: var(--cloth);
  font-size: var(--t-sm);
}
.grip {
  position: absolute;
  top: 0;
  bottom: 0;
  left: -3px;
  z-index: 2;
  width: 6px;
  cursor: col-resize;
}
.grip:hover,
.grip.dragging {
  background: var(--indigo);
  opacity: 0.35;
}
.head {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border-bottom: 1px solid var(--line);
  color: var(--ink-soft);
}
.name {
  overflow: hidden;
  color: var(--ink);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.status {
  flex: none;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.spacer {
  flex: 1;
}
.icon,
.tool {
  display: inline-grid;
  flex: none;
  place-items: center;
  min-width: 24px;
  height: 26px;
  border-radius: 6px;
  color: var(--ink-soft);
}
.icon:hover,
.tool:hover:not(:disabled),
.tool.on {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.tool:disabled {
  opacity: 0.4;
  cursor: default;
}
.tool.ai {
  display: inline-flex;
  gap: 4px;
  padding: 0 8px;
  color: var(--indigo);
  font-size: var(--t-xs);
}
.pill {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  padding: 4px 10px;
  border-radius: 999px;
  background: var(--indigo);
  color: #fff;
  font-size: var(--t-xs);
}
.pill.ghost {
  background: transparent;
  border: 1px solid var(--line);
  color: var(--ink);
}
.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px;
  padding: 4px 8px;
  border-bottom: 1px solid var(--line);
  background: var(--loom);
}
.sep {
  width: 1px;
  height: 16px;
  margin: 0 2px;
  background: var(--line);
}
.conflict {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--line);
  background: color-mix(in srgb, var(--amber, #d97706) 12%, transparent);
  color: var(--ink);
}
.conflict span {
  flex: 1 1 200px;
}
.empty {
  padding: 24px;
  color: var(--ink-faint);
  text-align: center;
}
.page {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 20px 28px 40px;
  color: var(--ink);
  font-size: var(--t-md, 15px);
  line-height: 1.75;
  outline: none;
}
.page.source {
  width: 100%;
  border: 0;
  background: var(--cloth);
  font-family: Consolas, 'Cascadia Mono', monospace;
  font-size: var(--t-sm);
  resize: none;
}
.page :deep(h1),
.page :deep(h2),
.page :deep(h3) {
  margin: 1.1em 0 0.5em;
  line-height: 1.35;
}
.page :deep(h1) {
  font-size: 1.6em;
}
.page :deep(h2) {
  font-size: 1.3em;
}
.page :deep(h3) {
  font-size: 1.12em;
}
.page :deep(p) {
  margin: 0.5em 0;
}
.page :deep(ul),
.page :deep(ol) {
  margin: 0.4em 0;
  padding-left: 1.6em;
}
.page :deep(blockquote) {
  margin: 0.8em 0;
  padding: 0.2em 1em;
  border-left: 3px solid var(--line-strong, var(--line));
  color: var(--ink-soft);
}
.page :deep(table) {
  width: 100%;
  margin: 0.8em 0;
  border-collapse: collapse;
}
.page :deep(th),
.page :deep(td) {
  padding: 6px 8px;
  border: 1px solid var(--line);
  text-align: left;
}
.page :deep(th) {
  background: var(--cloth-sunk);
}
.page :deep(pre) {
  padding: 10px 12px;
  border-radius: 6px;
  background: var(--cloth-sunk);
  white-space: pre-wrap;
}
.page :deep(code) {
  font-family: Consolas, 'Cascadia Mono', monospace;
  font-size: 0.92em;
}
.page :deep(hr) {
  margin: 1.2em 0;
  border: 0;
  border-top: 1px solid var(--line);
}
.hint {
  padding: 6px 12px;
  border-top: 1px solid var(--line);
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
</style>
