<script setup lang="ts">
/**
 * 一个 Monaco 编辑器：显示当前标签。AI 改过、还没看完的标签（tab.before 不为空）显示对比视图（左边改之前、右边改之后，
 * 面板窄时上下合在一起显示），看完点「收起对比」回到普通编辑。
 * 每个文件一个 model：切标签时撤销记录还在。只读文件（二进制、太大、链接到工作区外）不能改。
 */
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import type * as Monaco from 'monaco-editor/esm/vs/editor/editor.api'
import { languageOf, loadMonaco } from './monaco'
import type { CodeTab } from './store'

const props = defineProps<{ tab: CodeTab }>()
const emit = defineEmits<{ save: [] }>()

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
    } else if (editor.value.getModel() !== model) {
      editor.value.setModel(model)
    }
    editor.value.updateOptions({ readOnly: tab.readOnly })
  }
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
