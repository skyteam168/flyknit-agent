/**
 * Monaco（VS Code 的编辑器内核，MIT）按需加载：第一次打开「代码」标签时才下载，不拖慢主界面启动。
 *
 * 只要编辑器本体 + 常见语言的语法高亮（monarch 分词，不用额外的语言服务 worker），产物小得多；
 * 不带 TypeScript / JSON / CSS / HTML 的语言服务（智能提示、报错）——写代码靠 AI，这里主要是看和小改。
 */
import type * as Monaco from 'monaco-editor/esm/vs/editor/editor.api'

let loading: Promise<typeof Monaco> | null = null

export function loadMonaco(): Promise<typeof Monaco> {
  loading ??= (async () => {
    const [monaco, { default: EditorWorker }] = await Promise.all([
      import('monaco-editor/esm/vs/editor/editor.api'),
      import('monaco-editor/esm/vs/editor/editor.worker?worker'),
      // 编辑器的常用功能（查找替换、折叠、多光标、括号匹配……）
      import('monaco-editor/esm/vs/editor/edcore.main'),
      // 语法高亮：工厂 IT 常见的语言
      import('monaco-editor/esm/vs/basic-languages/python/python.contribution'),
      import('monaco-editor/esm/vs/basic-languages/javascript/javascript.contribution'),
      import('monaco-editor/esm/vs/basic-languages/typescript/typescript.contribution'),
      import('monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution'),
      import('monaco-editor/esm/vs/basic-languages/java/java.contribution'),
      import('monaco-editor/esm/vs/basic-languages/go/go.contribution'),
      import('monaco-editor/esm/vs/basic-languages/cpp/cpp.contribution'),
      import('monaco-editor/esm/vs/basic-languages/sql/sql.contribution'),
      import('monaco-editor/esm/vs/basic-languages/html/html.contribution'),
      import('monaco-editor/esm/vs/basic-languages/xml/xml.contribution'),
      import('monaco-editor/esm/vs/basic-languages/css/css.contribution'),
      import('monaco-editor/esm/vs/basic-languages/scss/scss.contribution'),
      import('monaco-editor/esm/vs/basic-languages/markdown/markdown.contribution'),
      import('monaco-editor/esm/vs/basic-languages/yaml/yaml.contribution'),
      import('monaco-editor/esm/vs/basic-languages/ini/ini.contribution'),
      import('monaco-editor/esm/vs/basic-languages/powershell/powershell.contribution'),
      import('monaco-editor/esm/vs/basic-languages/bat/bat.contribution'),
      import('monaco-editor/esm/vs/basic-languages/shell/shell.contribution'),
      import('monaco-editor/esm/vs/basic-languages/vb/vb.contribution'),
      import('monaco-editor/esm/vs/basic-languages/php/php.contribution'),
      import('monaco-editor/esm/vs/basic-languages/rust/rust.contribution'),
      import('monaco-editor/esm/vs/basic-languages/kotlin/kotlin.contribution'),
      import('monaco-editor/esm/vs/basic-languages/dockerfile/dockerfile.contribution'),
    ])
    ;(self as unknown as { MonacoEnvironment: unknown }).MonacoEnvironment = { getWorker: () => new EditorWorker() }
    return monaco
  })()
  return loading
}

/** 扩展名 → Monaco 的语言 id；不认识的按纯文本 */
const byExtension: Record<string, string> = {
  py: 'python', pyw: 'python',
  js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript', json: 'javascript', jsonc: 'javascript',
  ts: 'typescript', tsx: 'typescript', mts: 'typescript', vue: 'html',
  cs: 'csharp', csx: 'csharp',
  java: 'java', go: 'go', kt: 'kotlin', kts: 'kotlin', rs: 'rust', php: 'php',
  c: 'cpp', h: 'cpp', cpp: 'cpp', cc: 'cpp', hpp: 'cpp',
  sql: 'sql',
  html: 'html', htm: 'html', cshtml: 'html',
  xml: 'xml', xaml: 'xml', csproj: 'xml', props: 'xml', targets: 'xml', config: 'xml', resx: 'xml', svg: 'xml',
  css: 'css', scss: 'scss', less: 'scss',
  md: 'markdown', markdown: 'markdown',
  yml: 'yaml', yaml: 'yaml',
  ini: 'ini', cfg: 'ini', toml: 'ini', env: 'ini', editorconfig: 'ini', gitignore: 'ini',
  ps1: 'powershell', psm1: 'powershell', psd1: 'powershell',
  bat: 'bat', cmd: 'bat',
  sh: 'shell', bash: 'shell',
  vb: 'vb', vbs: 'vb', bas: 'vb',
}

export function languageOf(path: string): string {
  const name = path.split(/[\\/]/).pop()?.toLowerCase() ?? ''
  if (name === 'dockerfile') return 'dockerfile'
  const ext = name.includes('.') ? name.split('.').pop()! : name.replace(/^\./, '')
  return byExtension[ext] ?? 'plaintext'
}
