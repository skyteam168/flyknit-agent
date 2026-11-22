<script setup lang="ts">
/**
 * 一个 Monaco 编辑器：显示当前标签。AI 改过、还没看完的标签（tab.before 不为空）显示对比视图（左边改之前、右边改之后，
 * 面板窄时上下合在一起显示），看完点「收起对比」回到普通编辑。
 * 每个文件一个 model：切标签时撤销记录还在。只读文件（二进制、太大、链接到工作区外）不能改。
 */
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type * as Monaco from 'monaco-editor/esm/vs/editor/editor.api'
import { languageOf, loadMonaco } from './monaco'
import { currentCode, type CodeSelection, type CodeTab } from './store'

const props = defineProps<{ tab: CodeTab }>()
const emit = defineEmits<{ save: []; addToChat: [sel: CodeSelection]; quickEdit: [sel: CodeSelection, instruction: string] }>()
const { t } = useI18n()


const host = ref<HTMLElement>()
const failed = ref('')
let monaco: typeof Monaco | null = null
const editor = shallowRef<Monaco.editor.IStandaloneCodeEditor | null>(null)
const diff = shallowRef<Monaco.editor.IStandaloneDiffEditor | null>(null)
const models = new Map<string, Monaco.editor.ITextModel>()
let original: Monaco.editor.ITextModel | null = null
let observer: MutationObserver | null = null
let resize: ResizeObserver | null = null

const isDark = () => document.documentElement.dataset.theme === 'dark'

function modelFor(tab: CodeTab): Monaco.editor.ITextModel {
  let m = models.get(tab.fullPath)
  if (!m || m.isDisposed()) {
    m = monaco!.editor.createModel(tab.draft, languageOf(tab.path))
    m.onDidChangeContent(() => {
      // 编辑器里的改动同步回标签（「未保存」的点、保存时用）
      const t = props.tab
      if (t.fullPath === tab.fullPath && !t.readOnly) t.draft = m!.getValue()
    })
    models.set(tab.fullPath, m)
  } else if (m.getValue() !== tab.draft) {
    // 外面改了（AI 改了文件、员工选了「用 AI 的」）：整体换掉，光标位置尽量留着
    m.pushEditOperations([], [{ range: m.getFullModelRange(), text: tab.draft }], () => null)
  }
  return m
}

const common = (): Monaco.editor.IEditorOptions => ({
  automaticLayout: false,
  fontSize: 13,
  fontFamily: 'Cascadia Code, Consolas, "Courier New", monospace',
  minimap: { enabled: false },
  scrollBeyondLastLine: false,
  wordWrap: 'off',
  renderWhitespace: 'selection',
  smoothScrolling: true,
})

function render() {
  if (!monaco || !host.value) return
  const tab = props.tab
  const model = modelFor(tab)
  const wantDiff = tab.before !== null
  if (wantDiff) {
    editor.value?.dispose()
    editor.value = null
    if (!diff.value) {
      diff.value = monaco.editor.createDiffEditor(host.value, {
        ...common(),
        renderSideBySide: host.value.clientWidth > 760,
        originalEditable: false,
        readOnly: tab.readOnly,
      })
      diff.value.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => emit('save'))
      attachSelectionUi(diff.value.getModifiedEditor())
    }
    // 先换上新的再释放旧的（反过来 Monaco 会报 model 已释放）
    const previous = original
    original = monaco.editor.createModel(tab.before ?? '', languageOf(tab.path))
    diff.value.setModel({ original, modified: model })
    previous?.dispose()
    diff.value.getModifiedEditor().updateOptions({ readOnly: tab.readOnly })
    // 算完对比后跳到第一处改动
    const once = diff.value.onDidUpdateDiff(() => {
      once.dispose()
      diff.value?.revealFirstDiff()
    })
  } else {
    if (diff.value) {
      // 先把对比视图的 model 卸下再释放
      diff.value.setModel(null)
      diff.value.dispose()
      diff.value = null
    }
    original?.dispose()
    original = null
    if (!editor.value) {
      editor.value = monaco.editor.create(host.value, { ...common(), model })
      editor.value.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => emit('save'))
      attachSelectionUi(editor.value)
    } else if (editor.value.getModel() !== model) {
      editor.value.setModel(model)
    }
    // 方案文档是中文说明：中文标点不当「易混淆字符」标黄框，打字时也不弹单词补全，长段落自动换行
    editor.value.updateOptions({
      readOnly: tab.readOnly,
      unicodeHighlight: { ambiguousCharacters: !tab.virtual },
      wordBasedSuggestions: tab.virtual ? 'off' : 'matchingDocuments',
      quickSuggestions: tab.virtual ? false : { other: 'on', comments: 'off', strings: 'off' },
      wordWrap: tab.virtual ? 'on' : 'off',
    })
  }
  applyReveal()
}

// 全局搜索点了某一处：跳到那一行、选中匹配的那几个字（每次点击一个新的 at，同一个文件里换行也会跳）
let revealedAt = 0
function applyReveal() {
  const r = props.tab.reveal
  const ed = diff.value?.getModifiedEditor() ?? editor.value
  if (!r || !ed || !monaco || r.at === revealedAt) return
  revealedAt = r.at
  const range = new monaco.Range(r.line, r.column, r.line, r.column + r.length)
  ed.setSelection(range)
  ed.revealRangeInCenter(range)
  ed.focus()
}
watch(() => props.tab.reveal?.at, applyReveal)

// ---------- 选中代码：浮条「添加到对话 Ctrl+L · 快速修改 Ctrl+K」（参照 Cursor） ----------
function selectionOf(ed: Monaco.editor.ICodeEditor): CodeSelection | null {
  const sel = ed.getSelection()
  const model = ed.getModel()
  if (!sel || sel.isEmpty() || !model) return null
  // 选到下一行开头（第 0 列）时不算那一行
  const to = sel.endColumn === 1 && sel.endLineNumber > sel.startLineNumber ? sel.endLineNumber - 1 : sel.endLineNumber
  return { path: props.tab.path, language: languageOf(props.tab.path), from: sel.startLineNumber, to, code: model.getValueInRange(sel) }
}

function attachSelectionUi(ed: Monaco.editor.IStandaloneCodeEditor) {
  const m = monaco!
  const dom = document.createElement('div')
  dom.className = 'fk-sel'
  let position: Monaco.IPosition | null = null
  let editing: CodeSelection | null = null
  const widget: Monaco.editor.IContentWidget = {
    allowEditorOverflow: true,
    getId: () => 'flyknit.selection',
    getDomNode: () => dom,
    getPosition: () =>
      position ? { position, preference: [m.editor.ContentWidgetPositionPreference.ABOVE, m.editor.ContentWidgetPositionPreference.BELOW] } : null,
  }
  ed.addContentWidget(widget)

  const button = (label: string, key: string, run: () => void) => {
    const b = document.createElement('button')
    b.type = 'button'
    b.innerHTML = '<span></span><kbd></kbd>'
    b.querySelector('span')!.textContent = label
    b.querySelector('kbd')!.textContent = key
    b.addEventListener('mousedown', (e) => e.preventDefault()) // 别让编辑器失去选区
    b.addEventListener('click', run)
    return b
  }

  const hide = () => {
    position = null
    editing = null
    dom.classList.remove('editing')
    dom.replaceChildren()
    ed.layoutContentWidget(widget)
  }

  const showBar = () => {
    const sel = selectionOf(ed)
    if (!sel) return hide()
    editing = null
    position = { lineNumber: ed.getSelection()!.startLineNumber, column: 1 }
    dom.replaceChildren(button(t('ui.code.addToChat'), 'Ctrl+L', addToChat), button(t('ui.code.quickEdit'), 'Ctrl+K', quickEdit))
    ed.layoutContentWidget(widget)
  }

  const addToChat = () => {
    const sel = selectionOf(ed)
    if (!sel) return
    emit('addToChat', sel)
    hide()
  }

  const quickEdit = () => {
    const sel = selectionOf(ed)
    if (!sel || props.tab.readOnly) return
    editing = sel
    position = { lineNumber: sel.from, column: 1 }
    const input = document.createElement('input')
    input.placeholder = t('ui.code.quickEditPlaceholder', { from: sel.from, to: sel.to })
    const hint = document.createElement('small')
    hint.textContent = t('ui.code.quickEditHint')
    input.addEventListener('keydown', (e) => {
      e.stopPropagation()
      if (e.key !== 'Escape' && e.key !== 'Enter') return
      // 这一下按键到此为止：不然输入框收起、焦点回到编辑器后，同一个回车会落进编辑器，把选中的代码换成空行
      e.preventDefault()
      if (e.key === 'Enter') {
        if (!input.value.trim() || !editing) return
        emit('quickEdit', editing, input.value.trim())
      }
      hide()
      setTimeout(() => ed.focus(), 0)
    })
    dom.replaceChildren(input, hint)
    dom.classList.add('editing')
    ed.layoutContentWidget(widget)
    setTimeout(() => input.focus(), 0)
  }

  ed.onDidChangeCursorSelection(() => {
    // 光标、选中的行记在标签上：AI 每轮据此知道员工说的「这里」是哪里（store.reportFocus）
    const sel = ed.getSelection()
    if (sel) {
      const to = sel.endColumn === 1 && sel.endLineNumber > sel.startLineNumber ? sel.endLineNumber - 1 : sel.endLineNumber
      props.tab.cursor = { line: sel.positionLineNumber, from: sel.isEmpty() ? 0 : sel.startLineNumber, to: sel.isEmpty() ? 0 : to }
    }
    if (editing) return
    dom.classList.remove('editing')
    showBar()
  })
  ed.onDidBlurEditorWidget(() => {
    // 点浮条上的按钮、在快速修改框里打字时不收
    setTimeout(() => {
      if (!editing && !dom.contains(document.activeElement)) hide()
    }, 150)
  })
  ed.addAction({ id: 'flyknit.addToChat', label: t('ui.code.addToChat'), keybindings: [m.KeyMod.CtrlCmd | m.KeyCode.KeyL], run: addToChat })
  ed.addAction({ id: 'flyknit.quickEdit', label: t('ui.code.quickEdit'), keybindings: [m.KeyMod.CtrlCmd | m.KeyCode.KeyK], run: quickEdit })
}

onMounted(async () => {
  try {
    monaco = await loadMonaco()
  } catch (e) {
    failed.value = String((e as Error)?.message ?? e)
    return
  }
  monaco.editor.setTheme(isDark() ? 'vs-dark' : 'vs')
  observer = new MutationObserver(() => monaco?.editor.setTheme(isDark() ? 'vs-dark' : 'vs'))
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
  resize = new ResizeObserver(() => {
    editor.value?.layout()
    diff.value?.layout()
  })
  if (host.value) resize.observe(host.value)
  render()
})

// 换了文件、AI 改了（before 出现或变了）、只读变了：重画一次（只一个 watch，免得同一次变化画两遍、对比算到一半被换掉）
watch(() => [props.tab.fullPath, props.tab.before, props.tab.readOnly], render)
// 外面改了内容（AI 改了、选了「用 AI 的」）
watch(
  () => props.tab.draft,
  (text) => {
    const m = models.get(props.tab.fullPath)
    if (monaco && m && !m.isDisposed() && m.getValue() !== text) modelFor(props.tab)
  },
)

// 关掉的标签：释放它的 model（每个 model 都占着一份文件内容和撤销记录；不释放的话开过的文件越多越占内存）。
// 当前显示的那个不动；以后再打开会按标签内容重新建
watch(
  () => currentCode.value?.tabs.map((x) => x.fullPath).join('|') ?? '',
  () => {
    const open = new Set(currentCode.value?.tabs.map((x) => x.fullPath) ?? [])
    for (const [path, m] of models) {
      if (open.has(path) || path === props.tab.fullPath) continue
      if (editor.value?.getModel() === m || diff.value?.getModel()?.modified === m) continue
      m.dispose()
      models.delete(path)
    }
  },
)

onBeforeUnmount(() => {
  observer?.disconnect()
  resize?.disconnect()
  editor.value?.dispose()
  diff.value?.setModel(null)
  diff.value?.dispose()
  original?.dispose()
  models.forEach((m) => m.dispose())
})
</script>

<template>
  <div class="monaco-wrap">
    <p v-if="failed" class="failed">{{ failed }}</p>
    <div ref="host" class="monaco-host" />
  </div>
</template>

<style scoped>
.monaco-wrap {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
}
.monaco-host {
  flex: 1;
  min-width: 0;
  min-height: 0;
}
.failed {
  position: absolute;
  inset: 12px;
  color: var(--red);
}
</style>

<style>
/* 选中代码时的浮条（Monaco 的 content widget，不在组件的 scoped 样式里） */
.fk-sel {
  display: flex;
  gap: 2px;
  padding: 3px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  font: 12px/1.4 'Segoe UI', 'Microsoft YaHei', system-ui, sans-serif;
  z-index: 50;
}
.fk-sel:empty {
  display: none;
}
.fk-sel button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--ink);
  cursor: pointer;
  white-space: nowrap;
}
.fk-sel button:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.fk-sel kbd {
  padding: 0 4px;
  border-radius: 3px;
  background: color-mix(in srgb, var(--ink) 10%, transparent);
  color: var(--ink-soft);
  font: 11px Consolas, monospace;
}
.fk-sel.editing {
  flex-direction: column;
  gap: 4px;
  width: 380px;
  padding: 6px;
}
.fk-sel input {
  width: 100%;
  padding: 6px 8px;
  border: 1px solid var(--indigo);
  border-radius: 6px;
  background: var(--loom);
  color: var(--ink);
  font: inherit;
  outline: none;
}
.fk-sel small {
  color: var(--ink-faint);
}
</style>
