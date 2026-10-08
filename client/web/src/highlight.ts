import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import c from 'highlight.js/lib/languages/c'
import cpp from 'highlight.js/lib/languages/cpp'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import dos from 'highlight.js/lib/languages/dos'
import go from 'highlight.js/lib/languages/go'
import ini from 'highlight.js/lib/languages/ini'
import java from 'highlight.js/lib/languages/java'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import php from 'highlight.js/lib/languages/php'
import powershell from 'highlight.js/lib/languages/powershell'
import python from 'highlight.js/lib/languages/python'
import ruby from 'highlight.js/lib/languages/ruby'
import rust from 'highlight.js/lib/languages/rust'
import scss from 'highlight.js/lib/languages/scss'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import vbnet from 'highlight.js/lib/languages/vbnet'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'

// 代码高亮：聊天里的代码块、右侧文件预览、资料库查看器共用这一份。
// 只注册办公场景常见的语言（全量 highlight.js 有几百 KB），加一种语言 = 这里加一行 import + 一行注册。
const languages = {
  bash, c, cpp, csharp, css, dos, go, ini, java, javascript, json, markdown, php, powershell,
  python, ruby, rust, scss, sql, typescript, vbnet, xml, yaml,
}
for (const [name, lang] of Object.entries(languages)) hljs.registerLanguage(name, lang)

// 宿主（TextPreviewProvider.Languages）和模型写代码块时用的名字 → highlight.js 的名字
const aliases: Record<string, string> = {
  html: 'xml', htm: 'xml', vue: 'xml', svg: 'xml',
  js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript',
  ts: 'typescript', tsx: 'typescript',
  sh: 'bash', shell: 'bash', zsh: 'bash', ps1: 'powershell', pwsh: 'powershell',
  bat: 'dos', cmd: 'dos', batch: 'dos',
  py: 'python', cs: 'csharp', 'c#': 'csharp', vb: 'vbnet',
  yml: 'yaml', toml: 'ini', conf: 'ini', cfg: 'ini', env: 'ini',
  md: 'markdown', rs: 'rust', rb: 'ruby', h: 'c', hpp: 'cpp', 'c++': 'cpp',
}

/** 超过这个长度不高亮：几十万字的日志高亮一次要卡好几秒，看不出区别 */
export const MAX_HIGHLIGHT_CHARS = 200_000

export function resolveLanguage(lang: string | null | undefined): string | null {
  const key = (lang ?? '').trim().toLowerCase()
  if (!key) return null
  const name = aliases[key] ?? key
  return hljs.getLanguage(name) ? name : null
}

export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

/** 高亮后的 HTML（已转义，可以直接 v-html）。认不出语言或者太长就只转义 */
export function highlightCode(code: string, lang: string | null | undefined): string {
  const language = resolveLanguage(lang)
  if (!language || code.length > MAX_HIGHLIGHT_CHARS) return escapeHtml(code)
  try {
    return hljs.highlight(code, { language, ignoreIllegals: true }).value
  } catch {
    return escapeHtml(code)
  }
}

/**
 * 按行拆开的高亮结果，给带行号的查看器用。跨行的 token（多行注释、字符串）在行尾补上闭合标签、
 * 下一行开头重新打开，保证每行都是完整的 HTML。
 */
export function highlightLines(code: string, lang: string | null | undefined): string[] {
  const html = highlightCode(code, lang)
  const out: string[] = []
  let open: string[] = []
  for (const line of html.split('\n')) {
    const prefix = open.join('')
    for (const tag of line.match(/<span[^>]*>|<\/span>/g) ?? []) {
      if (tag === '</span>') open.pop()
      else open.push(tag)
    }
    out.push(prefix + line + '</span>'.repeat(open.length))
  }
  return out
}

export { hljs }
