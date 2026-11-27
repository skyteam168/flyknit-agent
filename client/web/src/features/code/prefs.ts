/**
 * 编码模式的编辑器外观（像 VS Code 的「颜色主题」「字号」「字重」）：主题、字号、粗细、字体、行高、自动换行、缩略图。
 * 每个员工自己的偏好，记在本机；改了马上生效（编辑器、对比视图、底部终端的字号一起变）。
 */
import { reactive, watch } from 'vue'
import type * as Monaco from 'monaco-editor/esm/vs/editor/editor.api'

/** auto = 跟着界面的深浅色；其余是 Monaco 自带的四个 + 我们定义的两个 */
export const EDITOR_THEMES = ['auto', 'vs-dark', 'vs', 'hc-black', 'hc-light', 'flyknit-night', 'flyknit-eye'] as const
export type EditorTheme = (typeof EDITOR_THEMES)[number]

/** 字体：员工电脑上没装的会自动退回 Consolas */
export const EDITOR_FONTS = [
  { id: 'cascadia', css: "'Cascadia Code', Consolas, 'Courier New', monospace" },
  { id: 'consolas', css: "Consolas, 'Courier New', monospace" },
  { id: 'jetbrains', css: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace" },
  { id: 'fira', css: "'Fira Code', 'Cascadia Code', Consolas, monospace" },
  { id: 'sarasa', css: "'Sarasa Mono SC', 'Microsoft YaHei Mono', 'Cascadia Code', Consolas, monospace" },
] as const

export const FONT_WEIGHTS = ['300', 'normal', '500', '600', 'bold'] as const

export interface EditorPrefs {
  theme: EditorTheme
  fontSize: number
  fontWeight: (typeof FONT_WEIGHTS)[number]
  font: (typeof EDITOR_FONTS)[number]['id']
  /** 0 = 跟着字号自动 */
  lineHeight: number
  wordWrap: boolean
  minimap: boolean
  /** 连字（=> != 显示成一个符号，Fira Code / Cascadia Code 等字体才有） */
  ligatures: boolean
}

const Key = 'flyknit.ide.editor'
const defaults: EditorPrefs = { theme: 'auto', fontSize: 13, fontWeight: 'normal', font: 'cascadia', lineHeight: 0, wordWrap: false, minimap: false, ligatures: false }

export const clampFont = (n: number) => Math.min(Math.max(Math.round(n), 9), 32)

function read(): EditorPrefs {
  try {
    const raw = JSON.parse(localStorage.getItem(Key) ?? '{}')
    const p = { ...defaults, ...(raw && typeof raw === 'object' ? raw : {}) } as EditorPrefs
    if (!EDITOR_THEMES.includes(p.theme)) p.theme = defaults.theme
    if (!FONT_WEIGHTS.includes(p.fontWeight)) p.fontWeight = defaults.fontWeight
    if (!EDITOR_FONTS.some((f) => f.id === p.font)) p.font = defaults.font
    p.fontSize = clampFont(Number(p.fontSize) || defaults.fontSize)
    p.lineHeight = Math.min(Math.max(Number(p.lineHeight) || 0, 0), 48)
    return p
  } catch {
    return { ...defaults }
  }
}

export const editorPrefs = reactive<EditorPrefs>(read())

watch(
  () => ({ ...editorPrefs }),
  (v) => {
    try {
      localStorage.setItem(Key, JSON.stringify(v))
    } catch {
      // 存不了就只在这次打开期间记住
    }
  },
  { deep: true },
)

export function resetEditorPrefs() {
  Object.assign(editorPrefs, defaults)
}

export const fontCss = () => EDITOR_FONTS.find((f) => f.id === editorPrefs.font)?.css ?? EDITOR_FONTS[0].css

/** 现在该用的 Monaco 主题名 */
export function themeName(dark: boolean): string {
  return editorPrefs.theme === 'auto' ? (dark ? 'vs-dark' : 'vs') : editorPrefs.theme
}

/** 编辑器（和对比视图）跟偏好有关的选项 */
export function prefOptions(): Monaco.editor.IEditorOptions {
  return {
    fontSize: editorPrefs.fontSize,
    fontWeight: editorPrefs.fontWeight,
    fontFamily: fontCss(),
    fontLigatures: editorPrefs.ligatures,
    lineHeight: editorPrefs.lineHeight || 0,
    minimap: { enabled: editorPrefs.minimap },
  }
}

let defined = false
/** 我们自己的两个主题：夜间蓝（深色、对比柔和）、护眼（浅绿底） */
export function defineThemes(m: typeof Monaco) {
  if (defined) return
  defined = true
  m.editor.defineTheme('flyknit-night', {
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: 'comment', foreground: '6b7a99', fontStyle: 'italic' },
      { token: 'keyword', foreground: 'c792ea' },
      { token: 'string', foreground: 'c3e88d' },
      { token: 'number', foreground: 'f78c6c' },
      { token: 'type', foreground: 'ffcb6b' },
    ],
    colors: {
      'editor.background': '#1b2233',
      'editor.lineHighlightBackground': '#232c42',
      'editorLineNumber.foreground': '#4b5878',
      'editor.selectionBackground': '#33415f',
    },
  })
  m.editor.defineTheme('flyknit-eye', {
    base: 'vs',
    inherit: true,
    rules: [
      { token: 'comment', foreground: '6a8a6a', fontStyle: 'italic' },
      { token: 'keyword', foreground: '2f5fa8' },
      { token: 'string', foreground: 'a0522d' },
    ],
    colors: {
      'editor.background': '#e9f2e4',
      'editor.lineHighlightBackground': '#dde9d6',
      'editorLineNumber.foreground': '#8aa38a',
      'editor.selectionBackground': '#c6dbbd',
    },
  })
}
