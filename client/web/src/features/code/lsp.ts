/**
 * 编码模式的语言服务（宿主 Core/Lsp，IT 统一安装）接到 Monaco：补全、悬停、函数签名、跳到定义、查找引用、重命名、大纲、报错波浪线。
 *
 * - 文件的 model 用文件路径做 URI（Uri.file），跨文件的跳转、引用能对上；没打开的文件按需建只读预览 model（看引用用），用完释放。
 * - 内容停一下就同步给语言服务，再取诊断画波浪线。
 * - 只在 IT 开了开发者工具、员工信任了这个项目、这台电脑装了对应语言服务时工作；否则什么都不做（编辑器照常用）。
 */
import { reactive } from 'vue'
import type * as Monaco from 'monaco-editor/esm/vs/editor/editor.api'
import { bridge } from '../../bridge'
import { i18n } from '../../i18n'
import { current, toast } from '../../store'
import { languageOf } from './monaco'
import type { LspDiagnostic, LspRange, LspStatus } from './types'

type M = typeof Monaco

/** 每个对话的语言服务状态（宿主查的：开关、是否信任、哪些语言装了） */
export const lsp = reactive<{ status: Record<string, LspStatus | null>; dismissed: Record<string, boolean> }>({ status: {}, dismissed: {} })

/** 编辑器里所有打开的文件的报错 / 警告个数（底部状态栏显示，像 VS Code 的 ⊗ 0 ⚠ 0） */
export const problems = reactive({ errors: 0, warnings: 0 })

export async function loadLspStatus(conversationId: string) {
  try {
    lsp.status[conversationId] = await bridge.lspStatus(conversationId)
  } catch {
    lsp.status[conversationId] = null // 旧版宿主：没有语言服务
  }
}

export async function trustProject(conversationId: string, trust: boolean) {
  await bridge.lspTrust(conversationId, trust)
  await loadLspStatus(conversationId)
  // 信任后把开着的文件都同步一遍（波浪线马上出来）
  if (trust) for (const b of bindings.values()) void syncNow(b)
  else for (const b of bindings.values()) monacoRef?.editor.setModelMarkers(b.model, 'lsp', [])
}

const ext = (path: string) => (path.match(/\.[^./\\]+$/)?.[0] ?? '').toLowerCase()

/** 这个文件的语言服务：null = 不支持这种文件 */
export function serverFor(conversationId: string, path: string) {
  const e = ext(path)
  return lsp.status[conversationId]?.servers.find((s) => s.extensions.includes(e)) ?? null
}

/** 这个文件现在能用语言服务 */
function usable(conversationId: string, path: string) {
  const st = lsp.status[conversationId]
  const s = serverFor(conversationId, path)
  return !!st?.enabled && st.trusted && !!s?.available && !s.disabled
}

// ---------- model ↔ 文件 ----------

interface Binding {
  model: Monaco.editor.ITextModel
  /** 工作区里的相对路径（发给宿主） */
  path: string
  fullPath: string
  timer: number
  disposers: Monaco.IDisposable[]
}

const bindings = new Map<string, Binding>()
/** 看引用时临时建的只读 model（没在标签里打开的文件）：URI → model 和它的相对路径 */
const previews = new Map<string, { model: Monaco.editor.ITextModel; path: string }>()
let monacoRef: M | null = null

const conv = () => current.value?.id ?? ''

/** 文件路径 → model 的 URI（和跨文件跳转、引用里的地址对上） */
export function uriFor(m: M, fullPath: string) {
  return m.Uri.file(fullPath)
}

/** MonacoView 建好（或复用）一个文件的 model 后调：开始同步、画波浪线 */
export function bindModel(model: Monaco.editor.ITextModel, path: string, fullPath: string) {
  const key = model.uri.toString()
  previews.delete(key)
  if (bindings.has(key)) return
  const b: Binding = { model, path, fullPath, timer: 0, disposers: [] }
  b.disposers.push(
    model.onDidChangeContent(() => {
      clearTimeout(b.timer)
      b.timer = window.setTimeout(() => void syncNow(b), 500)
    }),
    model.onWillDispose(() => unbind(key)),
  )
  bindings.set(key, b)
  void syncNow(b)
}

function unbind(key: string) {
  const b = bindings.get(key)
  if (!b) return
  clearTimeout(b.timer)
  b.disposers.forEach((d) => d.dispose())
  bindings.delete(key)
  const id = conv()
  if (id && usable(id, b.path)) void bridge.lspClose(id, b.path).catch(() => {})
}

const severity = (m: M, s?: number) =>
  s === 2 ? m.MarkerSeverity.Warning : s === 3 ? m.MarkerSeverity.Info : s === 4 ? m.MarkerSeverity.Hint : m.MarkerSeverity.Error

async function syncNow(b: Binding) {
  const id = conv()
  const m = monacoRef
  if (!id || !m || b.model.isDisposed() || !usable(id, b.path)) return
  try {
    const r = await bridge.lspSync(id, b.path, b.model.getValue())
    if (!r.ok) return
    const d = await bridge.lspDiagnostics(id, b.path, true)
    if (b.model.isDisposed() || !d.ok) return
    m.editor.setModelMarkers(b.model, 'lsp', (d.diagnostics ?? []).map((x: LspDiagnostic) => ({
      ...toRange(x.range),
      severity: severity(m, x.severity),
      message: x.message,
      source: x.source,
      code: x.code === undefined ? undefined : String(x.code),
    })))
  } catch {
    // 语言服务出错不影响编辑
  }
}

// ---------- 坐标 ----------

/** LSP 的 0 起行列 → Monaco 的 1 起范围 */
function toRange(r: LspRange) {
  return { startLineNumber: r.start.line + 1, startColumn: r.start.character + 1, endLineNumber: r.end.line + 1, endColumn: r.end.character + 1 }
}
const toPos = (p: Monaco.Position) => ({ line: p.lineNumber - 1, character: p.column - 1 })

function bindingOf(model: Monaco.editor.ITextModel) {
  const b = bindings.get(model.uri.toString())
  const id = conv()
  return b && id && usable(id, b.path) ? { b, id } : null
}

async function request<T>(model: Monaco.editor.ITextModel, method: string, params: Record<string, unknown>): Promise<T | null> {
  const hit = bindingOf(model)
  if (!hit) return null
  try {
    const r = await bridge.lspRequest(hit.id, hit.b.path, method, params, model.getValue())
    return r.ok ? ((r.result ?? null) as T) : null
  } catch {
    return null
  }
}

/** 结果里的相对路径 → 本机完整路径（用当前文件的完整路径和相对路径推出工作区根） */
function fullOf(from: Binding, rel: string) {
  const root = from.fullPath.slice(0, from.fullPath.length - from.path.length)
  const sep = from.fullPath.includes('\\') ? '\\' : '/'
  return root + rel.replace(/[\\/]/g, sep)
}

interface LspLocation {
  uri?: string
  path?: string | null
  range?: LspRange
  targetUri?: string
  targetPath?: string | null
  targetRange?: LspRange
  targetSelectionRange?: LspRange
}

/** 定义 / 引用的结果 → Monaco 的位置；没打开的文件建只读预览 model，这样「速览」也能看到内容 */
async function toLocations(m: M, from: Binding, raw: LspLocation | LspLocation[] | null, limit: number): Promise<Monaco.languages.Location[]> {
  const list = raw ? (Array.isArray(raw) ? raw : [raw]) : []
  const out: Monaco.languages.Location[] = []
  const id = conv()
  for (const l of list.slice(0, limit)) {
    const rel = l.targetPath ?? l.path
    const range = l.targetSelectionRange ?? l.targetRange ?? l.range
    if (!rel || !range) continue
    const uri = uriFor(m, fullOf(from, rel))
    if (!m.editor.getModel(uri) && id) {
      try {
        const file = await bridge.loadCode(id, rel)
        if (file.fullPath && !m.editor.getModel(uri)) previews.set(uri.toString(), { model: m.editor.createModel(file.text, languageOf(rel), uri), path: rel })
      } catch {
        continue
      }
    }
    out.push({ uri, range: toRange(range) })
  }
  trimPreviews()
  return out
}

/** 预览 model 最多留 60 个（先建的先释放） */
function trimPreviews() {
  while (previews.size > 60) {
    const [key, p] = previews.entries().next().value as [string, { model: Monaco.editor.ITextModel; path: string }]
    previews.delete(key)
    if (!bindings.has(key)) p.model.dispose()
  }
}

// ---------- 注册到 Monaco ----------

/** LSP 的 CompletionItemKind → Monaco 的 */
function kindOf(m: M, k?: number) {
  const K = m.languages.CompletionItemKind
  const map: Record<number, Monaco.languages.CompletionItemKind> = {
    1: K.Text, 2: K.Method, 3: K.Function, 4: K.Constructor, 5: K.Field, 6: K.Variable, 7: K.Class, 8: K.Interface, 9: K.Module,
    10: K.Property, 11: K.Unit, 12: K.Value, 13: K.Enum, 14: K.Keyword, 15: K.Snippet, 16: K.Color, 17: K.File, 18: K.Reference,
    19: K.Folder, 20: K.EnumMember, 21: K.Constant, 22: K.Struct, 23: K.Event, 24: K.Operator, 25: K.TypeParameter,
  }
  return map[k ?? 1] ?? K.Text
}

const markdown = (v: unknown): Monaco.IMarkdownString[] => {
  const one = (x: unknown): Monaco.IMarkdownString | null =>
    typeof x === 'string' ? { value: x } : x && typeof x === 'object' && 'value' in x
      ? 'language' in x ? { value: '```' + String((x as { language: string }).language) + '\n' + String((x as { value: string }).value) + '\n```' } : { value: String((x as { value: string }).value) }
      : null
  return (Array.isArray(v) ? v.map(one) : [one(v)]).filter((x): x is Monaco.IMarkdownString => !!x && !!x.value.trim())
}

/** 语言服务管的 Monaco 语言（.vue 在 Monaco 里按 html 高亮） */
const LANGUAGES = ['typescript', 'javascript', 'python', 'go', 'java', 'csharp', 'html']
let installed = false

interface LspCompletionItem {
  label: string | { label: string }
  kind?: number
  detail?: string
  documentation?: unknown
  insertText?: string
  insertTextFormat?: number
  textEdit?: { newText: string; range?: LspRange; insert?: LspRange }
  sortText?: string
  filterText?: string
}

export function installLsp(m: M) {
  monacoRef = m
  if (installed) return
  installed = true

  // 报错 / 警告个数：任何文件的标记变了就重新数一遍（语言服务、Monaco 自带的检查都算）
  const recount = () => {
    let errors = 0
    let warnings = 0
    for (const mk of m.editor.getModelMarkers({})) {
      if (mk.severity === m.MarkerSeverity.Error) errors++
      else if (mk.severity === m.MarkerSeverity.Warning) warnings++
    }
    problems.errors = errors
    problems.warnings = warnings
  }
  m.editor.onDidChangeMarkers(recount)
  m.editor.onWillDisposeModel(() => window.setTimeout(recount, 0))

  // 跨文件跳转：打开对应的标签（不是 Monaco 自己的编辑器）
  m.editor.registerEditorOpener({
    openCodeEditor(source, resource, selectionOrPosition) {
      // 目标文件的相对路径：开着的标签或者预览 model 里都记着（不从 URI 反推，各平台的写法不一样）
      const key = resource.toString()
      const rel = bindings.get(key)?.path ?? previews.get(key)?.path
      if (!rel || !bindings.has(source.getModel()?.uri.toString() ?? '')) return false
      const p = selectionOrPosition && 'startLineNumber' in selectionOrPosition
        ? { line: selectionOrPosition.startLineNumber, column: selectionOrPosition.startColumn }
        : selectionOrPosition ? { line: selectionOrPosition.lineNumber, column: selectionOrPosition.column } : { line: 1, column: 1 }
      window.dispatchEvent(new CustomEvent('flyknit:open-code', { detail: { path: rel, line: p.line, column: p.column } }))
      return true
    },
  })

  for (const language of LANGUAGES) {
    m.languages.registerCompletionItemProvider(language, {
      triggerCharacters: ['.', ':', '<', '"', "'", '/', '@', '('],
      async provideCompletionItems(model, position, context) {
        const r = await request<LspCompletionItem[] | { items: LspCompletionItem[]; isIncomplete?: boolean }>(model, 'textDocument/completion', {
          position: toPos(position),
          context: { triggerKind: context.triggerCharacter ? 2 : 1, triggerCharacter: context.triggerCharacter },
        })
        if (!r) return null
        const items = Array.isArray(r) ? r : r.items
        const word = model.getWordUntilPosition(position)
        const fallback = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn }
        return {
          incomplete: !Array.isArray(r) && !!r.isIncomplete,
          suggestions: items.slice(0, 500).map((it) => {
            const label = typeof it.label === 'string' ? it.label : it.label.label
            const edit = it.textEdit
            const range = edit?.range ?? edit?.insert
            return {
              label,
              kind: kindOf(m, it.kind),
              detail: it.detail,
              documentation: it.documentation ? markdown(it.documentation)[0] : undefined,
              insertText: edit?.newText ?? it.insertText ?? label,
              insertTextRules: it.insertTextFormat === 2 ? m.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
              range: range ? toRange(range) : fallback,
              sortText: it.sortText,
              filterText: it.filterText,
            }
          }),
        }
      },
    })

    m.languages.registerHoverProvider(language, {
      async provideHover(model, position) {
        const r = await request<{ contents: unknown; range?: LspRange }>(model, 'textDocument/hover', { position: toPos(position) })
        if (!r?.contents) return null
        const contents = markdown(r.contents)
        return contents.length ? { contents, range: r.range ? toRange(r.range) : undefined } : null
      },
    })

    m.languages.registerSignatureHelpProvider(language, {
      signatureHelpTriggerCharacters: ['(', ','],
      async provideSignatureHelp(model, position) {
        const r = await request<{ signatures: { label: string; documentation?: unknown; parameters?: { label: string | [number, number]; documentation?: unknown }[] }[]; activeSignature?: number; activeParameter?: number }>(
          model, 'textDocument/signatureHelp', { position: toPos(position) })
        if (!r?.signatures?.length) return null
        return {
          value: {
            signatures: r.signatures.map((s) => ({
              label: s.label,
              documentation: s.documentation ? markdown(s.documentation)[0] : undefined,
              parameters: (s.parameters ?? []).map((p) => ({ label: p.label, documentation: p.documentation ? markdown(p.documentation)[0] : undefined })),
            })),
            activeSignature: r.activeSignature ?? 0,
            activeParameter: r.activeParameter ?? 0,
          },
          dispose() {},
        }
      },
    })

    m.languages.registerDefinitionProvider(language, {
      async provideDefinition(model, position) {
        const hit = bindingOf(model)
        if (!hit) return null
        return toLocations(m, hit.b, await request<LspLocation[]>(model, 'textDocument/definition', { position: toPos(position) }), 20)
      },
    })

    m.languages.registerReferenceProvider(language, {
      async provideReferences(model, position) {
        const hit = bindingOf(model)
        if (!hit) return null
        return toLocations(m, hit.b, await request<LspLocation[]>(model, 'textDocument/references', { position: toPos(position), context: { includeDeclaration: true } }), 200)
      },
    })

    m.languages.registerDocumentSymbolProvider(language, {
      async provideDocumentSymbols(model) {
        interface Sym { name: string; detail?: string; kind: number; range?: LspRange; selectionRange?: LspRange; location?: { range: LspRange }; children?: Sym[] }
        const r = await request<Sym[]>(model, 'textDocument/documentSymbol', {})
        if (!r) return null
        const conv = (s: Sym): Monaco.languages.DocumentSymbol => {
          const range = toRange(s.range ?? s.location!.range)
          return {
            name: s.name,
            detail: s.detail ?? '',
            kind: Math.max(0, (s.kind ?? 13) - 1) as Monaco.languages.SymbolKind,
            tags: [],
            range,
            selectionRange: s.selectionRange ? toRange(s.selectionRange) : range,
            children: (s.children ?? []).map(conv),
          }
        }
        return r.filter((s) => s.range || s.location).map(conv)
      },
    })

    m.languages.registerRenameProvider(language, {
      async provideRenameEdits(model, position, newName) {
        const hit = bindingOf(model)
        if (!hit) return null
        interface Edit { range: LspRange; newText: string }
        const r = await request<{ changesByPath?: { path: string | null; edits: Edit[] }[]; documentChanges?: { textDocument?: { path?: string | null }; edits?: Edit[] }[] }>(
          model, 'textDocument/rename', { position: toPos(position), newName })
        if (!r) return { edits: [], rejectReason: '这里不能重命名' }
        const groups = new Map<string, Edit[]>()
        for (const c of r.changesByPath ?? []) if (c.path) groups.set(c.path, [...(groups.get(c.path) ?? []), ...c.edits])
        for (const c of r.documentChanges ?? []) if (c.textDocument?.path && c.edits) groups.set(c.textDocument.path, [...(groups.get(c.textDocument.path) ?? []), ...c.edits])
        // 打开着的文件在编辑器里改（员工再保存）；没打开的由宿主直接改磁盘上的文件
        const edits: Monaco.languages.IWorkspaceTextEdit[] = []
        const onDisk: { path: string; edits: Edit[] }[] = []
        for (const [rel, list] of groups) {
          const uri = uriFor(m, fullOf(hit.b, rel))
          if (bindings.has(uri.toString())) for (const e of list) edits.push({ resource: uri, versionId: undefined, textEdit: { range: toRange(e.range), text: e.newText } })
          else onDisk.push({ path: rel, edits: list })
        }
        if (onDisk.length) {
          const w = await bridge.lspApplyEdit(hit.id, onDisk)
          if (!w.ok) return { edits: [], rejectReason: w.error || '重命名失败' }
          for (const p of onDisk) previews.get(uriFor(m, fullOf(hit.b, p.path)).toString())?.model.dispose()
          window.dispatchEvent(new CustomEvent('flyknit:files-changed', { detail: { paths: w.changed ?? [] } }))
          toast(i18n.global.t('ui.code.lsp.renamedOnDisk', { n: onDisk.length }))
        }
        return { edits }
      },
    })
  }
}
